//! Хранилище области в файле SQLite (TSK-159) — заглушка этапа 1.
#![allow(unused_variables)]
use crate::error::Result;
use crate::storage::{Entity, Storage};
use std::collections::HashMap;
use std::path::Path;
use tasker_core::Timestamp;
use tasker_core::locks::{EditHolder, EditLock, LockedEntity};
use tasker_core::model::{Project, TaskItem, User, UserKind};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_files::index::{LinkEdge, NumberConflict, OwnFieldKind};
use uuid::Uuid;

pub trait DbEntity {}
macro_rules! e { ($($t:ty),*) => {$(impl DbEntity for $t {})*} }
use tasker_core::model::*;
e!(
    TaskItem,
    TaskType,
    Status,
    StatusSet,
    Board,
    Series,
    LinkType,
    FieldDefinition,
    FieldEnum
);

#[derive(Debug, Clone)]
pub struct DbStore;

impl DbStore {
    pub fn open(_file: &Path) -> std::io::Result<DbStore> {
        Err(std::io::Error::other("not yet"))
    }
    pub fn attach(_file: &Path) -> std::io::Result<DbStore> {
        Err(std::io::Error::other("not yet"))
    }
}

impl Storage for DbStore {
    fn get_by_id<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<T>> {
        todo!()
    }

    fn get_all<T: Entity>(&self, project_id: &Uuid) -> Result<Vec<T>> {
        todo!()
    }

    fn get_range<T: Entity>(&self, project_id: &Uuid, page: Page) -> Result<ListPage<T>> {
        todo!()
    }

    fn count_unreadable<T: Entity>(&self, project_id: &Uuid) -> Result<usize> {
        todo!()
    }

    fn add<T: Entity>(&self, entity: &T) -> Result<String> {
        todo!()
    }

    fn update<T: Entity>(&self, entity: &T, expected_version: &str) -> Result<Option<String>> {
        todo!()
    }

    fn delete<T: Entity>(&self, project_id: &Uuid, id: &Uuid, expected_version: &str) -> Result<bool> {
        todo!()
    }

    fn get_project(&self, id: &Uuid) -> Result<Option<Project>> {
        todo!()
    }

    fn project_range(&self, ids: Option<&[Uuid]>, page: Page) -> Result<ListPage<Project>> {
        todo!()
    }

    fn all_projects(&self) -> Result<Vec<Project>> {
        todo!()
    }

    fn add_project(&self, project: &Project) -> Result<String> {
        todo!()
    }

    fn update_project(&self, project: &Project, expected_version: &str) -> Result<Option<String>> {
        todo!()
    }

    fn delete_project(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        todo!()
    }

    fn tasks_where(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<TaskItem>> {
        todo!()
    }

    fn task_range(&self, project_id: &Uuid, filter: Option<&TaskFilter>, page: Page) -> Result<ListPage<TaskItem>> {
        todo!()
    }

    fn task_ids(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<Uuid>> {
        todo!()
    }

    fn count_tasks(&self, project_id: &Uuid, filter: &TaskFilter) -> Result<usize> {
        todo!()
    }

    fn tasks_by_id_prefix(&self, project_id: &Uuid, key: &str) -> Result<Vec<TaskItem>> {
        todo!()
    }

    fn own_field_kinds(&self, project_id: &Uuid, name_key: &str) -> Result<Vec<OwnFieldKind>> {
        todo!()
    }

    fn max_number(&self, project_id: &Uuid, series_id: &Uuid) -> Result<i32> {
        todo!()
    }

    fn tasks_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> Result<Vec<TaskItem>> {
        todo!()
    }

    fn number_conflicts(&self, project_id: &Uuid) -> Result<Vec<NumberConflict>> {
        todo!()
    }

    fn tasks_with_series_not_in(&self, project_id: &Uuid, known_series_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        todo!()
    }

    fn tasks_linked_to(&self, project_id: &Uuid, target_id: &Uuid) -> Result<Vec<TaskItem>> {
        todo!()
    }

    fn link_targets(&self, project_id: &Uuid, type_id: &Uuid, source_ids: &[Uuid]) -> Result<HashMap<Uuid, Vec<Uuid>>> {
        todo!()
    }

    fn link_edges(&self, project_id: &Uuid, type_id: &Uuid) -> Result<Vec<LinkEdge>> {
        todo!()
    }

    fn count_linked_to(&self, project_id: &Uuid, target_ids: &[Uuid]) -> Result<HashMap<Uuid, usize>> {
        todo!()
    }

    fn tasks_with_invalid_links(&self, project_id: &Uuid, known_type_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        todo!()
    }

    fn get_user(&self, id: &Uuid) -> Result<Option<User>> {
        todo!()
    }

    fn user_by_username(&self, username: &str) -> Result<Option<User>> {
        todo!()
    }

    fn user_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>> {
        todo!()
    }

    fn all_users(&self, kind: Option<UserKind>) -> Result<Vec<User>> {
        todo!()
    }

    fn add_user(&self, user: &User) -> Result<String> {
        todo!()
    }

    fn update_user(&self, user: &User, expected_version: &str) -> Result<Option<String>> {
        todo!()
    }

    fn delete_user(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        todo!()
    }

    fn lock_get(&self, entity: LockedEntity, id: &Uuid, now: &Timestamp) -> Result<Option<EditLock>> {
        todo!()
    }

    fn locks_by_project(&self, project_id: &Uuid, now: &Timestamp) -> Result<Vec<EditLock>> {
        todo!()
    }

    fn lock_acquire(
        &self,
        entity: LockedEntity,
        id: &Uuid,
        holder: &EditHolder,
        now: &Timestamp,
        expires_at: &Timestamp,
        project_id: Option<Uuid>,
    ) -> Result<EditLock> {
        todo!()
    }

    fn lock_release(&self, entity: LockedEntity, id: &Uuid, holder_key: &str) -> Result<bool> {
        todo!()
    }

    fn lock_remove(&self, entity: LockedEntity, id: &Uuid) -> Result<()> {
        todo!()
    }

    fn exclusive<T>(&self, project_id: &Uuid, action: &mut dyn FnMut() -> Result<T>) -> Result<T> {
        todo!()
    }
}
