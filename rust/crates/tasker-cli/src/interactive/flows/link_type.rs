use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;

fn bool_choice(w: &mut Wizard<'_, '_>, title: &str, with_default: bool) -> Steps<Option<bool>> {
    let mut items = vec![Item::new("true", "Yes"), Item::new("false", "No")];
    if with_default {
        items.push(Item::new("default", "Default"));
    }
    let choice = w.menu(title, items)?;
    Ok(match choice.key.as_str() {
        "true" => Some(true),
        "false" => Some(false),
        _ => None,
    })
}

pub fn run(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Link type name", "", Check::Required)?;
            let outward = w.text(
                "Outward name: how the link reads for the task it starts from (e.g. 'depends on')",
                "",
                Check::Required,
            )?;
            let inward = w.optional_text(
                "Inward name: how it reads for the task it points to (empty: no direction)",
                "",
                Check::Any,
            )?;
            let cycles = bool_choice(w, "May a chain of links of this type close a cycle", true)?;
            let hierarchical = bool_choice(w, "Is it a parent/child type (an epic includes its children)", true)?;
            let mut argv = cmd!["link-type", "create", name, "--outward", outward];
            if !inward.is_empty() {
                argv.extend(cmd!["--inward", inward]);
            }
            if let Some(cycles) = cycles {
                argv.extend(cmd!["--allow-cycles", cycles]);
            }
            if let Some(hierarchical) = hierarchical {
                argv.extend(cmd!["--hierarchical", hierarchical]);
            }
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Link type", super::link_type_items(w, &project)?)?;
            let link_type = super::link_types(w, &project)?
                .into_iter()
                .find(|t| guid_d(&t.id) == chosen.key)
                .ok_or_else(|| CliError::new(format!("No link type '{}'", chosen.key)))?;
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&link_type.name),
                    Item::new("outward", "Outward name").detail(&link_type.outward_name),
                    Item::new("inward", "Inward name").detail(&link_type.inward_name),
                    Item::new("allow-cycles", "Cycles allowed").detail(if link_type.allow_cycles { "yes" } else { "no" }),
                    Item::new("hierarchical", "Parent/child type").detail(if link_type.hierarchical { "yes" } else { "no" }),
                ],
            )?;
            let mut argv = cmd!["link-type", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &link_type.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                "outward" => {
                    let outward = w.text("New outward name", &link_type.outward_name, Check::Required)?;
                    argv.extend(cmd!["--outward", outward]);
                }
                "inward" => {
                    let inward = w.text("New inward name", &link_type.inward_name, Check::Required)?;
                    argv.extend(cmd!["--inward", inward]);
                }
                "allow-cycles" => {
                    let cycles = bool_choice(w, "May a chain of links of this type close a cycle", false)?.unwrap_or(true);
                    argv.extend(cmd!["--allow-cycles", cycles]);
                }
                _ => {
                    let hierarchical = bool_choice(w, "Is it a parent/child type", false)?.unwrap_or(false);
                    argv.extend(cmd!["--hierarchical", hierarchical]);
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::View => Err(CliError::new("Nothing to do").into()),
        Action::Delete => {
            let link_type = w.select("Link type", super::link_type_items(w, &project)?)?;
            super::confirm_delete(w, "link type", &link_type.label)?;
            let mut argv = cmd!["link-type", "delete", link_type.key];
            argv.extend(w.expected_version(&link_type));
            Ok(argv)
        }
    }
}
