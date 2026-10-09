//! `tasker lock …` и `tasker whoami` (`LockCommands` в .NET): блокировка на время правки (держатель — консоль) и имя,
//! которое другие видят в блокировках.
use super::{find_board, find_enum, find_field, find_link_type, find_series, find_status, find_status_set, find_task_type};
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::json::object;
use crate::kit;
use clap::ArgMatches;
use serde_json::Value;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::settings::{SettingsStore, os_user_name};
use tasker_services::locks::EntityLockService;
use uuid::Uuid;

const KINDS: &str = "project, task, taskType, linkType, field, enum, status, statusSet, board, series";

fn resolve(ctx: &Context<'_>, entity: &str, reference: &str) -> Result<(LockedEntity, Option<Uuid>, Uuid)> {
    let kind = EntityLockService::parse_entity(Some(entity))?;
    if kind == LockedEntity::User {
        return Err(CliError::new(format!("Users cannot be locked: use one of {KINDS}")));
    }
    let project = ctx.optional_project()?;
    if kind == LockedEntity::Project {
        return Ok((kind, project.map(|p| p.id), ctx.session().find_project(reference)?.id));
    }
    let project_id = ctx.project_id()?;
    let id = match kind {
        LockedEntity::Task => {
            let found = super::task::find_all(ctx, &project_id, reference)?;
            if found.len() != 1 {
                return Err(CliError::new(format!(
                    "Several tasks match '{reference}', use the id: {}",
                    found.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
                )));
            }
            found[0].id
        }
        LockedEntity::TaskType => find_task_type(ctx, &project_id, reference)?.id,
        LockedEntity::LinkType => find_link_type(ctx, &project_id, reference)?.id,
        LockedEntity::Field => find_field(ctx, &project_id, reference)?.id,
        LockedEntity::Enum => find_enum(ctx, &project_id, reference)?.id,
        LockedEntity::Status => find_status(ctx, &project_id, reference)?.id,
        LockedEntity::StatusSet => find_status_set(ctx, &project_id, reference)?.id,
        LockedEntity::Board => find_board(ctx, &project_id, reference)?.id,
        _ => find_series(ctx, &project_id, reference)?.id,
    };
    Ok((kind, Some(project_id), id))
}

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let entity = kit::required(leaf, "entity");
    let reference = kit::required(leaf, "id-or-name");
    let (kind, project_id, id) = resolve(ctx, &entity, &reference)?;
    let ws = ctx.session().workspace().clone();
    match command {
        "acquire" => {
            let held = ws
                .entity_locks()
                .acquire(project_id, kind, &id)?
                .ok_or_else(|| CliError::new(format!("No {entity} '{reference}'")))?;
            ctx.print(
                &entities::lock_info(&held),
                &format!("Locked {} {} until {}", kind.name(), guid_d(&id), held.expires_at.format_console()),
            );
        }
        "release" => {
            let released = ws.entity_locks().release(kind, &id)?;
            ctx.print(
                &object(vec![("released", Value::Bool(released))]),
                &if released {
                    format!("Released {} {}", kind.name(), guid_d(&id))
                } else {
                    format!("{} {} is not locked by you", kind.name(), guid_d(&id))
                },
            );
        }
        "show" => {
            let held = ws.entity_locks().get(kind, &id);
            let text = match &held {
                None => format!("{} {} is not locked", kind.name(), guid_d(&id)),
                Some(held) => format!(
                    "{} {} is edited by {}{} until {}",
                    kind.name(),
                    guid_d(&id),
                    held.holder,
                    if held.mine { " (you)" } else { "" },
                    held.expires_at.format_console()
                ),
            };
            ctx.print(
                &object(vec![("lock", held.as_ref().map(entities::lock_info).unwrap_or(Value::Null))]),
                &text,
            );
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}

/// Кто вы для остальных: имя в блокировках («правит Иван») на десктопе и в консоли.
pub fn whoami(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let settings = SettingsStore::new(None);
    let value = leaf.get_one::<String>("name").cloned();
    let clear = kit::flag(leaf, "clear");
    if value.is_some() && clear {
        return Err(CliError::new("Use either a name or --clear, not both"));
    }
    if value.is_some() || clear {
        settings.set_user_name(value.as_deref())?;
        let changed = settings.load()?.user_name.unwrap_or_else(os_user_name);
        ctx.print(
            &object(vec![("name", Value::String(changed.clone()))]),
            &format!("Your name is {changed}"),
        );
        return Ok(());
    }
    let current = settings.load()?.user_name.unwrap_or_else(os_user_name);
    ctx.print(&object(vec![("name", Value::String(current.clone()))]), &current);
    Ok(())
}
