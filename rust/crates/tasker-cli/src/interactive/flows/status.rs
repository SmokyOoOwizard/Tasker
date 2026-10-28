use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::model::{Status, StatusSet};
use tasker_services::Storage;

fn status_of(w: &Wizard<'_, '_>, project: &uuid::Uuid, key: &str) -> Steps<Status> {
    let id = tasker_core::ids::parse_guid(key).ok_or_else(|| CliError::new("Bad status id"))?;
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<Status>(project, &id)?
        .ok_or_else(|| CliError::new(format!("No status '{key}'")))?)
}

pub fn run_status(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Status name", "", Check::Required)?;
            let color = w.text("Color (#RRGGBB)", "#808080", Check::Color)?;
            let description = w.editor("Description (optional)", "", "What the status means and when to use it")?;
            let mut argv = cmd!["status", "create", name, "--color", color];
            if !description.is_empty() {
                argv.extend(cmd!["-d", description]);
            }
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Status", super::statuses(w, &project)?)?;
            let status = status_of(w, &project, &chosen.key)?;
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&status.name),
                    Item::new("color", "Color").detail(&status.color),
                    Item::new("description", "Description").detail(status.description.lines().next().unwrap_or_default()),
                ],
            )?;
            let mut argv = cmd!["status", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &status.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                "color" => {
                    let color = w.text("New color (#RRGGBB)", &status.color, Check::Color)?;
                    argv.extend(cmd!["--color", color]);
                }
                _ => {
                    let description = w.editor("Description", &status.description, "An empty text clears the description")?;
                    argv.extend(cmd!["-d", description]);
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::View => Err(CliError::new("Nothing to do").into()),
        Action::Delete => {
            let status = w.select("Status", super::statuses(w, &project)?)?;
            super::confirm_delete(w, "status", &status.label)?;
            let mut argv = cmd!["status", "delete", status.key];
            argv.extend(w.expected_version(&status));
            Ok(argv)
        }
    }
}

pub fn run_status_set(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Status set name", "", Check::Required)?;
            let statuses = w.multi("Statuses of the set, in order of marking", super::statuses(w, &project)?, false)?;
            let mut argv = cmd!["status-set", "create", name];
            for status in statuses {
                argv.extend(cmd!["--status", status.key]);
            }
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Status set", super::status_sets(w, &project)?)?;
            let id = tasker_core::ids::parse_guid(&chosen.key).ok_or_else(|| CliError::new("Bad status set id"))?;
            let set = w
                .ctx
                .session
                .workspace()
                .get_by_id::<StatusSet>(&project, &id)?
                .ok_or_else(|| CliError::new(format!("No status set '{}'", chosen.key)))?;
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&set.name),
                    Item::new("statuses", "Statuses").detail(format!("{} now", set.status_ids.len())),
                ],
            )?;
            let mut argv = cmd!["status-set", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &set.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                _ => {
                    let statuses = w.multi("New statuses of the set, in order of marking", super::statuses(w, &project)?, false)?;
                    for status in statuses {
                        argv.extend(cmd!["--status", status.key]);
                    }
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::View => Err(CliError::new("Nothing to do").into()),
        Action::Delete => {
            let set = w.select("Status set", super::status_sets(w, &project)?)?;
            super::confirm_delete(w, "status set", &set.label)?;
            let mut argv = cmd!["status-set", "delete", set.key];
            argv.extend(w.expected_version(&set));
            Ok(argv)
        }
    }
}
