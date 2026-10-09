//! Ошибки домена — те же три класса, что в .NET (`TaskerValidationException`, `TaskerNotFoundException`,
//! `TaskerConflictException` с кодом): по ним консоль и MCP выбирают текст (`Not found:`, `In use:`, `[modified]`…).
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConflictCode {
    InUse,
    Modified,
    Locked,
    UnsupportedFormat,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TaskerError {
    Validation(String),
    NotFound(String),
    Conflict { code: ConflictCode, message: String },
}

impl TaskerError {
    pub fn validation(message: impl Into<String>) -> Self {
        Self::Validation(message.into())
    }

    pub fn not_found(message: impl Into<String>) -> Self {
        Self::NotFound(message.into())
    }

    pub fn conflict(code: ConflictCode, message: impl Into<String>) -> Self {
        Self::Conflict {
            code,
            message: message.into(),
        }
    }

    pub fn message(&self) -> &str {
        match self {
            Self::Validation(m) | Self::NotFound(m) => m,
            Self::Conflict { message, .. } => message,
        }
    }
}

impl fmt::Display for TaskerError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.message())
    }
}

impl std::error::Error for TaskerError {}

pub type Result<T> = std::result::Result<T, TaskerError>;
