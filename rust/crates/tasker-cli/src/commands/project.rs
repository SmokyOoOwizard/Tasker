//! `tasker project …` (`ProjectCommands` в .NET).
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use clap::ArgMatches;
use tasker_core::ids::guid_d;
use tasker_services::project::{CreateProject, ProjectStats, UpdateProject};

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    match command {
        "create" => {
            let project = ctx.session().workspace().projects().create(&CreateProject {
                name: kit::required(leaf, "name"),
            })?;
            ctx.print(
                &entities::project(&project),
                &format!("Created project '{}' {}", project.name, guid_d(&project.id)),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ctx.session().workspace().projects().get_range(page)?))?;
            ctx.print_list(&list, entities::project, |x| kit::row(&x.id, &[&x.name]));
        }
        "get" => {
            let project = ctx.session().find_project(&kit::required(leaf, "id-or-name"))?;
            let text = kit::fields(&[
                ("id", Some(guid_d(&project.id))),
                ("name", Some(project.name.clone())),
                ("created", Some(project.created_at.format_console())),
                ("version", Some(project.version.clone())),
            ]);
            ctx.print(&entities::project(&project), &text);
        }
        "update" => {
            kit::require_change(leaf, &["name"])?;
            let project = ctx.session().find_project(&kit::required(leaf, "id-or-name"))?;
            let updated = ctx
                .session()
                .workspace()
                .projects()
                .update(
                    &project.id,
                    &UpdateProject {
                        name: kit::text(leaf, "name"),
                        version: Some(kit::text(leaf, "expected-version").unwrap_or(project.version.clone())),
                    },
                )?
                .ok_or_else(|| CliError::new(format!("No project '{}'", guid_d(&project.id))))?;
            ctx.print(
                &entities::project(&updated),
                &format!("Updated project '{}' {}", updated.name, guid_d(&updated.id)),
            );
        }
        "delete" => {
            let project = ctx.session().find_project(&kit::required(leaf, "id-or-name"))?;
            if !kit::flag(leaf, "yes") {
                let stats = ctx
                    .session()
                    .workspace()
                    .projects()
                    .get_stats(&project.id)?
                    .ok_or_else(|| CliError::new(format!("No project '{}'", guid_d(&project.id))))?;
                let summary = summary(&project.name, &stats);
                if !ctx.is_interactive {
                    return Err(CliError::new(format!("{summary} Repeat with --yes to confirm")));
                }
                let _ = writeln!(ctx.prompts(), "{summary}");
                let _ = write!(ctx.prompts(), "Delete project '{}'? [y/N] ", project.name);
                let _ = ctx.prompts().flush();
                let mut answer = String::new();
                let _ = std::io::stdin().read_line(&mut answer);
                let answer = answer.trim();
                if !answer.eq_ignore_ascii_case("y") && !answer.eq_ignore_ascii_case("yes") {
                    // Ввод закончился без перевода строки (EOF) — курсор остался после вопроса.
                    let _ = writeln!(ctx.prompts());
                    return Err(CliError::new("Cancelled, nothing was deleted"));
                }
            }
            let version = kit::text(leaf, "expected-version").unwrap_or(project.version.clone());
            ctx.session().workspace().projects().delete(&project.id, Some(&version))?;
            ctx.print(
                &entities::deleted(&project.id),
                &kit::deleted("project", &project.name, &project.id),
            );
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}

/// Что пропадёт вместе с проектом; нулевые количества не показываются.
fn summary(name: &str, stats: &ProjectStats) -> String {
    let parts: Vec<String> = [
        (stats.tasks, "task", "tasks"),
        (stats.boards, "board", "boards"),
        (stats.statuses, "status", "statuses"),
        (stats.status_sets, "status set", "status sets"),
        (stats.task_types, "task type", "task types"),
        (stats.series, "series", "series"),
        (stats.link_types, "link type", "link types"),
    ]
    .iter()
    .filter(|(count, _, _)| *count > 0)
    .map(|(count, one, many)| format!("{count} {}", if *count == 1 { one } else { many }))
    .collect();
    if parts.is_empty() {
        format!("Project '{name}' is empty and will be deleted. This cannot be undone.")
    } else {
        format!(
            "Project '{name}' will be deleted with everything in it: {}. This cannot be undone.",
            parts.join(", ")
        )
    }
}
