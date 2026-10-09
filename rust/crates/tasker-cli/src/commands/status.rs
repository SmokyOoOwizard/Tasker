//! `tasker status …` и `tasker status-set …` (`StatusCommands` в .NET).
use super::{find_status, find_status_set};
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use tasker_core::ids::guid_d;
use tasker_services::status::{CreateStatus, UpdateStatus};
use tasker_services::status_set::{CreateStatusSet, UpdateStatusSet};

pub fn run_status(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let status = ws.statuses().create(
                &project_id,
                &CreateStatus {
                    name: kit::required(leaf, "name"),
                    color: kit::text(leaf, "color").unwrap_or_else(|| "#808080".to_string()),
                    description: kit::text(leaf, "description"),
                },
            )?;
            ctx.print(
                &entities::status(&status),
                &format!("Created status '{}' {}", status.name, guid_d(&status.id)),
            );
        }
        "list" => {
            let length = kit::description_length(leaf);
            let list = kit::Paging::load(leaf, |page| Ok(ws.statuses().list(&project_id, page, length)?))?;
            ctx.print_list(&list, entities::status_list_item, |x| {
                kit::row(&x.status.id, &[&x.status.name, &x.status.color])
            });
        }
        "get" => {
            let status = find_status(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let text = kit::fields(&[
                ("id", Some(guid_d(&status.id))),
                ("name", Some(status.name.clone())),
                ("color", Some(status.color.clone())),
                ("version", Some(status.version.clone())),
            ]);
            ctx.print(&entities::status(&status), &kit::with_description(text, &status.description));
        }
        "update" => {
            kit::require_change(leaf, &["name", "color", "description"])?;
            let status = find_status(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let updated = ws
                .statuses()
                .update(
                    &project_id,
                    &status.id,
                    &UpdateStatus {
                        name: kit::text(leaf, "name"),
                        color: kit::text(leaf, "color"),
                        version: Some(kit::text(leaf, "expected-version").unwrap_or(status.version.clone())),
                        description: kit::text(leaf, "description"),
                    },
                )?
                .ok_or_else(|| CliError::new(format!("No status '{}'", guid_d(&status.id))))?;
            ctx.print(
                &entities::status(&updated),
                &format!("Updated status '{}' {}", updated.name, guid_d(&updated.id)),
            );
        }
        "delete" => {
            let status = find_status(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(status.version.clone());
            ws.statuses().delete(&project_id, &status.id, Some(&version))?;
            ctx.print(&entities::deleted(&status.id), &kit::deleted("status", &status.name, &status.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}

pub fn run_status_set(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let all = ws.statuses().get_all(&project_id)?;
            let ids = refs::find_all(&all, &kit::values_or_empty(leaf, "status"), |x| x.id, |x| &x.name, "status")?;
            let set = ws.status_sets().create(
                &project_id,
                &CreateStatusSet {
                    name: kit::required(leaf, "name"),
                    status_ids: ids,
                },
            )?;
            ctx.print(
                &entities::status_set(&set),
                &format!("Created status set '{}' {}", set.name, guid_d(&set.id)),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ws.status_sets().get_range(&project_id, page)?))?;
            ctx.print_list(&list, entities::status_set, |x| {
                kit::row(&x.id, &[&x.name, &format!("({} statuses)", x.status_ids.len())])
            });
        }
        "get" => {
            let set = find_status_set(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let all = ws.statuses().get_all(&project_id)?;
            let list: Vec<String> = set
                .status_ids
                .iter()
                .map(|x| format!("  {}", kit::named(&all, x, |s| s.id, |s| &s.name)))
                .collect();
            let text = kit::fields(&[
                ("id", Some(guid_d(&set.id))),
                ("name", Some(set.name.clone())),
                ("version", Some(set.version.clone())),
            ]);
            ctx.print(&entities::status_set(&set), &format!("{text}\nstatuses:\n{}", list.join("\n")));
        }
        "update" => {
            kit::require_change(leaf, &["name", "status"])?;
            let set = find_status_set(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let ids = match kit::values(leaf, "status") {
                Some(references) => {
                    let all = ws.statuses().get_all(&project_id)?;
                    Some(refs::find_all(&all, &references, |x| x.id, |x| &x.name, "status")?)
                }
                None => None,
            };
            let updated = ws
                .status_sets()
                .update(
                    &project_id,
                    &set.id,
                    &UpdateStatusSet {
                        name: kit::text(leaf, "name"),
                        status_ids: ids,
                        version: Some(kit::text(leaf, "expected-version").unwrap_or(set.version.clone())),
                    },
                )?
                .ok_or_else(|| CliError::new(format!("No status set '{}'", guid_d(&set.id))))?;
            ctx.print(
                &entities::status_set(&updated),
                &format!("Updated status set '{}' {}", updated.name, guid_d(&updated.id)),
            );
        }
        "delete" => {
            let set = find_status_set(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(set.version.clone());
            ws.status_sets().delete(&project_id, &set.id, Some(&version))?;
            ctx.print(&entities::deleted(&set.id), &kit::deleted("status set", &set.name, &set.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
