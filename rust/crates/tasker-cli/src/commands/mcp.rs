//! `tasker mcp …` (`McpCommands` в .NET): демон MCP и его глобальные настройки (`settings.json` общий с десктопом и демоном).
//! Команды управляют установленным `tasker-mcpd` рядом с программой — в том числе .NET-демоном: `run` запускает его в этом
//! терминале, `start` — в фоне, `upgrade` заменяет рабочий процесс демона под супервизором на лету (одиночный — перезапускает).
use crate::context::Context;
use crate::daemon::{self, Controller};
use crate::errors::{CliError, Result};
use crate::json::object;
use crate::kit;
use clap::ArgMatches;
use serde_json::Value;
use tasker_core::io::full_path;
use tasker_core::settings::{SettingsStore, WorkspaceLocation, expand_user_path};

pub fn run(ctx: &mut Context<'_>, path: &[&str], leaf: &ArgMatches) -> Result<()> {
    let settings = SettingsStore::new(None);
    match path {
        ["run"] => {
            ctx.exit_code = daemon::run_foreground(kit::flag(leaf, "detached"))?;
            Ok(())
        }
        ["upgrade"] => {
            let restart = kit::flag(leaf, "restart");
            let timeout = kit::int(leaf, "timeout", daemon::DEFAULT_UPGRADE_TIMEOUT_SECONDS).max(1);
            let program = kit::text(leaf, "daemon")
                .filter(|p| !p.is_empty())
                .map(|p| daemon::daemon_override(&p));
            let outcome = Controller::system().upgrade(restart, timeout, program)?.map_err(|message| {
                CliError::new(format!(
                    "{message}\nThe running MCP server is not changed. Look at the log or restart it with 'tasker mcp upgrade --restart'."
                ))
            })?;
            let text = match &outcome.status {
                None => outcome.message.clone(),
                Some(status) => format!("{}\n{}", outcome.message, daemon::describe(status)),
            };
            ctx.print(
                &object(vec![
                    ("upgraded", Value::String(outcome.kind.json_name().into())),
                    ("message", Value::String(outcome.message.clone())),
                    ("status", outcome.status.as_ref().map(|s| s.value.clone()).unwrap_or(Value::Null)),
                ]),
                &text,
            );
            if outcome.kind == daemon::UpgradeKind::NotRunning {
                ctx.exit_code = 3;
            }
            Ok(())
        }
        ["start"] => {
            let (status, already) = Controller::system().start()?;
            ctx.print(
                &object(vec![("alreadyRunning", Value::Bool(already)), ("status", status.value.clone())]),
                &format!(
                    "{}\n{}",
                    if already {
                        "The MCP server is already running"
                    } else {
                        "The MCP server is started"
                    },
                    daemon::describe(&status)
                ),
            );
            Ok(())
        }
        ["stop"] => {
            let was_running = Controller::system().stop()?;
            ctx.print(
                &object(vec![("stopped", Value::Bool(was_running))]),
                if was_running {
                    "The MCP server is stopped"
                } else {
                    "The MCP server is not running"
                },
            );
            Ok(())
        }
        ["restart"] => {
            let status = Controller::system().restart()?;
            ctx.print(
                &object(vec![("status", status.value.clone())]),
                &format!("The MCP server is restarted\n{}", daemon::describe(&status)),
            );
            Ok(())
        }
        ["status"] => {
            let controller = Controller::system();
            let status = daemon::get_status();
            let autostart = controller.autostart();
            let autostart_text = if autostart.enabled {
                format!(
                    "autostart: enabled ({}){}",
                    autostart.manager,
                    if autostart.loaded { "" } else { ", service not loaded" }
                )
            } else {
                "autostart: off".to_string()
            };
            let text = match &status {
                None => "The MCP server is not running".to_string(),
                Some(status) => daemon::describe(status),
            };
            ctx.print(
                &object(vec![
                    ("daemon", status.as_ref().map(|s| s.value.clone()).unwrap_or(Value::Null)),
                    ("autostart", autostart.json()),
                ]),
                &format!("{text}\n{autostart_text}"),
            );
            if status.is_none() {
                ctx.exit_code = 3;
            }
            Ok(())
        }
        ["autostart", "enable"] => {
            let controller = Controller::system();
            let status = controller.enable_autostart()?;
            ctx.print(
                &object(vec![("autostart", Value::Bool(true)), ("status", status.value.clone())]),
                &format!(
                    "Autostart is enabled ({})\n{}",
                    controller.service.name(),
                    daemon::describe(&status)
                ),
            );
            Ok(())
        }
        ["autostart", "disable"] => {
            let status = Controller::system().disable_autostart()?;
            ctx.print(
                &object(vec![
                    ("autostart", Value::Bool(false)),
                    ("status", status.as_ref().map(|s| s.value.clone()).unwrap_or(Value::Null)),
                ]),
                &match &status {
                    None => "Autostart is disabled".to_string(),
                    Some(status) => format!("Autostart is disabled\n{}", daemon::describe(status)),
                },
            );
            Ok(())
        }
        ["autostart", "status"] => {
            let info = Controller::system().autostart();
            ctx.print(
                &info.json(),
                &if info.enabled {
                    format!(
                        "enabled ({}){}",
                        info.manager,
                        if info.loaded { "" } else { ", service not loaded" }
                    )
                } else {
                    "off".to_string()
                },
            );
            Ok(())
        }
        ["workspace", "add"] => {
            let location = locate(leaf.get_one::<String>("path").map(String::as_str))?;
            let (entry, added) = settings.add_workspace(&location)?;
            ctx.print(
                &object(vec![("added", Value::Bool(added)), ("workspace", entry_json(&entry))]),
                &if added {
                    format!("Added {} workspace {}", entry.kind.json_name(), entry.path)
                } else {
                    format!("Already in the list: {}", entry.path)
                },
            );
            Ok(())
        }
        ["workspace", "remove"] => {
            // Папку могли уже удалить с диска — тогда ищем в списке по пути, любого вида.
            let full = full_path_of(leaf.get_one::<String>("path").map(String::as_str))?;
            let candidates = if std::path::Path::new(&full).exists() {
                vec![locate(Some(&full))?]
            } else {
                vec![WorkspaceLocation::files(&full), WorkspaceLocation::sqlite(&full)]
            };
            for location in &candidates {
                if settings.remove_workspace(location)? {
                    ctx.print(
                        &object(vec![("removed", Value::String(location.path().into()))]),
                        &format!("Removed {}", location.path()),
                    );
                    return Ok(());
                }
            }
            Err(CliError::new(format!("Not in the list: {}", candidates[0].path())))
        }
        ["workspace", "list"] => {
            let entries = settings.load()?.mcp.workspaces;
            ctx.print_all(
                || Value::Array(entries.iter().map(entry_json).collect()),
                &entries,
                |x| vec![x.kind.json_name().to_string(), x.path.clone()],
            );
            Ok(())
        }
        ["config"] => {
            let mcp = settings.load()?.mcp;
            ctx.print(
                &object(vec![
                    ("file", Value::String(settings.file_path().to_string_lossy().into_owned())),
                    (
                        "mcp",
                        object(vec![
                            ("port", Value::from(mcp.port)),
                            ("workspaces", Value::Array(mcp.workspaces.iter().map(entry_json).collect())),
                        ]),
                    ),
                ]),
                &format!(
                    "file:       {}\nport:       {}\nworkspaces: {}",
                    settings.file_path().display(),
                    mcp.port,
                    mcp.workspaces.len()
                ),
            );
            Ok(())
        }
        ["port"] => {
            if let Some(value) = kit::int_opt(leaf, "port") {
                settings.set_port(value as i32)?;
                ctx.print(
                    &object(vec![("port", Value::from(value))]),
                    &format!("Port is {value}: a running MCP server applies it after a restart"),
                );
                return Ok(());
            }
            let current = settings.load()?.mcp.port;
            ctx.print(&object(vec![("port", Value::from(current))]), &current.to_string());
            Ok(())
        }
        _ => Err(CliError::new("not implemented in this build")),
    }
}

fn entry_json(entry: &tasker_core::settings::WorkspaceEntry) -> Value {
    object(vec![
        ("kind", Value::String(entry.kind.json_name().into())),
        ("path", Value::String(entry.path.clone())),
    ])
}

fn full_path_of(path: Option<&str>) -> Result<String> {
    let text = match path {
        Some(path) => expand_user_path(path),
        None => std::env::current_dir()?.to_string_lossy().into_owned(),
    };
    Ok(full_path(text).to_string_lossy().into_owned())
}

/// Существующая папка или файл SQLite; без пути — текущая папка.
fn locate(path: Option<&str>) -> Result<WorkspaceLocation> {
    let full = full_path_of(path)?;
    let meta = std::fs::metadata(&full).map_err(|_| CliError::new(format!("Not found: {full}")))?;
    if meta.is_file() {
        Ok(WorkspaceLocation::sqlite(&full))
    } else {
        Ok(WorkspaceLocation::files(&full))
    }
}
