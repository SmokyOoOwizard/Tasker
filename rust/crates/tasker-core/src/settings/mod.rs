//! Глобальные настройки Tasker (`Tasker.Global`) и адресация рабочих областей (`WorkspaceLocation`, `CanonicalPath`):
//! каталоги данных приложения, `settings.json`, общий для десктопа, консоли и демона, и его изменение под `settings.lock`.
//! Слежение за `settings.json` ([`SettingsStore::watch`]) — для демона: добавление и удаление областей применяются на лету.
mod app_directories;
mod canonical_path;
mod global_settings;
mod os_user;
mod store;
mod user_path;
mod watch;
mod workspace_location;

pub use app_directories::{HOME_VARIABLE, daemon_dir, data_dir, logs_dir};
pub use canonical_path::{canonical_path, normalize_root, strip_extended_prefix};
pub use global_settings::{DEFAULT_PORT, GlobalSettings, MAX_USER_NAME_LENGTH, McpSettings, WorkspaceEntry};
pub use os_user::{normalize_user_name, os_user_name};
pub use store::{SettingsError, SettingsStore};
pub use user_path::{expand_user_path, expand_user_path_in};
pub use watch::{SettingsWatch, WATCH_QUIET};
pub use workspace_location::{WorkspaceKind, WorkspaceLocation};
