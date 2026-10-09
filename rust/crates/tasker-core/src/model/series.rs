use crate::error::{Result, TaskerError};
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq)]
pub struct Series {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub prefix: String,
    pub version: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TaskSeriesNumber {
    pub series_id: Uuid,
    pub number: i32,
}

/// Префикс серии: 1–20 латинских букв и цифр, регистр значим (`SeriesPrefix` в .NET).
pub struct SeriesPrefix;

impl SeriesPrefix {
    pub const MAX_LENGTH: usize = 20;

    pub fn is_valid(prefix: Option<&str>) -> bool {
        matches!(prefix, Some(p) if !p.is_empty() && p.len() <= Self::MAX_LENGTH && p.bytes().all(|b| b.is_ascii_alphanumeric()))
    }

    pub fn validate(prefix: Option<&str>) -> Result<String> {
        if Self::is_valid(prefix) {
            Ok(prefix.unwrap().to_string())
        } else {
            Err(TaskerError::validation(format!(
                "Series prefix must be 1-{} Latin letters or digits",
                Self::MAX_LENGTH
            )))
        }
    }
}
