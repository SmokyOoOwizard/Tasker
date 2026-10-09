//! Блокировки на время правки на диске (`EditLockStorage` в .NET): `.cache/edit-locks/<Entity>-<id>.json`, в git не попадают.
//! Работают между процессами одной машины (`FileLock` на `.cache/edit-locks.lock`); между машинами блокировки не передаются —
//! там остаётся проверка версии.
//!
//! Читают без блокировки: файл пишется во временный и переименовывается, поэтому виден целиком или никак. Повреждённый файл
//! считается отсутствующей блокировкой — она временная, ничего не теряется.
//!
//! Формат файла — `System.Text.Json` по умолчанию, то есть имена свойств записи .NET в PascalCase, без отступов, null пишется:
//! `{"Holder":"cli","HolderName":"Artem (console)","AcquiredAt":"2026-01-01T00:00:00+00:00","ExpiresAt":"2026-01-01T00:02:00+00:00","ProjectId":null}`.
use crate::layout::TaskerDirectory;
use serde_json::{Map, Value};
use std::io;
use std::path::PathBuf;
use tasker_core::ids::{guid_d, guid_n, parse_guid};
use tasker_core::io::{self as atomic, FileLock};
use tasker_core::locks::{EditHolder, EditLock, LockedEntity};
use tasker_core::time::Timestamp;
use uuid::Uuid;

pub struct EditLockStorage<'a> {
    directory: &'a TaskerDirectory,
}

impl<'a> EditLockStorage<'a> {
    pub fn new(directory: &'a TaskerDirectory) -> EditLockStorage<'a> {
        EditLockStorage { directory }
    }

    /// Действующая блокировка сущности; None — нет или истекла.
    pub fn get(&self, entity: LockedEntity, id: &Uuid, now: &Timestamp) -> Option<EditLock> {
        self.read(entity, id).filter(|held| held.is_active(now))
    }

    /// Действующие блокировки сущностей проекта, по времени захвата, затем по id.
    pub fn get_by_project(&self, project_id: &Uuid, now: &Timestamp) -> Vec<EditLock> {
        let Ok(entries) = std::fs::read_dir(self.directory.edit_locks()) else {
            return Vec::new();
        };
        let mut found = Vec::new();
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().into_owned();
            let Some(stem) = name.strip_suffix(".json") else {
                continue;
            };
            let Some((entity, id)) = parse_file_name(stem) else {
                continue;
            };
            if let Some(held) = self.read(entity, &id)
                && held.project_id == Some(*project_id)
                && held.is_active(now)
            {
                found.push(held);
            }
        }
        found.sort_by(|a, b| {
            a.acquired_at
                .unix_ticks()
                .cmp(&b.acquired_at.unix_ticks())
                .then_with(|| a.id.cmp(&b.id))
        });
        found
    }

    /// Берёт или продлевает блокировку. Держит другой — возвращает его блокировку (вызывающий сравнивает `holder.key`). Своя
    /// действующая блокировка сохраняет время захвата.
    pub fn acquire(
        &self,
        entity: LockedEntity,
        id: &Uuid,
        holder: &EditHolder,
        now: &Timestamp,
        expires_at: &Timestamp,
        project_id: Option<Uuid>,
    ) -> io::Result<EditLock> {
        FileLock::run(&self.directory.edit_locks_lock(), None, || {
            if let Some(held) = self.get(entity, id, now)
                && held.holder.key != holder.key
            {
                return Ok(held);
            }
            let acquired_at = match self.read(entity, id) {
                Some(own) if own.holder.key == holder.key && own.is_active(now) => own.acquired_at,
                _ => *now,
            };
            let taken = EditLock {
                entity,
                id: *id,
                holder: holder.clone(),
                acquired_at,
                expires_at: *expires_at,
                project_id,
            };
            self.write(&taken)?;
            Ok(taken)
        })?
    }

    /// Снимает блокировку держателя `holder_key`; false — блокировки нет или она чужая.
    pub fn release(&self, entity: LockedEntity, id: &Uuid, holder_key: &str) -> io::Result<bool> {
        FileLock::run(&self.directory.edit_locks_lock(), None, || {
            match self.read(entity, id) {
                Some(held) if held.holder.key == holder_key => {}
                _ => return Ok(false),
            }
            atomic::delete(&self.path_of(entity, id))?;
            Ok(true)
        })?
    }

    /// Снимает блокировку, чья бы она ни была (сущность удалена).
    pub fn remove(&self, entity: LockedEntity, id: &Uuid) -> io::Result<()> {
        // Почти всегда блокировки нет (сущность никто не правил): не берём межпроцессную блокировку ради пустого удаления.
        if !self.path_of(entity, id).exists() {
            return Ok(());
        }
        FileLock::run(&self.directory.edit_locks_lock(), None, || {
            atomic::delete(&self.path_of(entity, id))
        })?
    }

    pub fn path_of(&self, entity: LockedEntity, id: &Uuid) -> PathBuf {
        self.directory.edit_locks().join(format!("{}-{}.json", entity.name(), guid_d(id)))
    }

    fn read(&self, entity: LockedEntity, id: &Uuid) -> Option<EditLock> {
        let bytes = std::fs::read(self.path_of(entity, id)).ok()?;
        parse_lock_file(entity, id, &bytes)
    }

    fn write(&self, held: &EditLock) -> io::Result<()> {
        std::fs::create_dir_all(self.directory.edit_locks())?;
        let path = self.path_of(held.entity, &held.id);
        let temp = PathBuf::from(format!("{}.{}.tmp", path.display(), guid_n(&Uuid::new_v4())));
        std::fs::write(&temp, lock_file_json(held))?;
        atomic::move_file(&temp, &path, true)
    }
}

/// Имя файла `<сущность>-<id>` → сущность и id; чужие файлы в папке — None.
pub fn parse_file_name(stem: &str) -> Option<(LockedEntity, Uuid)> {
    let dash = stem.find('-')?;
    if dash == 0 {
        return None;
    }
    let entity = LockedEntity::parse(&stem[..dash])?;
    let id = parse_guid(&stem[dash + 1..])?;
    Some((entity, id))
}

/// Текст файла блокировки — запись `LockFile(Holder, HolderName, AcquiredAt, ExpiresAt, ProjectId)` в JSON System.Text.Json.
pub fn lock_file_json(held: &EditLock) -> String {
    let mut map = Map::new();
    map.insert("Holder".into(), Value::String(held.holder.key.clone()));
    map.insert("HolderName".into(), Value::String(held.holder.name.clone()));
    map.insert("AcquiredAt".into(), Value::String(held.acquired_at.format_json()));
    map.insert("ExpiresAt".into(), Value::String(held.expires_at.format_json()));
    map.insert(
        "ProjectId".into(),
        held.project_id.as_ref().map(|id| Value::String(guid_d(id))).unwrap_or(Value::Null),
    );
    tasker_core::json::to_string(&Value::Object(map))
}

/// Разбор файла блокировки; повреждённый или неполный файл — None (как `JsonException`).
pub fn parse_lock_file(entity: LockedEntity, id: &Uuid, bytes: &[u8]) -> Option<EditLock> {
    let value: Value = serde_json::from_slice(bytes).ok()?;
    let object = value.as_object()?;
    let text = |key: &str| object.get(key).and_then(Value::as_str);
    // Записи .NET без значения: строка — null, DateTimeOffset — ошибка разбора. Holder без значения даёт null, которым дальше
    // нельзя пользоваться, — считаем файл повреждённым.
    let holder = EditHolder::new(text("Holder")?, text("HolderName")?);
    let acquired_at = Timestamp::parse(text("AcquiredAt")?)?;
    let expires_at = Timestamp::parse(text("ExpiresAt")?)?;
    let project_id = match object.get("ProjectId") {
        None | Some(Value::Null) => None,
        Some(Value::String(s)) => Some(parse_guid(s)?),
        Some(_) => return None,
    };
    Some(EditLock {
        entity,
        id: *id,
        holder,
        acquired_at,
        expires_at,
        project_id,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::temp_dir;

    fn at(minute: u8) -> Timestamp {
        Timestamp {
            year: 2026,
            month: 1,
            day: 1,
            hour: 0,
            minute,
            second: 0,
            ticks: 0,
            offset_minutes: 0,
        }
    }

    #[test]
    fn the_file_is_pascal_case_json_without_indentation() {
        let held = EditLock {
            entity: LockedEntity::Task,
            id: Uuid::parse_str("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f").unwrap(),
            holder: EditHolder::new("cli", "Artem (console)"),
            acquired_at: at(0),
            expires_at: Timestamp { ticks: 1_234_500, ..at(2) },
            project_id: None,
        };
        let json = lock_file_json(&held);
        assert_eq!(
            json,
            "{\"Holder\":\"cli\",\"HolderName\":\"Artem (console)\",\"AcquiredAt\":\"2026-01-01T00:00:00+00:00\",\"ExpiresAt\":\"2026-01-01T00:02:00.12345+00:00\",\"ProjectId\":null}"
        );
        assert_eq!(parse_lock_file(LockedEntity::Task, &held.id, json.as_bytes()), Some(held.clone()));
        let with_project = EditLock {
            project_id: Some(Uuid::nil()),
            ..held
        };
        assert!(lock_file_json(&with_project).ends_with("\"ProjectId\":\"00000000-0000-0000-0000-000000000000\"}"));
        assert_eq!(
            parse_lock_file(LockedEntity::Task, &with_project.id, lock_file_json(&with_project).as_bytes()),
            Some(with_project)
        );
        assert_eq!(parse_lock_file(LockedEntity::Task, &Uuid::nil(), b"{\"Holder\":"), None);
        assert_eq!(parse_lock_file(LockedEntity::Task, &Uuid::nil(), b"{}"), None);
    }

    #[test]
    fn file_names_are_entity_dash_guid() {
        let id = Uuid::parse_str("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f").unwrap();
        assert_eq!(
            parse_file_name("StatusSet-3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f"),
            Some((LockedEntity::StatusSet, id))
        );
        assert_eq!(
            parse_file_name("Enum-3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f"),
            Some((LockedEntity::Enum, id))
        );
        assert_eq!(parse_file_name("-3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f"), None);
        assert_eq!(parse_file_name("Note-3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f"), None);
        assert_eq!(parse_file_name("Task-notaguid"), None);
        assert_eq!(parse_file_name("Task"), None);
    }

    #[test]
    fn acquire_release_and_listing_follow_dotnet() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let storage = EditLockStorage::new(&dir);
        let project = Uuid::new_v4();
        let task = Uuid::new_v4();
        let ivan = EditHolder::new("user:1", "Иван");
        let cli = EditHolder::new("cli", "Console");

        assert_eq!(storage.get(LockedEntity::Task, &task, &at(0)), None);
        let taken = storage
            .acquire(LockedEntity::Task, &task, &ivan, &at(0), &at(2), Some(project))
            .unwrap();
        assert_eq!(taken.holder, ivan);
        assert_eq!(
            storage.path_of(LockedEntity::Task, &task),
            dir.edit_locks().join(format!("Task-{}.json", guid_d(&task)))
        );
        assert!(storage.path_of(LockedEntity::Task, &task).exists());

        // Чужой держатель получает чужую блокировку, файл не меняется.
        let held = storage
            .acquire(LockedEntity::Task, &task, &cli, &at(1), &at(3), Some(project))
            .unwrap();
        assert_eq!(held, taken);
        assert!(!storage.release(LockedEntity::Task, &task, "cli").unwrap());

        // Продление своей сохраняет время захвата.
        let renewed = storage
            .acquire(LockedEntity::Task, &task, &ivan, &at(1), &at(3), Some(project))
            .unwrap();
        assert_eq!((renewed.acquired_at, renewed.expires_at), (at(0), at(3)));

        // Истёкшая — как отсутствующая: берётся заново, с новым временем захвата.
        let fresh = storage
            .acquire(LockedEntity::Task, &task, &cli, &at(5), &at(7), Some(project))
            .unwrap();
        assert_eq!((fresh.holder.key.as_str(), fresh.acquired_at), ("cli", at(5)));

        let other = Uuid::new_v4();
        storage
            .acquire(LockedEntity::Status, &other, &ivan, &at(4), &at(9), Some(project))
            .unwrap();
        storage
            .acquire(LockedEntity::Board, &Uuid::new_v4(), &ivan, &at(4), &at(9), Some(Uuid::new_v4()))
            .unwrap();
        std::fs::write(dir.edit_locks().join("notes.json"), b"{}").unwrap();
        std::fs::write(dir.edit_locks().join(format!("Task-{}.json", guid_d(&Uuid::new_v4()))), b"broken").unwrap();
        let listed: Vec<(LockedEntity, Uuid)> = storage
            .get_by_project(&project, &at(6))
            .into_iter()
            .map(|x| (x.entity, x.id))
            .collect();
        assert_eq!(listed, vec![(LockedEntity::Status, other), (LockedEntity::Task, task)]);
        assert!(storage.get_by_project(&project, &at(10)).is_empty());

        assert!(storage.release(LockedEntity::Task, &task, "cli").unwrap());
        assert!(!storage.path_of(LockedEntity::Task, &task).exists());
        storage.remove(LockedEntity::Status, &other).unwrap();
        storage.remove(LockedEntity::Status, &other).unwrap();
        assert_eq!(storage.get(LockedEntity::Status, &other, &at(5)), None);
        assert!(dir.edit_locks_lock().exists());
        std::fs::remove_dir_all(&ws).unwrap();
    }
}
