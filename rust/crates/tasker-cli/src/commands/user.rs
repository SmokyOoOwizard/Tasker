//! `tasker user …` и `tasker agent …` (`UserCommands` в .NET): у пользователя только имя.
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use tasker_core::ShortId;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::model::{User, UserKind};
use tasker_services::users::UpdateUser;

/// По id, имени или короткому id; агент и человек — разные сущности с общими именами.
fn find(ctx: &Context<'_>, reference: &str, kind: UserKind) -> Result<User> {
    let users = ctx.session().workspace().users();
    let what = if kind == UserKind::Agent { "agent" } else { "user" };
    let mut user = match parse_guid(reference) {
        Some(id) => users.get_by_id(&id)?,
        None => None,
    };
    if user.is_none() {
        user = users.get_by_username(reference)?;
    }
    if user.is_none() && ShortId::try_key(Some(reference)).is_some() {
        let all = users.get_all(Some(kind))?;
        user = refs::by_id_prefix(&all, reference, |x| x.id, what)?.cloned();
    }
    match user {
        Some(found) if found.kind == kind => Ok(found),
        _ => Err(CliError::new(format!("No {what} '{reference}'"))),
    }
}

pub fn run(ctx: &mut Context<'_>, group: &str, command: &str, leaf: &ArgMatches) -> Result<()> {
    let agent = group == "agent";
    let kind = if agent { UserKind::Agent } else { UserKind::Human };
    let what = if agent { "agent" } else { "user" };
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let name = kit::required(leaf, "name");
            let user = if agent {
                ws.agents().create(&name)?
            } else {
                ws.users().create(&name)?
            };
            ctx.print(
                &entities::user(&user),
                &format!("Created {what} '{}' {}", user.username, guid_d(&user.id)),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ws.users().get_range(Some(kind), page)?))?;
            ctx.print_list(&list, entities::user, |x| kit::row(&x.id, &[&x.username]));
        }
        "get" => {
            let user = find(ctx, &kit::required(leaf, "id-or-name"), kind)?;
            let text = kit::fields(&[
                ("id", Some(guid_d(&user.id))),
                ("name", Some(user.username.clone())),
                ("created", Some(user.created_at.format_console())),
                ("version", Some(user.version.clone())),
            ]);
            ctx.print(&entities::user(&user), &text);
        }
        "update" => {
            kit::require_change(leaf, &["name"])?;
            let user = find(ctx, &kit::required(leaf, "id-or-name"), kind)?;
            let command = UpdateUser {
                username: kit::text(leaf, "name"),
                version: Some(kit::text(leaf, "expected-version").unwrap_or(user.version.clone())),
            };
            let updated = if agent {
                ws.agents().update(&user.id, &command)?
            } else {
                ws.users().update(&user.id, &command)?
            }
            .ok_or_else(|| CliError::new(format!("No {what} '{}'", guid_d(&user.id))))?;
            ctx.print(
                &entities::user(&updated),
                &format!("Updated {what} '{}' {}", updated.username, guid_d(&updated.id)),
            );
        }
        "delete" => {
            let user = find(ctx, &kit::required(leaf, "id-or-name"), kind)?;
            let version = kit::text(leaf, "expected-version").unwrap_or(user.version.clone());
            if agent {
                ws.agents().delete(&user.id, Some(&version))?;
            } else {
                ws.users().delete(&user.id, Some(&version))?;
            }
            ctx.print(&entities::deleted(&user.id), &kit::deleted(what, &user.username, &user.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
