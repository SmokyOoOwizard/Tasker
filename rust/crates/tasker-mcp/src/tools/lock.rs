//! `LockTools`: блокировка на время правки — «запись занята, её редактирует Иван».
use crate::call::{Call, Output};
use crate::error::{Result, ToolError};
use crate::json;
use serde_json::Value;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_services::locks::EntityLockService;
use uuid::Uuid;

fn target(call: &Call) -> Result<(Uuid, LockedEntity, Uuid, String)> {
    let project_id = call.require_project()?;
    let entity = call.args.required_string("entity")?;
    let kind = EntityLockService::parse_entity(Some(&entity))?;
    let id = id_of(kind, &project_id, call.args.guid("entityId")?)?;
    Ok((project_id, kind, id, entity))
}

/// `LockTools.IdOf`.
fn id_of(kind: LockedEntity, project_id: &Uuid, entity_id: Option<Uuid>) -> Result<Uuid> {
    match kind {
        LockedEntity::User => Err(ToolError::invalid(
            "Entity: users cannot be locked here; expected one of project, task, taskType, linkType, field, enum, status, statusSet, board, series",
        )),
        LockedEntity::Project => match entity_id {
            Some(id) if id != *project_id => Err(ToolError::invalid("EntityId: for a project it must be the projectId or omitted")),
            _ => Ok(*project_id),
        },
        kind => entity_id.ok_or_else(|| ToolError::invalid(format!("EntityId: required to lock a {}", kind.name()))),
    }
}

pub fn lock_entity(call: &Call) -> Result<Output> {
    let (project_id, kind, id, entity) = target(call)?;
    let info = Call::found(
        call.ws()?.entity_locks().acquire(Some(project_id), kind, &id)?,
        format!("{entity} {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::lock_info(&info)))
}

pub fn unlock_entity(call: &Call) -> Result<Output> {
    let (_, kind, id, _) = target(call)?;
    let released = call.ws()?.entity_locks().release(kind, &id)?;
    Ok(Output::Text(if released { "unlocked" } else { "not locked by you" }.to_string()))
}

pub fn get_lock(call: &Call) -> Result<Output> {
    let (_, kind, id, _) = target(call)?;
    let info = call.ws()?.entity_locks().get(kind, &id);
    Ok(Output::Json(json::object(vec![(
        "lock",
        info.as_ref().map(json::lock_info).unwrap_or(Value::Null),
    )])))
}
