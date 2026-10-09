//! Ошибки инструментов (`McpCall` + `ErrorText` в .NET): текст начинается с кода в квадратных скобках — `[invalid]`, `[forbidden]`,
//! `[not_found]`, `[modified]`, `[locked]`, `[unsupported_format]`, `[in_use]`, `[unavailable]`, `[failed]` — и уходит агенту
//! результатом с `isError: true`, без префикса SDK. Необработанная ошибка (ввод-вывод, аргументы не по схеме) — как у .NET SDK:
//! `An error occurred invoking '<tool>'.`
use tasker_core::{ConflictCode, TaskerError};
use tasker_services::Error;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ToolError {
    /// Текст с кодом в начале: `[not_found] Task x not found`.
    Coded(String),
    /// Ошибка, которую .NET SDK не показывает агенту: текст подставляется по имени инструмента.
    Failed,
}

impl ToolError {
    pub fn invalid(message: impl AsRef<str>) -> ToolError {
        ToolError::Coded(format!("[invalid] {}", message.as_ref()))
    }

    pub fn forbidden(message: impl AsRef<str>) -> ToolError {
        ToolError::Coded(format!("[forbidden] {}", message.as_ref()))
    }

    pub fn not_found(message: impl AsRef<str>) -> ToolError {
        ToolError::Coded(format!("[not_found] {}", message.as_ref()))
    }

    pub fn unavailable(message: impl AsRef<str>) -> ToolError {
        ToolError::Coded(format!("[unavailable] {}", message.as_ref()))
    }

    pub fn failed_workspace(message: impl AsRef<str>) -> ToolError {
        ToolError::Coded(format!("[failed] {}", message.as_ref()))
    }

    /// Текст результата с `isError: true`.
    pub fn text(&self, tool: &str) -> String {
        match self {
            ToolError::Coded(text) => text.clone(),
            ToolError::Failed => invoke_error(tool),
        }
    }
}

/// `An error occurred invoking '<tool>'.` — так .NET SDK отвечает на аргументы не по схеме и на необработанное исключение.
pub fn invoke_error(tool: &str) -> String {
    format!("An error occurred invoking '{tool}'.")
}

impl From<Error> for ToolError {
    fn from(error: Error) -> ToolError {
        match error {
            Error::Tasker(e) => e.into(),
            // В .NET ошибка ввода-вывода — необработанное исключение инструмента.
            Error::Io(_) => ToolError::Failed,
        }
    }
}

impl From<TaskerError> for ToolError {
    fn from(error: TaskerError) -> ToolError {
        match error {
            TaskerError::Validation(m) => ToolError::invalid(m),
            TaskerError::NotFound(m) => ToolError::not_found(m),
            TaskerError::Conflict { code, message } => {
                let code = match code {
                    ConflictCode::Modified => "modified",
                    ConflictCode::Locked => "locked",
                    ConflictCode::UnsupportedFormat => "unsupported_format",
                    ConflictCode::InUse => "in_use",
                };
                ToolError::Coded(format!("[{code}] {message}"))
            }
        }
    }
}

impl From<std::io::Error> for ToolError {
    fn from(_: std::io::Error) -> ToolError {
        ToolError::Failed
    }
}

impl From<tasker_files::Error> for ToolError {
    fn from(error: tasker_files::Error) -> ToolError {
        Error::from(error).into()
    }
}

pub type Result<T> = std::result::Result<T, ToolError>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codes_go_first() {
        assert_eq!(
            ToolError::from(Error::not_found("Task x not found")).text("get_task"),
            "[not_found] Task x not found"
        );
        assert_eq!(
            ToolError::from(Error::conflict(ConflictCode::Modified, "changed")).text("t"),
            "[modified] changed"
        );
        assert_eq!(ToolError::from(Error::in_use("used")).text("t"), "[in_use] used");
        assert_eq!(ToolError::from(Error::validation("bad")).text("t"), "[invalid] bad");
        assert_eq!(
            ToolError::from(Error::Io(std::io::Error::other("disk"))).text("get_task"),
            "An error occurred invoking 'get_task'."
        );
    }
}
