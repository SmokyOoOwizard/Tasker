//! Открытая на время команды рабочая область (`Session` в .NET): папка с `.tasker` — проверка, создание служебных папок и
//! индекс ([`WorkspaceIndex`]); выбор проекта из `-p`, `TASKER_PROJECT` или единственного в области; поиск сущностей по id,
//! имени и короткому id (`Refs`). Область в файле SQLite (`--sqlite`) — та же БД, что у .NET-десктопа и сервера (TSK-159):
//! файла нет — он создаётся со схемой EF Core.
use crate::errors::{CliError, Result};
use std::path::Path;
use tasker_core::ShortId;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::model::Project;
use tasker_core::settings::WorkspaceKind;
use tasker_core::settings::WorkspaceLocation;
use tasker_core::settings::expand_user_path;
use tasker_core::tasks::Page;
use tasker_core::validate::to_upper_invariant;
use tasker_files::layout::TaskerDirectory;
use tasker_services::{Storage, Workspace};
use uuid::Uuid;

pub struct Session {
    location: WorkspaceLocation,
    workspace: Workspace,
}

impl Session {
    /// Рабочая область из параметров: файл SQLite или папка (по умолчанию текущая). Папка должна существовать.
    pub fn locate(folder: Option<&str>, sqlite: Option<&str>) -> Result<WorkspaceLocation> {
        if folder.is_some() && sqlite.is_some() {
            return Err(CliError::new("Use either --workspace or --sqlite, not both"));
        }
        if let Some(file) = sqlite {
            return Ok(WorkspaceLocation::sqlite(&expand_user_path(file)));
        }
        let folder = match folder {
            Some(folder) => expand_user_path(folder),
            None => std::env::current_dir()?.to_string_lossy().into_owned(),
        };
        let location = WorkspaceLocation::files(&folder);
        if !Path::new(location.path()).is_dir() {
            return Err(CliError::new(format!("Folder not found: {}", location.path())));
        }
        Ok(location)
    }

    /// Есть ли область: у папки — `.tasker/projects`, у SQLite — сам файл.
    pub fn exists(location: &WorkspaceLocation) -> bool {
        match location.kind() {
            WorkspaceKind::Files => TaskerDirectory::new(location.path()).projects().is_dir(),
            WorkspaceKind::Sqlite => Path::new(location.path()).is_file(),
        }
    }

    /// Открывает область. `for_completion` — только чтение существующей области (справка, автодополнение): ничего не создаётся,
    /// нет области — ошибка `No workspace at …`. Иначе служебные папки и `.gitignore` дописываются, как `FileStorageModule`.
    pub fn open(folder: Option<&str>, sqlite: Option<&str>, for_completion: bool) -> Result<Session> {
        let location = Self::locate(folder, sqlite)?;
        if for_completion && !Self::exists(&location) {
            return Err(CliError::new(format!("No workspace at {}", location.path())));
        }
        // Консоль — отдельный держатель блокировок правки («<имя> (console)», ключ cli): блокировка интерфейса останавливает и команды.
        let workspace = match (location.kind(), for_completion) {
            (WorkspaceKind::Sqlite, _) => Workspace::open_sqlite(location.path())?,
            (WorkspaceKind::Files, true) => Workspace::attach(location.path())?,
            (WorkspaceKind::Files, false) => Workspace::open(location.path())?,
        }
        .with_editor(tasker_services::locks::console_editor(&editor_name()));
        Ok(Session { location, workspace })
    }

    /// Имя держателя блокировок консоли: имя из глобальных настроек (`tasker whoami <name>`), иначе имя пользователя ОС
    /// (`SettingsLocalEditor.Name`); нечитаемые настройки — как отсутствующие.
    pub fn editor_name() -> String {
        editor_name()
    }

    pub fn location(&self) -> &WorkspaceLocation {
        &self.location
    }

    pub fn workspace(&self) -> &Workspace {
        &self.workspace
    }

    /// Все проекты области (по имени).
    pub fn all_projects(&self) -> Result<Vec<Project>> {
        Ok(self.workspace.all_projects()?)
    }

    /// Проект по id или имени.
    pub fn find_project(&self, reference: &str) -> Result<Project> {
        let projects = self.all_projects()?;
        refs::find_readable(
            &projects,
            reference,
            |p| p.id,
            |p| &p.name,
            "project",
            false,
            || Ok(self.workspace.unreadable_project_reference(reference)?),
        )
        .cloned()
    }

    /// Проект из `--project` (id или имя). Параметр можно не указывать, если в рабочей области ровно один проект.
    /// Проектов нет или несколько, а параметра нет — ошибка с подсказкой.
    pub fn project_id(&self, reference: Option<&str>) -> Result<Uuid> {
        if let Some(reference) = reference.filter(|r| !r.trim().is_empty()) {
            return Ok(self.find_project(reference)?.id);
        }
        // Достаточно двух: нужно знать только «один ли».
        let page = self.workspace.project_range(None, Page::first(2))?;
        match page.total_count {
            1 => Ok(page.data[0].id),
            0 => Err(CliError::new(
                "There are no projects in this workspace: create one with 'tasker project create <name>'",
            )),
            count => Err(CliError::new(format!(
                "Project is required: this workspace has {count} projects, use --project <id or name> or set TASKER_PROJECT"
            ))),
        }
    }

    /// Проект из `--project`; None — не указан (команда работает со всеми проектами).
    pub fn optional_project(&self, reference: Option<&str>) -> Result<Option<Project>> {
        match reference.filter(|r| !r.trim().is_empty()) {
            Some(reference) => Ok(Some(self.find_project(reference)?)),
            None => Ok(None),
        }
    }
}

fn editor_name() -> String {
    let configured = tasker_core::settings::SettingsStore::new(None)
        .load()
        .ok()
        .and_then(|s| s.user_name)
        .filter(|n| !n.trim().is_empty());
    configured.unwrap_or_else(tasker_core::settings::os_user_name)
}

/// Ссылки на сущности в командах (`Refs` в .NET): полный Guid, имя, затем короткий id — префикс Guid из 8 и более
/// шестнадцатеричных символов (регистр не важен). Точное имя сильнее префикса.
pub mod refs {
    use super::*;

    pub fn find<T>(items: &[T], reference: &str, id: impl Fn(&T) -> Uuid, name: impl Fn(&T) -> &str, kind: &str) -> Result<Uuid> {
        find_item(items, reference, &id, name, kind, false).map(id)
    }

    /// Сущность по ссылке; `list_available` — в ошибке «нет такой» перечислить имена всех.
    pub fn find_item<'a, T>(
        items: &'a [T],
        reference: &str,
        id: impl Fn(&T) -> Uuid,
        name: impl Fn(&T) -> &str,
        kind: &str,
        list_available: bool,
    ) -> Result<&'a T> {
        find_readable(items, reference, id, name, kind, list_available, || Ok(()))
    }

    /// Как [`find_item`] над прочитанными сущностями хранилища: прежде чем ответить «нет такой», `unreadable` проверяет, не
    /// указывает ли ссылка (id или короткий id) на файл, который не удалось прочитать, — тогда ошибка его чтения.
    pub fn find_readable<'a, T>(
        items: &'a [T],
        reference: &str,
        id: impl Fn(&T) -> Uuid,
        name: impl Fn(&T) -> &str,
        kind: &str,
        list_available: bool,
        unreadable: impl FnOnce() -> Result<()>,
    ) -> Result<&'a T> {
        if let Some(guid) = parse_guid(reference)
            && let Some(by_id) = items.iter().find(|x| id(x) == guid)
        {
            return Ok(by_id);
        }

        let wanted = to_upper_invariant(reference);
        let by_name: Vec<&T> = items.iter().filter(|x| to_upper_invariant(name(x)) == wanted).collect();
        if by_name.len() > 1 {
            return Err(CliError::new(format!(
                "Several {kind}s are named '{reference}', use the id: {}",
                by_name.iter().map(|x| guid_d(&id(x))).collect::<Vec<_>>().join(", ")
            )));
        }
        if let Some(found) = by_name.first() {
            return Ok(found);
        }

        if let Some(found) = by_id_prefix(items, reference, &id, kind)? {
            return Ok(found);
        }
        unreadable()?;
        let available = if list_available {
            let mut names: Vec<&str> = items.iter().map(name).collect();
            names.sort_by_key(|n| to_upper_invariant(n));
            format!(". Available: {}", names.join(", "))
        } else {
            String::new()
        };
        Err(CliError::new(format!("No {kind} '{reference}'{available}")))
    }

    /// Сущность по короткому id; None — текст не префикс id или такой сущности нет. Префиксу подходят несколько — ошибка с их id.
    pub fn by_id_prefix<'a, T>(items: &'a [T], reference: &str, id: impl Fn(&T) -> Uuid, kind: &str) -> Result<Option<&'a T>> {
        let Some(key) = ShortId::try_key(Some(reference)) else {
            return Ok(None);
        };
        let found: Vec<&T> = items.iter().filter(|x| ShortId::matches(&id(x), &key)).collect();
        match found.len() {
            0 => Ok(None),
            1 => Ok(Some(found[0])),
            _ => Err(CliError::new(format!(
                "Several {kind}s start with '{}', use a longer prefix or the full id: {}",
                reference.trim(),
                found.iter().map(|x| guid_d(&id(x))).collect::<Vec<_>>().join(", ")
            ))),
        }
    }

    pub fn find_all<T>(
        items: &[T],
        references: &[String],
        id: impl Fn(&T) -> Uuid,
        name: impl Fn(&T) -> &str,
        kind: &str,
    ) -> Result<Vec<Uuid>> {
        references.iter().map(|r| find(items, r, &id, &name, kind)).collect()
    }

    /// Значения одного параметра-фильтра («ИЛИ»): каждое — id, имя или короткий id; повторы убираются.
    /// Неизвестное значение — ошибка с перечнем допустимых.
    pub fn find_distinct<T>(
        items: &[T],
        references: &[String],
        id: impl Fn(&T) -> Uuid,
        name: impl Fn(&T) -> &str,
        kind: &str,
    ) -> Result<Vec<Uuid>> {
        let mut ids = Vec::new();
        for reference in references {
            let found = id(find_item(items, reference, &id, &name, kind, true)?);
            if !ids.contains(&found) {
                ids.push(found);
            }
        }
        Ok(ids)
    }
}

#[cfg(test)]
mod tests {
    use super::refs::*;
    use super::*;

    #[derive(Debug)]
    struct Item(Uuid, &'static str);

    fn items() -> Vec<Item> {
        vec![
            Item(Uuid::parse_str("11111111-0000-4000-8000-000000000001").unwrap(), "Golden"),
            Item(Uuid::parse_str("11111111-0000-4000-8000-000000000002").unwrap(), "Legacy"),
            Item(Uuid::parse_str("22222222-0000-4000-8000-000000000003").unwrap(), "deadbeef"),
            Item(Uuid::parse_str("deadbeef-0000-4000-8000-000000000004").unwrap(), "Other"),
        ]
    }

    fn find_ref(reference: &str) -> Result<&'static str> {
        let all = items();
        find_item(&all, reference, |x| x.0, |x| x.1, "project", false).map(|x| x.1)
    }

    #[test]
    fn id_then_name_then_prefix() {
        assert_eq!(find_ref("11111111-0000-4000-8000-000000000002").unwrap(), "Legacy");
        assert_eq!(find_ref("golden").unwrap(), "Golden");
        assert_eq!(find_ref("deadbeef").unwrap(), "deadbeef"); // имя сильнее префикса
        assert_eq!(find_ref("22222222").unwrap(), "deadbeef");
        assert_eq!(find_ref("Nope").unwrap_err().text(), "Error: No project 'Nope'");
        assert_eq!(
            find_ref("11111111").unwrap_err().text(),
            "Error: Several projects start with '11111111', use a longer prefix or the full id: 11111111-0000-4000-8000-000000000001, 11111111-0000-4000-8000-000000000002"
        );
    }

    #[test]
    fn a_prefix_ignores_case_needs_eight_characters_and_a_longer_one_disambiguates() {
        assert_eq!(find_ref("DEADBEEF0000").unwrap(), "Other");
        assert_eq!(find_ref("1111111").unwrap_err().text(), "Error: No project '1111111'");
        assert_eq!(find_ref("33333333").unwrap_err().text(), "Error: No project '33333333'");
        assert!(
            find_ref("111111110000")
                .unwrap_err()
                .text()
                .contains("Several projects start with '111111110000'")
        );
        assert_eq!(find_ref("11111111000040008000000000000002").unwrap(), "Legacy");
    }

    #[test]
    fn available_names_are_listed_when_asked() {
        let all = items();
        let error = find_item(&all, "x", |x| x.0, |x| x.1, "status", true).unwrap_err();
        assert_eq!(error.text(), "Error: No status 'x'. Available: deadbeef, Golden, Legacy, Other");
    }

    #[test]
    fn both_locations_and_a_missing_folder_are_errors() {
        assert_eq!(
            Session::locate(Some("a"), Some("b")).unwrap_err().text(),
            "Error: Use either --workspace or --sqlite, not both"
        );
        let file = Session::locate(None, Some("x.db")).unwrap();
        assert_eq!(file.kind(), WorkspaceKind::Sqlite);
        assert!(file.path().ends_with("x.db"));
        assert!(
            Session::locate(Some("/nonexistent/nowhere"), None)
                .unwrap_err()
                .text()
                .starts_with("Error: Folder not found: ")
        );
    }
}
