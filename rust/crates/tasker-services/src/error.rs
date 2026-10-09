//! Ошибка сервиса: ошибка домена (`TaskerValidationException`, `TaskerNotFoundException`, `TaskerConflictException` с кодом) или
//! ввод-вывод (в .NET — необработанное исключение). Консоль по коду выбирает текст (`Not found:`, `In use:`, `[modified]`…).
use std::fmt;
use tasker_core::{ConflictCode, TaskerError};

#[derive(Debug)]
pub enum Error {
    Tasker(TaskerError),
    Io(std::io::Error),
}

impl Error {
    pub fn validation(message: impl Into<String>) -> Error {
        Error::Tasker(TaskerError::validation(message))
    }

    pub fn not_found(message: impl Into<String>) -> Error {
        Error::Tasker(TaskerError::not_found(message))
    }

    pub fn conflict(code: ConflictCode, message: impl Into<String>) -> Error {
        Error::Tasker(TaskerError::conflict(code, message))
    }

    /// `TaskerConflictException` без кода — «используется» (`InUse`).
    pub fn in_use(message: impl Into<String>) -> Error {
        Error::conflict(ConflictCode::InUse, message)
    }

    pub fn message(&self) -> String {
        match self {
            Error::Tasker(e) => e.message().to_string(),
            Error::Io(e) => e.to_string(),
        }
    }

    pub fn is_validation(&self) -> bool {
        matches!(self, Error::Tasker(TaskerError::Validation(_)))
    }

    pub fn is_not_found(&self) -> bool {
        matches!(self, Error::Tasker(TaskerError::NotFound(_)))
    }

    pub fn conflict_code(&self) -> Option<ConflictCode> {
        match self {
            Error::Tasker(TaskerError::Conflict { code, .. }) => Some(*code),
            _ => None,
        }
    }

    pub fn is_in_use(&self) -> bool {
        self.conflict_code() == Some(ConflictCode::InUse)
    }

    pub fn is_modified(&self) -> bool {
        self.conflict_code() == Some(ConflictCode::Modified)
    }

    pub fn is_locked(&self) -> bool {
        self.conflict_code() == Some(ConflictCode::Locked)
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message())
    }
}

impl std::error::Error for Error {}

impl From<TaskerError> for Error {
    fn from(e: TaskerError) -> Self {
        Error::Tasker(e)
    }
}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Error::Io(e)
    }
}

impl From<tasker_files::Error> for Error {
    fn from(e: tasker_files::Error) -> Self {
        Error::Tasker(e.into())
    }
}

pub type Result<T> = std::result::Result<T, Error>;
