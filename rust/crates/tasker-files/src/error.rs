//! Ошибки чтения файла: неподдерживаемый формат (`UnsupportedFormatException` в .NET — `TaskerConflictException` с кодом
//! `UnsupportedFormat`), неразрешённый конфликт слияния git (индекс .NET проверяет маркеры до разбора) и ошибка самого YAML.
use std::fmt;
use tasker_core::{ConflictCode, TaskerError};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    /// `{file}: format version N is newer than this Tasker supports (9): update Tasker`, `{file}: formatVersion 'x' is not a
    /// number`, `{file}: unknown field type 'x': update Tasker`, `{file}: unknown field filter operator 'x': update Tasker`.
    UnsupportedFormat(String),
    /// Маркер конфликта git (`<<<<<<<`, `=======`, `>>>>>>>`) в начале строки; `line` — номер строки с первым маркером (с 1).
    MergeConflict { line: usize },
    /// YAML не разбирается или не той формы (не отображение, не Guid, не дата…).
    Yaml(String),
}

impl Error {
    pub fn message(&self) -> String {
        match self {
            Self::UnsupportedFormat(m) | Self::Yaml(m) => m.clone(),
            Self::MergeConflict { line } => format!("Unresolved git merge conflict (line {line})"),
        }
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message())
    }
}

impl std::error::Error for Error {}

/// Как ошибка домена: неподдерживаемый формат — конфликт с кодом `UnsupportedFormat` (консоль печатает `Unsupported format: …`),
/// остальное — ошибка валидации с тем же текстом.
impl From<Error> for TaskerError {
    fn from(error: Error) -> Self {
        match error {
            Error::UnsupportedFormat(m) => TaskerError::conflict(ConflictCode::UnsupportedFormat, m),
            other => TaskerError::validation(other.message()),
        }
    }
}

pub type Result<T> = std::result::Result<T, Error>;
