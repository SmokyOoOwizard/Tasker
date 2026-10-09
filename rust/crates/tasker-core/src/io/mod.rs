//! Файловый слой `.tasker` и настроек (`Tasker.Core.IO`): атомарная запись, короткие блокировки между процессами, окончания
//! строк, правила сравнения путей и имён файлов Windows.
mod atomic_file;
mod file_lock;
mod line_endings;
mod path_rules;
mod windows_names;

pub use atomic_file::{DELAYS, delete, is_transient, move_file, read_all_bytes, read_all_text, retry, write_all_bytes, write_all_text};
pub use file_lock::{DEFAULT_TIMEOUT, FileLock};
pub use line_endings::{bytes_for_hash, line_ending_of, strip_bom, to_lf};
pub use path_rules::{full_path, full_path_in, is_inside, is_inside_with, path_key, same_path};
pub use windows_names::{INVALID_CHARS, is_reserved, is_valid, problem};
