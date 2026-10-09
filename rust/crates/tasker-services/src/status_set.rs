//! Наборы статусов (`StatusSetService`): убрать статус из набора нельзя, если он ещё нужен задачам типов с этим набором или доскам.
use crate::Workspace;
use crate::error::{Error, Result};
use crate::usages::Usages;
use std::collections::{HashMap, HashSet};
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Board, Status, StatusSet, TaskType};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{validate, versioning};
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateStatusSet {
    pub name: String,
    /// Статусы проекта в порядке отображения.
    pub status_ids: Vec<Uuid>,
}

/// Поля None — не меняются. `status_ids` заменяет список целиком (и порядок).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateStatusSet {
    pub name: Option<String>,
    pub status_ids: Option<Vec<Uuid>>,
    pub version: Option<String>,
}

pub struct StatusSetService<'a> {
    ws: &'a Workspace,
}

fn subject(set: &StatusSet) -> String {
    format!("Status set '{}'", set.name)
}

impl<'a> StatusSetService<'a> {
    pub fn new(ws: &'a Workspace) -> StatusSetService<'a> {
        StatusSetService { ws }
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<StatusSet>> {
        self.ws.get_by_id::<StatusSet>(project_id, id)
    }

    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<StatusSet>> {
        self.ws.get_all::<StatusSet>(project_id)
    }

    pub fn get_range(&self, project_id: &Uuid, page: Page) -> Result<ListPage<StatusSet>> {
        self.ws.get_range::<StatusSet>(project_id, page)
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateStatusSet) -> Result<StatusSet> {
        let mut set = StatusSet {
            id: Uuid::new_v4(),
            project_id: *project_id,
            name: validate::entity_name(Some(&command.name), "Status set name")?,
            status_ids: self.validate_statuses(project_id, &command.status_ids)?,
            version: versioning::NEW.to_string(),
        };
        set.version = self.ws.add(&set)?;
        Ok(set)
    }

    /// None — набора нет.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateStatusSet) -> Result<Option<StatusSet>> {
        let Some(set) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&set);
        self.ws.locks().ensure_writable(LockedEntity::StatusSet, &set.id, &subject)?;
        let expected = versioning::check(&set.version, command.version.as_deref(), &subject)?;

        let name = match &command.name {
            None => set.name.clone(),
            Some(name) => validate::entity_name(Some(name), "Status set name")?,
        };
        let status_ids = match &command.status_ids {
            None => set.status_ids.clone(),
            Some(ids) => self.validate_statuses(project_id, ids)?,
        };
        let removed: Vec<Uuid> = set.status_ids.iter().filter(|x| !status_ids.contains(x)).copied().collect();
        if !removed.is_empty() {
            self.ensure_can_remove(project_id, &set, &removed)?;
        }

        let mut updated = StatusSet { name, status_ids, ..set };
        let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
        updated.version = version;
        Ok(Some(updated))
    }

    /// Нельзя удалить набор, который используют типы задач или доски. false — набора нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(set) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&set);
        self.ws.locks().ensure_writable(LockedEntity::StatusSet, &set.id, &subject)?;
        let expected = versioning::check(&set.version, version, &subject)?;

        let mut usages = Usages::new(&subject);
        for task_type in self.ws.get_all::<TaskType>(project_id)? {
            if task_type.status_set_id == *id {
                usages.add(format!("task type '{}'", task_type.name));
            }
        }
        for board in self.ws.get_all::<Board>(project_id)? {
            if board.status_set_ids.contains(id) {
                usages.add(format!("board '{}'", board.name));
            }
        }
        usages.throw_if_any("deleted")?;

        if !self.ws.delete::<StatusSet>(project_id, id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::StatusSet, &set.id)?;
        Ok(true)
    }

    fn ensure_can_remove(&self, project_id: &Uuid, set: &StatusSet, removed: &[Uuid]) -> Result<()> {
        let names: HashMap<Uuid, String> = self.ws.get_all::<Status>(project_id)?.into_iter().map(|x| (x.id, x.name)).collect();
        let project_sets: HashMap<Uuid, StatusSet> = self.get_all(project_id)?.into_iter().map(|x| (x.id, x)).collect();
        let name_of = |id: &Uuid| names.get(id).cloned().unwrap_or_default();
        let subject = if removed.len() == 1 {
            format!("Status '{}'", name_of(&removed[0]))
        } else {
            format!(
                "Statuses {}",
                removed.iter().map(|x| format!("'{}'", name_of(x))).collect::<Vec<_>>().join(", ")
            )
        };
        let mut usages = Usages::new(subject);

        let type_ids: Vec<Uuid> = self
            .ws
            .get_all::<TaskType>(project_id)?
            .into_iter()
            .filter(|x| x.status_set_id == set.id)
            .map(|x| x.id)
            .collect();
        if !type_ids.is_empty() {
            let filter = TaskFilter {
                type_ids: Some(type_ids),
                status_ids: Some(removed.to_vec()),
                ..TaskFilter::default()
            };
            usages.add_tasks(
                self.ws
                    .index()
                    .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?,
            );
        }

        for board in self.ws.get_all::<Board>(project_id)? {
            if !board.status_set_ids.contains(&set.id) {
                continue;
            }
            // Статусы, которые после изменения останутся хоть в одном наборе доски.
            let still_on_board: HashSet<Uuid> = board
                .status_set_ids
                .iter()
                .filter(|x| **x != set.id)
                .flat_map(|x| project_sets.get(x).map(|s| s.status_ids.clone()).unwrap_or_default())
                .collect();
            for column in &board.columns {
                let drops_removed = column.drop_status(&set.id).is_some_and(|drop| removed.contains(&drop));
                let column_loses_status = column.status_ids.iter().any(|x| removed.contains(x) && !still_on_board.contains(x));
                if drops_removed || column_loses_status {
                    usages.add_board_column(&board, column);
                }
            }
        }
        usages.throw_if_any(&format!("removed from status set '{}'", set.name))
    }

    fn validate_statuses(&self, project_id: &Uuid, ids: &[Uuid]) -> Result<Vec<Uuid>> {
        let status_ids = validate::distinct(ids, "StatusIds")?;
        if status_ids.is_empty() {
            return Err(Error::validation("Status set must contain at least one status"));
        }
        let known: Vec<Uuid> = self.ws.get_all::<Status>(project_id)?.into_iter().map(|x| x.id).collect();
        validate::all_known(status_ids.iter(), &known, "StatusIds")?;
        Ok(status_ids)
    }
}
