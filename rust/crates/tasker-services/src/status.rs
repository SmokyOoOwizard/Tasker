//! Статусы проекта (`StatusService`).
use crate::Workspace;
use crate::error::Result;
use crate::preview::{self, StatusListItem};
use crate::usages::Usages;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Board, Status, StatusSet};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{validate, versioning};
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateStatus {
    pub name: String,
    pub color: String,
    pub description: Option<String>,
}

/// Поля None — не меняются; `description`: пустая строка — очистить.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateStatus {
    pub name: Option<String>,
    pub color: Option<String>,
    pub version: Option<String>,
    pub description: Option<String>,
}

pub struct StatusService<'a> {
    ws: &'a Workspace,
}

fn subject(status: &Status) -> String {
    format!("Status '{}'", status.name)
}

impl<'a> StatusService<'a> {
    pub fn new(ws: &'a Workspace) -> StatusService<'a> {
        StatusService { ws }
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<Status>> {
        self.ws.get_by_id::<Status>(project_id, id)
    }

    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<Status>> {
        self.ws.get_all::<Status>(project_id)
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateStatus) -> Result<Status> {
        let mut status = Status {
            id: Uuid::new_v4(),
            project_id: *project_id,
            name: validate::entity_name(Some(&command.name), "Status name")?,
            color: validate::color(Some(&command.color))?,
            description: validate::description(command.description.as_deref()).unwrap_or_default(),
            version: versioning::NEW.to_string(),
        };
        status.version = self.ws.add(&status)?;
        Ok(status)
    }

    /// Страница статусов по имени; описания усечены до `description_length`.
    pub fn list(&self, project_id: &Uuid, page: Page, description_length: i32) -> Result<ListPage<StatusListItem>> {
        preview::check(Some(description_length), preview::FULL)?;
        let found = self.ws.get_range::<Status>(project_id, page)?;
        Ok(ListPage {
            total_count: found.total_count,
            offset: found.offset,
            limit: found.limit,
            data: found.data.into_iter().map(|x| StatusListItem::new(x, description_length)).collect(),
        })
    }

    /// None — статуса нет.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateStatus) -> Result<Option<Status>> {
        let Some(status) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&status);
        self.ws.locks().ensure_writable(LockedEntity::Status, &status.id, &subject)?;
        let expected = versioning::check(&status.version, command.version.as_deref(), &subject)?;

        let mut updated = status.clone();
        if let Some(name) = &command.name {
            updated.name = validate::entity_name(Some(name), "Status name")?;
        }
        if let Some(color) = &command.color {
            updated.color = validate::color(Some(color))?;
        }
        if let Some(description) = &command.description {
            updated.description = validate::description(Some(description)).unwrap_or_default();
        }
        let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
        updated.version = version;
        Ok(Some(updated))
    }

    /// Нельзя удалить статус, который используют задачи, наборы статусов или колонки досок. false — статуса нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(status) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&status);
        self.ws.locks().ensure_writable(LockedEntity::Status, &status.id, &subject)?;
        let expected = versioning::check(&status.version, version, &subject)?;

        let mut usages = Usages::new(&subject);
        let filter = TaskFilter {
            status_ids: Some(vec![*id]),
            ..TaskFilter::default()
        };
        usages.add_tasks(
            self.ws
                .index()
                .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?,
        );
        for set in self.ws.get_all::<StatusSet>(project_id)? {
            if set.status_ids.contains(id) {
                usages.add(format!("status set '{}'", set.name));
            }
        }
        for board in self.ws.get_all::<Board>(project_id)? {
            for column in &board.columns {
                if column.status_ids.contains(id) || column.drop_statuses.iter().any(|(_, s)| s == id) {
                    usages.add_board_column(&board, column);
                }
            }
        }
        usages.throw_if_any("deleted")?;

        if !self.ws.delete::<Status>(project_id, id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::Status, &status.id)?;
        Ok(true)
    }
}
