//! Ссылка на задачу: полный id, префикс id (8+ hex, только если разрешено) или `PREFIX-N` (`TaskReference`).
use crate::error::{Result, TaskerError};
use crate::ids::{ShortId, parse_guid};
use crate::model::SeriesPrefix;
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TaskReference {
    Id(Uuid),
    IdPrefix(String),
    Series { prefix: String, number: i32 },
}

impl TaskReference {
    pub fn try_parse(text: Option<&str>, allow_id_prefix: bool) -> Option<Self> {
        let trimmed = text.map(str::trim).unwrap_or("");
        if trimmed.is_empty() {
            return None;
        }
        if let Some(id) = parse_guid(trimmed) {
            return Some(Self::Id(id));
        }
        if allow_id_prefix && let Some(key) = ShortId::try_key(Some(trimmed)) {
            return Some(Self::IdPrefix(key));
        }
        let dash = trimmed.rfind('-')?;
        if dash == 0 || dash == trimmed.len() - 1 {
            return None;
        }
        let prefix = &trimmed[..dash];
        let digits = &trimmed[dash + 1..];
        if !SeriesPrefix::is_valid(Some(prefix)) || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        let number: i32 = digits.parse().ok()?;
        if number < 1 {
            return None;
        }
        Some(Self::Series {
            prefix: prefix.to_string(),
            number,
        })
    }

    pub fn parse(text: Option<&str>, allow_id_prefix: bool) -> Result<Self> {
        Self::try_parse(text, allow_id_prefix).ok_or_else(|| {
            let shown = text.unwrap_or("");
            TaskerError::validation(if allow_id_prefix {
                format!(
                    "'{shown}' is neither a task id (full, or its first {}+ hex characters) nor a reference like TSK-5",
                    ShortId::LENGTH
                )
            } else {
                format!("'{shown}' is neither a task id nor a reference like TSK-5")
            })
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn references_are_parsed_like_dotnet() {
        assert_eq!(
            TaskReference::try_parse(Some(" TSK-5 "), false),
            Some(TaskReference::Series {
                prefix: "TSK".into(),
                number: 5
            })
        );
        assert_eq!(
            TaskReference::try_parse(Some("ABCDEF12-7"), true),
            Some(TaskReference::Series {
                prefix: "ABCDEF12".into(),
                number: 7
            })
        );
        assert_eq!(TaskReference::try_parse(Some("70053344"), false), None);
        assert_eq!(
            TaskReference::try_parse(Some("70053344"), true),
            Some(TaskReference::IdPrefix("70053344".into()))
        );
        assert_eq!(TaskReference::try_parse(Some("7005334"), true), None);
        assert_eq!(TaskReference::try_parse(Some("TSK-0"), false), None);
        assert_eq!(TaskReference::try_parse(Some("TSK-"), false), None);
        assert_eq!(TaskReference::try_parse(Some("-5"), false), None);
        assert_eq!(TaskReference::try_parse(Some("TSK-99999999999"), false), None);
        assert!(matches!(
            TaskReference::try_parse(Some("70053344-2907-4f88-b86c-40a39484c33d"), false),
            Some(TaskReference::Id(_))
        ));
        assert_eq!(
            TaskReference::parse(Some("x"), false).unwrap_err().message(),
            "'x' is neither a task id nor a reference like TSK-5"
        );
    }
}
