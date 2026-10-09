//! Ошибки команд и их текст в stderr (`GlobalOptions.Guard` в .NET): ошибка пользователя (`CliException`) и ошибки домена
//! печатаются одной строкой без стека и дают код выхода 1 — `Not found:`, `In use:`, `Locked:`, `Unsupported format:`,
//! `Modified by someone else:`, остальное — `Error:`.
use tasker_core::settings::SettingsError;
use tasker_core::{ConflictCode, TaskerError};

/// Код выхода при любой ошибке команды.
pub const ERROR_EXIT_CODE: i32 = 1;

#[derive(Debug)]
pub enum CliError {
    /// Ошибка пользователя команды (`CliException`): сообщение выводится как есть.
    Message(String),
    /// Ошибка домена: текст зависит от класса и кода.
    Domain(TaskerError),
    /// Ошибка файловой системы: в .NET это необработанное исключение, здесь — строка `Error:` и код 1.
    Io(std::io::Error),
}

impl CliError {
    pub fn new(message: impl Into<String>) -> CliError {
        CliError::Message(message.into())
    }

    /// Строка для stderr с префиксом.
    pub fn text(&self) -> String {
        match self {
            CliError::Message(m) => format!("Error: {m}"),
            CliError::Io(e) => format!("Error: {e}"),
            CliError::Domain(e) => match e {
                TaskerError::NotFound(m) => format!("Not found: {m}"),
                TaskerError::Validation(m) => format!("Error: {m}"),
                TaskerError::Conflict { code, message } => match code {
                    ConflictCode::InUse => format!("In use: {message}"),
                    ConflictCode::Locked => format!("Locked: {message}"),
                    ConflictCode::UnsupportedFormat => format!("Unsupported format: {message}"),
                    ConflictCode::Modified => format!("Modified by someone else: {message}"),
                },
            },
        }
    }
}

impl std::fmt::Display for CliError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.text())
    }
}

impl std::error::Error for CliError {}

impl From<TaskerError> for CliError {
    fn from(e: TaskerError) -> Self {
        CliError::Domain(e)
    }
}

impl From<std::io::Error> for CliError {
    fn from(e: std::io::Error) -> Self {
        CliError::Io(e)
    }
}

impl From<SettingsError> for CliError {
    fn from(e: SettingsError) -> Self {
        match e {
            SettingsError::Settings(m) => CliError::Message(m),
            SettingsError::Io(e) => CliError::Io(e),
        }
    }
}

pub type Result<T> = std::result::Result<T, CliError>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn texts_match_the_dotnet_console() {
        assert_eq!(CliError::new("No project 'Nope'").text(), "Error: No project 'Nope'");
        assert_eq!(CliError::from(TaskerError::not_found("Task x")).text(), "Not found: Task x");
        assert_eq!(CliError::from(TaskerError::validation("bad")).text(), "Error: bad");
        assert_eq!(
            CliError::from(TaskerError::conflict(ConflictCode::InUse, "Status is used")).text(),
            "In use: Status is used"
        );
        assert_eq!(
            CliError::from(TaskerError::conflict(ConflictCode::Locked, "by bob")).text(),
            "Locked: by bob"
        );
        assert_eq!(
            CliError::from(TaskerError::conflict(ConflictCode::UnsupportedFormat, "99")).text(),
            "Unsupported format: 99"
        );
        assert_eq!(
            CliError::from(TaskerError::conflict(ConflictCode::Modified, "version")).text(),
            "Modified by someone else: version"
        );
    }
}
