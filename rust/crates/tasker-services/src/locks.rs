//! Блокировки на время правки (`EditLockService`, `EntityLockService` в .NET): «запись занята, её редактирует Иван». Дополняют
//! проверку версии: редактор берёт блокировку при открытии формы, продлевает и снимает; забытая истекает через [`DURATION`].
//! Пока блокировку держит другой, изменить и удалить сущность нельзя ([`EditLockService::ensure_writable`]). Массовые правки
//! ждут чужие блокировки до [`CASCADE_WAIT`] ([`EditLockService::wait_until_writable`]) — снаружи секции записи, внутри —
//! проверяют без ожидания.
use crate::Workspace;
use crate::clock::{add, until};
use crate::error::{Error, Result};
use std::time::Duration;
use tasker_core::Timestamp;
use tasker_core::locks::{EditLock, LockedEntity, locked_error};
use tasker_core::model::{Board, FieldDefinition, FieldEnum, LinkType, Series, Status, StatusSet, TaskItem, TaskType};
use uuid::Uuid;

/// На сколько выдаётся и продлевается блокировка.
pub const DURATION: Duration = tasker_core::locks::DURATION;

/// Сколько массовая правка ждёт, пока другой отпустит блокировку затрагиваемой задачи.
pub const CASCADE_WAIT: Duration = Duration::from_secs(30);

/// Как часто при ожидании проверяется, снята ли блокировка.
pub const CASCADE_POLL: Duration = Duration::from_millis(200);

/// Что заблокировать: вид, id и название для ошибки (`Task 'Fix login'`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LockTarget {
    pub entity: LockedEntity,
    pub id: Uuid,
    pub subject: String,
}

impl LockTarget {
    pub fn new(entity: LockedEntity, id: Uuid, subject: impl Into<String>) -> LockTarget {
        LockTarget {
            entity,
            id,
            subject: subject.into(),
        }
    }

    pub fn task(task: &TaskItem) -> LockTarget {
        LockTarget::new(LockedEntity::Task, task.id, format!("Task '{}'", task.title))
    }
}

pub struct EditLockService<'a> {
    ws: &'a Workspace,
}

impl<'a> EditLockService<'a> {
    pub fn new(ws: &'a Workspace) -> EditLockService<'a> {
        EditLockService { ws }
    }

    /// Берёт блокировку или продлевает свою («пульс» редактора). Держит другой — `Locked`.
    pub fn acquire(&self, entity: LockedEntity, id: &Uuid, subject: &str, project_id: Option<Uuid>) -> Result<EditLock> {
        let holder = self.ws.editor();
        let now = self.ws.now();
        let held = self
            .ws
            .edit_locks()
            .acquire(entity, id, holder, &now, &add(now, DURATION), project_id)?;
        if held.holder.key != holder.key {
            return Err(locked_error(subject, &held).into());
        }
        Ok(held)
    }

    /// Снимает свою блокировку; нет или чужая — false.
    pub fn release(&self, entity: LockedEntity, id: &Uuid) -> Result<bool> {
        Ok(self.ws.edit_locks().release(entity, id, &self.ws.editor().key)?)
    }

    /// Действующая блокировка; None — нет.
    pub fn get(&self, entity: LockedEntity, id: &Uuid) -> Option<EditLock> {
        self.ws.edit_locks().get(entity, id, &self.ws.now())
    }

    /// Действующие блокировки проекта: старые первыми.
    pub fn get_by_project(&self, project_id: &Uuid) -> Vec<EditLock> {
        self.ws.edit_locks().get_by_project(project_id, &self.ws.now())
    }

    /// Перед изменением и удалением: свободна или своя — можно писать; чужая — отказ.
    pub fn ensure_writable(&self, entity: LockedEntity, id: &Uuid, subject: &str) -> Result<()> {
        match self.get(entity, id) {
            Some(held) if held.holder.key != self.ws.editor().key => Err(locked_error(subject, &held).into()),
            _ => Ok(()),
        }
    }

    /// Для массовых правок: ждёт, пока другие отпустят блокировки всех перечисленных сущностей (своя не мешает). `timeout` None —
    /// [`Workspace::cascade_timeout`]; ноль — проверка без ожидания. По истечении — `Locked` со всеми занятыми сущностями.
    pub fn wait_until_writable(&self, items: &[LockTarget], timeout: Option<Duration>) -> Result<()> {
        if items.is_empty() {
            return Ok(());
        }
        let deadline = add(self.ws.now(), timeout.unwrap_or(self.ws.cascade_timeout()));
        loop {
            let me = &self.ws.editor().key;
            let mut first: Option<EditLock> = None;
            let mut busy: Vec<&str> = Vec::new();
            for item in items {
                if let Some(held) = self.get(item.entity, &item.id)
                    && held.holder.key != *me
                {
                    first.get_or_insert(held);
                    busy.push(&item.subject);
                }
            }
            let Some(first) = first else {
                return Ok(());
            };
            let Some(left) = until(&self.ws.now(), &deadline) else {
                return Err(locked_error(&busy.join(", "), &first).into());
            };
            self.ws.clock().sleep(left.min(CASCADE_POLL));
        }
    }

    /// Сущность удалена — её блокировка больше не нужна.
    pub fn forget(&self, entity: LockedEntity, id: &Uuid) -> Result<()> {
        Ok(self.ws.edit_locks().remove(entity, id)?)
    }
}

/// Блокировка в виде для клиентов: без внутреннего ключа держателя.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LockInfo {
    pub entity: LockedEntity,
    pub id: Uuid,
    pub holder: String,
    pub mine: bool,
    pub acquired_at: Timestamp,
    pub expires_at: Timestamp,
}

/// Блокировка по виду сущности и id (`EntityLockService`): проверяет, что сущность есть, и называет её в ошибке как сервисы.
pub struct EntityLockService<'a> {
    ws: &'a Workspace,
}

impl<'a> EntityLockService<'a> {
    pub fn new(ws: &'a Workspace) -> EntityLockService<'a> {
        EntityLockService { ws }
    }

    /// Сущности внутри проекта — для них при блокировке нужен `projectId`.
    pub fn is_project_scoped(entity: LockedEntity) -> bool {
        !matches!(entity, LockedEntity::Project | LockedEntity::User)
    }

    /// Вид из текста клиента: `task`, `taskType`, `status-set`…, регистр не важен.
    pub fn parse_entity(text: Option<&str>) -> Result<LockedEntity> {
        let normalized: String = text.unwrap_or("").replace(['-', '_'], "");
        let normalized = normalized.trim();
        for entity in LockedEntity::ALL {
            if tasker_core::validate::eq_ignore_case(entity.name(), normalized) {
                return Ok(entity);
            }
        }
        let names: Vec<String> = LockedEntity::ALL
            .iter()
            .map(|x| {
                let n = x.name();
                let mut s = String::new();
                s.push(n.chars().next().unwrap().to_ascii_lowercase());
                s.push_str(&n[1..]);
                s
            })
            .collect();
        Err(Error::validation(format!(
            "Entity: unknown kind '{}'; expected one of {}",
            text.unwrap_or(""),
            names.join(", ")
        )))
    }

    /// Берёт блокировку или продлевает свою. None — такой сущности нет.
    pub fn acquire(&self, project_id: Option<Uuid>, entity: LockedEntity, id: &Uuid) -> Result<Option<LockInfo>> {
        let Some(subject) = self.subject(project_id, entity, id)? else {
            return Ok(None);
        };
        let scope = if entity == LockedEntity::Project { Some(*id) } else { project_id };
        let held = self.ws.locks().acquire(entity, id, &subject, scope)?;
        Ok(Some(self.to_info(&held)))
    }

    /// Действующие блокировки проекта, старые первыми.
    pub fn get_by_project(&self, project_id: &Uuid) -> Vec<LockInfo> {
        self.ws.locks().get_by_project(project_id).iter().map(|x| self.to_info(x)).collect()
    }

    pub fn release(&self, entity: LockedEntity, id: &Uuid) -> Result<bool> {
        self.ws.locks().release(entity, id)
    }

    pub fn get(&self, entity: LockedEntity, id: &Uuid) -> Option<LockInfo> {
        self.ws.locks().get(entity, id).map(|x| self.to_info(&x))
    }

    fn to_info(&self, held: &EditLock) -> LockInfo {
        LockInfo {
            entity: held.entity,
            id: held.id,
            holder: held.holder.name.clone(),
            mine: held.holder.key == self.ws.editor().key,
            acquired_at: held.acquired_at,
            expires_at: held.expires_at,
        }
    }

    fn subject(&self, project_id: Option<Uuid>, entity: LockedEntity, id: &Uuid) -> Result<Option<String>> {
        if Self::is_project_scoped(entity) && project_id.is_none() {
            return Err(Error::validation(format!("ProjectId is required to lock a {}", entity.name())));
        }
        let project = project_id.unwrap_or_default();
        let ws = self.ws;
        Ok(match entity {
            LockedEntity::Project => ws.get_project(id)?.map(|x| format!("Project '{}'", x.name)),
            LockedEntity::Task => ws.get_by_id::<TaskItem>(&project, id)?.map(|x| format!("Task '{}'", x.title)),
            LockedEntity::TaskType => ws.get_by_id::<TaskType>(&project, id)?.map(|x| format!("Task type '{}'", x.name)),
            LockedEntity::Status => ws.get_by_id::<Status>(&project, id)?.map(|x| format!("Status '{}'", x.name)),
            LockedEntity::StatusSet => ws.get_by_id::<StatusSet>(&project, id)?.map(|x| format!("Status set '{}'", x.name)),
            LockedEntity::Board => ws.get_by_id::<Board>(&project, id)?.map(|x| format!("Board '{}'", x.name)),
            LockedEntity::Series => ws.get_by_id::<Series>(&project, id)?.map(|x| format!("Series '{}'", x.name)),
            LockedEntity::User => {
                let path = ws.directory().user_file(id);
                match tasker_files::write::read_bytes(&path)? {
                    Some(bytes) => Some(format!(
                        "User '{}'",
                        tasker_files::files::user::parse(&bytes, &path)?.model.username
                    )),
                    None => None,
                }
            }
            LockedEntity::LinkType => ws.get_by_id::<LinkType>(&project, id)?.map(|x| format!("Link type '{}'", x.name)),
            LockedEntity::Field => ws
                .get_by_id::<FieldDefinition>(&project, id)?
                .map(|x| format!("Field '{}'", x.name)),
            LockedEntity::Enum => ws.get_by_id::<FieldEnum>(&project, id)?.map(|x| format!("Enum '{}'", x.name)),
        })
    }
}

/// Без входа у каждого клиента свой ключ: консоль — `cli`, «<имя> (console)».
pub fn console_editor(name: &str) -> tasker_core::locks::EditHolder {
    tasker_core::locks::EditHolder::new("cli", format!("{name} (console)"))
}
