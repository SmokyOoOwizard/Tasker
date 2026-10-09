//! `AppDirectories`: каталоги данных самого Tasker (не рабочих папок) — одни и те же для десктопа, консоли и демона MCP, поэтому
//! настройки и состояние демона у них общие. `TASKER_HOME` переопределяет каталог (тесты, портативная установка).
use crate::io::full_path;
use crate::settings::user_path::{expand_user_path, home_dir};
use std::path::PathBuf;

pub const HOME_VARIABLE: &str = "TASKER_HOME";

/// `~/Library/Application Support/Tasker` (macOS), `%LOCALAPPDATA%\Tasker` (Windows), `$XDG_DATA_HOME/Tasker` или
/// `~/.local/share/Tasker` (Linux); `TASKER_HOME` (с раскрытием `~`) — вместо всего этого.
pub fn data_dir() -> PathBuf {
    if let Ok(home) = std::env::var(HOME_VARIABLE)
        && !home.is_empty()
    {
        return full_path(expand_user_path(&home));
    }
    local_application_data().join("Tasker")
}

pub fn logs_dir() -> PathBuf {
    data_dir().join("logs")
}

/// Состояние демона MCP: блокировка единственного экземпляра и файл с адресом.
pub fn daemon_dir() -> PathBuf {
    data_dir().join("mcp")
}

/// `Environment.GetFolderPath(LocalApplicationData, DoNotVerify)`.
fn local_application_data() -> PathBuf {
    if cfg!(windows) {
        PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_default())
    } else if cfg!(target_os = "macos") {
        PathBuf::from(home_dir()).join("Library").join("Application Support")
    } else {
        match std::env::var("XDG_DATA_HOME") {
            Ok(xdg) if xdg.starts_with('/') => PathBuf::from(xdg),
            _ => PathBuf::from(home_dir()).join(".local").join("share"),
        }
    }
}
