//! Проекты (`ProjectService`): доступ открытый (десктоп, консоль — `OpenProjectAccess`), видны все проекты.
use crate::Workspace;
use crate::error::Result;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Board, LinkType, Project, Series, Status, StatusSet, TaskType};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{validate, versioning};
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateProject {
    pub name: String,
}

/// Поля None — не меняются. `version` — версия, которую видел клиент.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateProject {
    pub name: Option<String>,
    pub version: Option<String>,
}

/// Сколько чего в проекте: показывается перед удалением.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ProjectStats {
    pub tasks: usize,
    pub boards: usize,
    pub statuses: usize,
    pub status_sets: usize,
    pub task_types: usize,
    pub series: usize,
    pub link_types: usize,
}

pub struct ProjectService<'a> {
    ws: &'a Workspace,
}

impl<'a> ProjectService<'a> {
    pub fn new(ws: &'a Workspace) -> ProjectService<'a> {
        ProjectService { ws }
    }

    /// Страница проектов по имени.
    pub fn get_range(&self, page: Page) -> Result<ListPage<Project>> {
        self.ws.project_range(None, page)
    }

    /// Все проекты области по имени (страницами по `Page::MaxLimit`, как `Context.AllProjects`).
    pub fn get_all(&self) -> Result<Vec<Project>> {
        let mut all: Vec<Project> = Vec::new();
        loop {
            let page = self.get_range(Page::new(all.len(), 200))?;
            let total = page.total_count;
            let count = page.data.len();
            all.extend(page.data);
            if count == 0 || all.len() >= total {
                return Ok(all);
            }
        }
    }

    pub fn get_by_id(&self, id: &Uuid) -> Result<Option<Project>> {
        self.ws.get_project(id)
    }

    /// Количество сущностей проекта; None — проекта нет.
    pub fn get_stats(&self, id: &Uuid) -> Result<Option<ProjectStats>> {
        if self.get_by_id(id)?.is_none() {
            return Ok(None);
        }
        let one = Page::new(0, 1);
        Ok(Some(ProjectStats {
            tasks: self
                .ws
                .index()
                .count(EntityKind::Task, &IndexQuery::tasks(id, Some(&TaskFilter::default())))?,
            boards: self.ws.get_range::<Board>(id, one)?.total_count,
            statuses: self.ws.get_range::<Status>(id, one)?.total_count,
            status_sets: self.ws.get_range::<StatusSet>(id, one)?.total_count,
            task_types: self.ws.get_range::<TaskType>(id, one)?.total_count,
            series: self.ws.get_range::<Series>(id, one)?.total_count,
            link_types: self.ws.get_range::<LinkType>(id, one)?.total_count,
        }))
    }

    pub fn create(&self, command: &CreateProject) -> Result<Project> {
        let mut project = Project {
            id: Uuid::new_v4(),
            name: validate::name(Some(&command.name), "Project name", validate::MAX_NAME_LENGTH)?,
            created_at: self.ws.now(),
            version: versioning::NEW.to_string(),
        };
        project.version = self.ws.add_project(&project)?;
        Ok(project)
    }

    /// None — проекта нет.
    pub fn update(&self, id: &Uuid, command: &UpdateProject) -> Result<Option<Project>> {
        let Some(project) = self.get_by_id(id)? else {
            return Ok(None);
        };
        let subject = format!("Project '{}'", project.name);
        self.ws.locks().ensure_writable(LockedEntity::Project, &project.id, &subject)?;
        let expected = versioning::check(&project.version, command.version.as_deref(), &subject)?;

        let mut updated = project.clone();
        if let Some(name) = &command.name {
            updated.name = validate::name(Some(name), "Project name", validate::MAX_NAME_LENGTH)?;
        }
        let version = self
            .ws
            .update_project(&updated, &expected)?
            .ok_or_else(|| versioning::modified(&subject))?;
        updated.version = version;
        Ok(Some(updated))
    }

    /// Удаляет проект вместе со всем содержимым. false — проекта нет.
    pub fn delete(&self, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(project) = self.get_by_id(id)? else {
            return Ok(false);
        };
        let subject = format!("Project '{}'", project.name);
        self.ws.locks().ensure_writable(LockedEntity::Project, &project.id, &subject)?;
        let expected = versioning::check(&project.version, version, &subject)?;
        if !self.ws.delete_project(id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::Project, &project.id)?;
        Ok(true)
    }
}
