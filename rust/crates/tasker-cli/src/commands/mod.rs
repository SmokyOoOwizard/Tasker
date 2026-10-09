//! Команды консоли поверх сервисов (`tasker-services`): пока `sync` и `cleanup` — средство проверки паритета сервисов на реальной
//! области. Общее: открытие области ([`open`]), проект по `-p` ([`find_project`]), вывод JSON по конвенциям `TaskerJson`.
pub mod cleanup;
pub mod sync;

use crate::CliError;
use clap::ArgMatches;
use serde_json::{Map, Value};
use tasker_core::ShortId;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::model::Project;
use tasker_core::validate::eq_ignore_case;
use tasker_services::Workspace;
use uuid::Uuid;

/// Открывает область по `-w` (как `Session.Open`): `.tasker` создаётся, индекс сверяется с файлами.
pub(crate) fn open(matches: &ArgMatches) -> Result<Workspace, CliError> {
    let location = crate::locate_folder(matches)?;
    let name = tasker_core::settings::os_user_name();
    Ok(Workspace::open(location.path())?.with_editor(tasker_services::locks::console_editor(&name)))
}

/// Все проекты области по имени (`Context.AllProjects`).
pub(crate) fn all_projects(ws: &Workspace) -> Result<Vec<Project>, CliError> {
    Ok(ws.projects().get_all()?)
}

/// Проект из `-p` или `TASKER_PROJECT`; None — не указан (команда работает со всеми проектами).
pub(crate) fn optional_project(ws: &Workspace, matches: &ArgMatches) -> Result<Option<Project>, CliError> {
    let reference = matches
        .get_one::<String>("project")
        .cloned()
        .or_else(|| std::env::var("TASKER_PROJECT").ok())
        .filter(|r| !r.trim().is_empty());
    match reference {
        None => Ok(None),
        Some(reference) => Ok(Some(find_project(ws, &reference)?)),
    }
}

/// Проект по id, имени (без учёта регистра) или префиксу id (`Refs.FindItem`).
pub(crate) fn find_project(ws: &Workspace, reference: &str) -> Result<Project, CliError> {
    let all = all_projects(ws)?;
    if let Some(id) = parse_guid(reference)
        && let Some(found) = all.iter().find(|x| x.id == id)
    {
        return Ok(found.clone());
    }
    let by_name: Vec<&Project> = all.iter().filter(|x| eq_ignore_case(&x.name, reference)).collect();
    if by_name.len() > 1 {
        return Err(CliError(format!(
            "Several projects are named '{reference}', use the id: {}",
            by_name.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
        )));
    }
    if let Some(found) = by_name.first() {
        return Ok((*found).clone());
    }
    if let Some(key) = ShortId::try_key(Some(reference)) {
        let found: Vec<&Project> = all.iter().filter(|x| ShortId::matches(&x.id, &key)).collect();
        match found.len() {
            0 => {}
            1 => return Ok(found[0].clone()),
            _ => {
                return Err(CliError(format!(
                    "Several projects start with '{}', use a longer prefix or the full id: {}",
                    reference.trim(),
                    found.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
                )));
            }
        }
    }
    Err(CliError(format!("No project '{reference}'")))
}

/// JSON-объект с полями в заданном порядке (анонимные объекты .NET сериализуются в порядке объявления).
pub(crate) fn object(pairs: Vec<(&str, Value)>) -> Value {
    Value::Object(pairs.into_iter().map(|(k, v)| (k.to_string(), v)).collect::<Map<_, _>>())
}

pub(crate) fn id(id: &Uuid) -> Value {
    Value::String(guid_d(id))
}

pub(crate) fn ids(list: &[Uuid]) -> Value {
    Value::Array(list.iter().map(id).collect())
}

pub(crate) fn opt_id(id: Option<&Uuid>) -> Value {
    id.map(self::id).unwrap_or(Value::Null)
}

pub(crate) fn opt_str(text: Option<&str>) -> Value {
    text.map(|t| Value::String(t.to_string())).unwrap_or(Value::Null)
}
