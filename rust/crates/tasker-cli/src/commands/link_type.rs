//! `tasker link-type …` (`LinkTypeCommands` в .NET).
use super::find_link_type;
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use clap::ArgMatches;
use tasker_core::ids::guid_d;
use tasker_core::model::LinkType;
use tasker_services::link_type::{CreateLinkType, UpdateLinkType};

fn sides(t: &LinkType) -> String {
    if t.is_symmetric() {
        t.outward_name.clone()
    } else {
        format!("{} / {}", t.outward_name, t.inward_name)
    }
}

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let created = ws.link_types().create(
                &project_id,
                &CreateLinkType {
                    name: kit::required(leaf, "name"),
                    outward_name: kit::required(leaf, "outward"),
                    inward_name: kit::text(leaf, "inward"),
                    allow_cycles: kit::optional_bool(leaf, "allow-cycles"),
                    hierarchical: kit::optional_bool(leaf, "hierarchical"),
                },
            )?;
            ctx.print(
                &entities::link_type(&created),
                &format!("Created link type '{}' ({}) {}", created.name, sides(&created), guid_d(&created.id)),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ws.link_types().get_range(&project_id, page)?))?;
            ctx.print_list(&list, entities::link_type, |x| kit::row(&x.id, &[&x.name, &sides(x)]));
        }
        "get" => {
            let t = find_link_type(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let text = kit::fields(&[
                ("id", Some(guid_d(&t.id))),
                ("name", Some(t.name.clone())),
                ("outward", Some(t.outward_name.clone())),
                ("inward", Some(t.inward_name.clone())),
                ("allowCycles", Some(kit::bool_text(t.allow_cycles))),
                ("hierarchical", Some(kit::bool_text(t.hierarchical))),
                ("version", Some(t.version.clone())),
            ]);
            ctx.print(&entities::link_type(&t), &text);
        }
        "update" => {
            kit::require_change(leaf, &["name", "outward", "inward", "allow-cycles", "hierarchical"])?;
            let t = find_link_type(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let updated = ws
                .link_types()
                .update(
                    &project_id,
                    &t.id,
                    &UpdateLinkType {
                        name: kit::text(leaf, "name"),
                        outward_name: kit::text(leaf, "outward"),
                        inward_name: kit::text(leaf, "inward"),
                        version: Some(kit::text(leaf, "expected-version").unwrap_or(t.version.clone())),
                        allow_cycles: kit::optional_bool(leaf, "allow-cycles"),
                        hierarchical: kit::optional_bool(leaf, "hierarchical"),
                    },
                )?
                .ok_or_else(|| CliError::new(format!("No link type '{}'", guid_d(&t.id))))?;
            ctx.print(
                &entities::link_type(&updated),
                &format!("Updated link type '{}' ({}) {}", updated.name, sides(&updated), guid_d(&updated.id)),
            );
        }
        "delete" => {
            let t = find_link_type(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(t.version.clone());
            ws.link_types().delete(&project_id, &t.id, Some(&version))?;
            ctx.print(&entities::deleted(&t.id), &kit::deleted("link type", &t.name, &t.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
