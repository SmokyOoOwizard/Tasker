use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;
use tasker_core::model::{Board, Status};
use tasker_services::Storage;

const COLUMNS_HINT: &str = "One column per line: Name=status,status (statuses by name)";

fn board_of(w: &Wizard<'_, '_>, project: &uuid::Uuid, key: &str) -> Steps<Board> {
    let id = tasker_core::ids::parse_guid(key).ok_or_else(|| CliError::new("Bad board id"))?;
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<Board>(project, &id)?
        .ok_or_else(|| CliError::new(format!("No board '{key}'")))?)
}

fn columns_text(w: &Wizard<'_, '_>, project: &uuid::Uuid, board: &Board) -> Steps<String> {
    let statuses: Vec<Status> = w.ctx.session.workspace().get_all::<Status>(project)?;
    Ok(board
        .columns
        .iter()
        .map(|c| {
            let names: Vec<String> = c
                .status_ids
                .iter()
                .map(|id| {
                    statuses
                        .iter()
                        .find(|s| s.id == *id)
                        .map(|s| s.name.clone())
                        .unwrap_or_else(|| guid_d(id))
                })
                .collect();
            format!("{}={}", c.name, names.join(","))
        })
        .collect::<Vec<_>>()
        .join("\n"))
}

fn columns(text: &str) -> Steps<Vec<String>> {
    let lines = super::lines(text);
    if lines.is_empty() {
        return Err(CliError::new("A board needs at least one column").into());
    }
    Ok(lines)
}

pub fn run(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Board name", "", Check::Required)?;
            let sets = w.multi("Status sets whose tasks are on the board", super::status_sets(w, &project)?, false)?;
            let text = w.editor("Columns", "", COLUMNS_HINT)?;
            let mut argv = cmd!["board", "create", name, "--status-set"];
            argv.extend(sets.iter().map(|s| s.key.clone()));
            argv.push("--column".into());
            argv.extend(columns(&text)?);
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Board", super::boards(w, &project)?)?;
            let board = board_of(w, &project, &chosen.key)?;
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&board.name),
                    Item::new("status-set", "Status sets").detail(format!("{} now", board.status_set_ids.len())),
                    Item::new("columns", "Columns").detail(format!("{} now", board.columns.len())),
                ],
            )?;
            let mut argv = cmd!["board", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &board.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                "status-set" => {
                    let sets = w.multi("New status sets of the board", super::status_sets(w, &project)?, false)?;
                    argv.push("--status-set".into());
                    argv.extend(sets.iter().map(|s| s.key.clone()));
                }
                _ => {
                    let current = columns_text(w, &project, &board)?;
                    let text = w.editor(
                        "Columns (a column with the name of an existing one keeps its conditions)",
                        &current,
                        COLUMNS_HINT,
                    )?;
                    argv.push("--column".into());
                    argv.extend(columns(&text)?);
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::View => Err(CliError::new("Nothing to do").into()),
        Action::Delete => {
            let board = w.select("Board", super::boards(w, &project)?)?;
            super::confirm_delete(w, "board", &board.label)?;
            let mut argv = cmd!["board", "delete", board.key];
            argv.extend(w.expected_version(&board));
            Ok(argv)
        }
    }
}
