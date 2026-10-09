//! `tasker mcp …` (`McpCommands` в .NET): демон MCP и его глобальные настройки (`settings.json` общий с десктопом и демоном).
//! Сам сервер в этой сборке не запускается (`mcp run` — TSK-137, `mcp upgrade` — TSK-139): команды управляют установленным
//! `tasker-mcpd` рядом с программой — в том числе .NET-демоном.
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
        ["run"] => Err(CliError::new(
            "'tasker mcp run' is not available in this build: the MCP server (tasker-mcpd) is a separate program, start it with 'tasker mcp start'",
        )),
        ["upgrade"] => Err(CliError::new(
            "'tasker mcp upgrade' is not available in this build: stop and start the MCP server with 'tasker mcp restart'",
        )),
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
