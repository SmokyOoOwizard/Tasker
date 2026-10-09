//! Подсказки значений в справке (`<R&D|Баг|Фича>`): System.CommandLine берёт их из источников автодополнения (`Sources` в .NET),
//! которые открывают текущую область (`-w` или текущая папка; проект — `-p`, `TASKER_PROJECT` или единственный) только если она
//! есть, и молчат при любой ошибке — тогда подсказки нет. Область открывается один раз на вызов справки, лениво.
use crate::session::{Session, refs};
use crate::spec::Hint;
use std::cell::OnceCell;
use tasker_core::model::{FieldDefinition, FieldEnum, LinkType, Series, Status, StatusSet, TaskType};
use tasker_files::index::{IndexEntity, IndexQuery};
use uuid::Uuid;

/// Ключи сортировки задач, известные без полей (`TaskSortNames.BuiltinNames`).
pub const BUILTIN_SORT_NAMES: [&str; 6] = ["status", "type", "title", "created", "updated", "series"];

pub struct Hints {
    folder: Option<String>,
    sqlite: Option<String>,
    project: Option<String>,
    /// Типы задач, уже набранные в `--type`: статусы подсказываются только из их наборов (`Sources.TaskStatuses`).
    type_references: Vec<String>,
    session: OnceCell<Option<Session>>,
}

impl Hints {
    pub fn new(folder: Option<String>, sqlite: Option<String>, project: Option<String>, type_references: Vec<String>) -> Hints {
        Hints {
            folder,
            sqlite,
            project: project.or_else(|| std::env::var("TASKER_PROJECT").ok()),
            type_references,
            session: OnceCell::new(),
        }
    }

    /// Подсказки по сырой строке аргументов — для справки при ошибке разбора, когда разобранных значений нет: `-w`/`--workspace`,
    /// `--sqlite`, `-p`/`--project` и `--type` берутся из слов строки (`--name value` или `--name=value`).
    pub fn from_args(args: &[String]) -> Hints {
        let mut folder = None;
        let mut sqlite = None;
        let mut project = None;
        let mut type_references = Vec::new();
        let mut i = 0;
        while i < args.len() {
            let token = &args[i];
            if token == "--" {
                break;
            }
            let (name, inline) = match token.split_once(['=', ':']) {
                Some((name, value)) if name.starts_with('-') => (name, Some(value.to_string())),
                _ => (token.as_str(), None),
            };
            let target = match name {
                "-w" | "--workspace" => Some(&mut folder),
                "--sqlite" => Some(&mut sqlite),
                "-p" | "--project" => Some(&mut project),
                _ => None,
            };
            let takes_value = target.is_some() || name == "--type";
            let value = match inline {
                Some(value) => Some(value),
                None if takes_value => {
                    i += 1;
                    args.get(i).cloned()
                }
                None => None,
            };
            if let Some(target) = target {
                *target = value;
            } else if name == "--type"
                && let Some(value) = value
            {
                type_references.push(value);
            }
            i += 1;
        }
        Hints::new(folder, sqlite, project, type_references)
    }

    /// Значения подсказки; пусто — источника нет, области нет или она не открылась.
    pub fn resolve(&mut self, hint: Hint) -> Vec<String> {
        self.try_resolve(hint).unwrap_or_default()
    }

    fn session(&self) -> Option<&Session> {
        self.session
            .get_or_init(|| Session::open(self.folder.as_deref(), self.sqlite.as_deref(), true).ok())
            .as_ref()
    }

    fn all<T: IndexEntity>(&self, session: &Session, project: &Uuid) -> Option<Vec<T>> {
        session.index().all::<T>(&IndexQuery::project(project)).ok()
    }

    fn try_resolve(&mut self, hint: Hint) -> Option<Vec<String>> {
        let session = self.session()?;
        if let Hint::Projects = hint {
            return Some(session.all_projects().ok()?.into_iter().map(|p| p.name).collect());
        }
        let project = session.project_id(self.project.as_deref()).ok()?;
        let values = match hint {
            Hint::Name | Hint::None | Hint::Fixed(_) | Hint::Projects => return None,
            Hint::Statuses => self.statuses(session, &project)?,
            Hint::StatusSets => self.all::<StatusSet>(session, &project)?.into_iter().map(|s| s.name).collect(),
            Hint::TaskTypes => self.all::<TaskType>(session, &project)?.into_iter().map(|t| t.name).collect(),
            Hint::SeriesPrefixes => self.all::<Series>(session, &project)?.into_iter().map(|s| s.prefix).collect(),
            Hint::TaskRefs => self
                .all::<Series>(session, &project)?
                .into_iter()
                .map(|s| format!("{}-", s.prefix))
                .collect(),
            Hint::HierarchicalLinkTypes => self
                .all::<LinkType>(session, &project)?
                .into_iter()
                .filter(|t| t.hierarchical)
                .map(|t| t.name)
                .collect(),
            Hint::FieldEquals => self
                .all::<FieldDefinition>(session, &project)?
                .into_iter()
                .map(|f| format!("{}=", f.name))
                .collect(),
            Hint::FieldNames => self
                .all::<FieldDefinition>(session, &project)?
                .into_iter()
                .map(|f| f.name)
                .collect(),
            Hint::Enums => self.all::<FieldEnum>(session, &project)?.into_iter().map(|e| e.name).collect(),
            Hint::SortKeys => {
                let names: Vec<String> = BUILTIN_SORT_NAMES
                    .iter()
                    .map(|n| n.to_string())
                    .chain(self.all::<FieldDefinition>(session, &project)?.into_iter().map(|f| f.name))
                    .collect();
                names.iter().cloned().chain(names.iter().map(|n| format!("-{n}"))).collect()
            }
        };
        Some(values)
    }

    /// Статусы проекта; с набранными типами — только статусы их наборов (нет ни одного — все).
    fn statuses(&self, session: &Session, project: &Uuid) -> Option<Vec<String>> {
        let statuses = self.all::<Status>(session, project)?;
        if self.type_references.is_empty() {
            return Some(statuses.into_iter().map(|s| s.name).collect());
        }
        let types = self.all::<TaskType>(session, project)?;
        let sets = self.all::<StatusSet>(session, project)?;
        let mut in_set: Vec<Uuid> = Vec::new();
        for reference in &self.type_references {
            let set_id = refs::find_item(&types, reference, |t| t.id, |t| &t.name, "task type", false)
                .ok()?
                .status_set_id;
            in_set.extend(sets.iter().filter(|s| s.id == set_id).flat_map(|s| s.status_ids.iter().copied()));
        }
        Some(if in_set.is_empty() {
            statuses.into_iter().map(|s| s.name).collect()
        } else {
            statuses.into_iter().filter(|s| in_set.contains(&s.id)).map(|s| s.name).collect()
        })
    }
}
