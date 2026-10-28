use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;

pub fn run(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    match action {
        Action::Create => {
            let name = w.text("Project name", "", Check::Required)?;
            Ok(cmd!["project", "create", name])
        }
        Action::Change => {
            let project = w.select("Project", projects(w)?)?;
            let name = w.text("New name", &project.label, Check::Required)?;
            let mut argv = cmd!["project", "update", project.key, "--name", name];
            argv.extend(w.expected_version(&project));
            Ok(argv)
        }
        Action::Delete => {
            let project = w.select("Project", projects(w)?)?;
            let id = tasker_core::ids::parse_guid(&project.key).ok_or_else(|| CliError::new("Bad project id"))?;
            let stats = w
                .ctx
                .session
                .workspace()
                .projects()
                .get_stats(&id)?
                .ok_or_else(|| CliError::new(format!("No project '{}'", project.key)))?;
            let summary = crate::commands::project::summary(&project.label, &stats);
            w.confirm("Delete project", &format!("{summary}\n\nDelete project '{}'?", project.label))?;
            let mut argv = cmd!["project", "delete", project.key, "--yes"];
            argv.extend(w.expected_version(&project));
            Ok(argv)
        }
    }
}

fn projects(w: &Wizard<'_, '_>) -> Steps<Vec<Item>> {
    Ok(w.ctx
        .session
        .all_projects()?
        .iter()
        .map(|p| Item::new(guid_d(&p.id), &p.name).version(&p.version))
        .collect())
}
