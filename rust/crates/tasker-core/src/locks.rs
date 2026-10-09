//! Блокировки на время правки (`Tasker.Core.Locks`): «запись занята, её редактирует Иван». Дополняют проверку версии, а не
//! заменяют её: версия остаётся последней защитой при записи, блокировка заранее говорит другим, что запись правят. Забытая
//! блокировка истекает сама через [`DURATION`].
use crate::error::{ConflictCode, TaskerError};
use crate::time::Timestamp;
use std::time::Duration;
use uuid::Uuid;

/// Срок блокировки (`EditLockService.Duration`): две минуты, редактор продлевает, пока форма открыта.
pub const DURATION: Duration = Duration::from_secs(120);

/// Ключ и имя держателя без входа (`CurrentUserEditor.LocalKey`/`LocalName`); консоль регистрирует свой: `cli`, «… (console)».
pub const LOCAL_KEY: &str = "local";
pub const LOCAL_NAME: &str = "Local user";

/// Какие сущности можно блокировать на время правки. Имена — как у enum .NET: они идут в имена файлов `<Entity>-<id>.json`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LockedEntity {
    Project,
    Task,
    TaskType,
    Status,
    StatusSet,
    Board,
    Series,
    User,
    LinkType,
    Field,
    Enum,
}

impl LockedEntity {
    pub const ALL: [LockedEntity; 11] = [
        Self::Project,
        Self::Task,
        Self::TaskType,
        Self::Status,
        Self::StatusSet,
        Self::Board,
        Self::Series,
        Self::User,
        Self::LinkType,
        Self::Field,
        Self::Enum,
    ];

    /// `Enum.ToString()`: PascalCase.
    pub fn name(self) -> &'static str {
        match self {
            Self::Project => "Project",
            Self::Task => "Task",
            Self::TaskType => "TaskType",
            Self::Status => "Status",
            Self::StatusSet => "StatusSet",
            Self::Board => "Board",
            Self::Series => "Series",
            Self::User => "User",
            Self::LinkType => "LinkType",
            Self::Field => "Field",
            Self::Enum => "Enum",
        }
    }

    /// `Enum.TryParse<LockedEntity>` без `ignoreCase`: имя или число (порядковый номер) — так .NET разбирает enum из строки.
    pub fn parse(text: &str) -> Option<LockedEntity> {
        let trimmed = text.trim();
        if let Some(found) = Self::ALL.iter().find(|x| x.name() == trimmed) {
            return Some(*found);
        }
        let number: usize = trimmed.parse().ok()?;
        Self::ALL.get(number).copied()
    }
}

/// Кто держит блокировку: `key` — идентичность (один и тот же ключ — один и тот же держатель: `user:<id>`, `local`, `cli`),
/// `name` — как показывать другим («правит Иван»).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EditHolder {
    pub key: String,
    pub name: String,
}

impl EditHolder {
    pub fn new(key: impl Into<String>, name: impl Into<String>) -> EditHolder {
        EditHolder {
            key: key.into(),
            name: name.into(),
        }
    }
}

/// Блокировка сущности на время правки. Действует до `expires_at`, дальше считается снятой. `project_id` — проект сущности
/// (у самого проекта — он сам): по нему отбираются блокировки проекта; None — вне проектов или не указан.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EditLock {
    pub entity: LockedEntity,
    pub id: Uuid,
    pub holder: EditHolder,
    pub acquired_at: Timestamp,
    pub expires_at: Timestamp,
    pub project_id: Option<Uuid>,
}

impl EditLock {
    /// `ExpiresAt > now` — сравнение моментов, а не полей (смещения могут различаться).
    pub fn is_active(&self, now: &Timestamp) -> bool {
        self.expires_at.unix_ticks() > now.unix_ticks()
    }
}

/// `TaskerLockedException`: сущность занята, её правит другой держатель. В API — 409 с кодом `locked`.
pub fn locked_error(subject: &str, held_by: &EditLock) -> TaskerError {
    TaskerError::conflict(
        ConflictCode::Locked,
        format!("{subject} is being edited by {}", held_by.holder.name),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn entity_names_round_trip_like_dotnet_enums() {
        for entity in LockedEntity::ALL {
            assert_eq!(LockedEntity::parse(entity.name()), Some(entity));
        }
        assert_eq!(LockedEntity::parse("StatusSet"), Some(LockedEntity::StatusSet));
        assert_eq!(LockedEntity::parse("statusset"), None);
        assert_eq!(LockedEntity::parse("4"), Some(LockedEntity::StatusSet));
        assert_eq!(LockedEntity::parse("11"), None);
        assert_eq!(LockedEntity::parse("Tasks"), None);
    }

    #[test]
    fn locked_message_matches_dotnet() {
        let held = EditLock {
            entity: LockedEntity::Task,
            id: Uuid::nil(),
            holder: EditHolder::new("local", "Иван"),
            acquired_at: Timestamp::UNIX_EPOCH,
            expires_at: Timestamp::UNIX_EPOCH,
            project_id: None,
        };
        assert_eq!(locked_error("Task 'x'", &held).message(), "Task 'x' is being edited by Иван");
        assert!(!held.is_active(&Timestamp::UNIX_EPOCH));
    }
}
