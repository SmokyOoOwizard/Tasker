//! Усечение описаний для списков (`DescriptionPreview`) и записи списков с усечённым описанием (`StatusListItem`,
//! `TaskTypeListItem`, `TaskListItem`, `TaskTreeItem`).
use crate::error::{Error, Result};
use tasker_core::model::{Status, TaskItem, TaskType};
use unicode_segmentation::UnicodeSegmentation as _;
use uuid::Uuid;

/// Значение `descriptionLength`: полный текст.
pub const FULL: i32 = -1;

/// Предпросмотр по умолчанию в MCP-списках.
pub const MCP_DEFAULT: i32 = 200;

/// Проверка параметра клиента: 0 (без описания), N > 0 или -1 (полный текст); не задан — `default`.
pub fn check(description_length: Option<i32>, default: i32) -> Result<i32> {
    match description_length {
        None => Ok(default),
        Some(n) if n < FULL => Err(Error::validation(format!(
            "descriptionLength: {n} - use 0 (no description), N (first N characters) or -1 (full text)"
        ))),
        Some(n) => Ok(n),
    }
}

/// Первые `length` знаков Unicode описания, не разрезая графемы (кластер, который не влезает, отбрасывается целиком). «…» не
/// добавляется. -1 — без усечения, 0 — пусто. Возвращает текст (None — описания нет) и полную длину в знаках.
pub fn cut(description: Option<&str>, length: i32) -> (Option<String>, usize) {
    let Some(text) = description.filter(|d| !d.is_empty()) else {
        return (description.map(str::to_string), 0);
    };
    let total = text.chars().count();
    if length < 0 || length as usize >= total {
        return (Some(text.to_string()), total);
    }
    if length == 0 {
        return (Some(String::new()), total);
    }
    let limit = length as usize;
    let mut runes = 0;
    let mut end = 0;
    for (at, cluster) in text.grapheme_indices(true) {
        let count = cluster.chars().count();
        if runes + count > limit {
            break;
        }
        runes += count;
        end = at + cluster.len();
    }
    (Some(text[..end].to_string()), total)
}

/// Статус в списке: описание может быть усечено.
#[derive(Debug, Clone, PartialEq)]
pub struct StatusListItem {
    pub status: Status,
    pub description_truncated: bool,
    pub description_length: usize,
}

impl StatusListItem {
    pub fn new(status: Status, description_length: i32) -> StatusListItem {
        let (text, length) = cut(Some(&status.description), description_length);
        let description = text.unwrap_or_default();
        let truncated = description.chars().count() < status.description.chars().count();
        StatusListItem {
            status: Status { description, ..status },
            description_truncated: truncated,
            description_length: length,
        }
    }
}

/// Тип задачи в списке: описание может быть усечено.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskTypeListItem {
    pub task_type: TaskType,
    pub description_truncated: bool,
    pub description_length: usize,
}

impl TaskTypeListItem {
    pub fn new(task_type: TaskType, description_length: i32) -> TaskTypeListItem {
        let (text, length) = cut(Some(&task_type.description), description_length);
        let description = text.unwrap_or_default();
        let truncated = description.chars().count() < task_type.description.chars().count();
        TaskTypeListItem {
            task_type: TaskType { description, ..task_type },
            description_truncated: truncated,
            description_length: length,
        }
    }
}

/// Задача в списке (`TaskListItem`): описание усечено, число связей (исходящих и входящих), родители и число дочерних по иерархии.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskListItem {
    pub task: TaskItem,
    pub description_truncated: bool,
    pub description_length: usize,
    pub links_count: usize,
    pub parent_ids: Vec<Uuid>,
    pub child_count: usize,
}

impl TaskListItem {
    pub fn new(task: TaskItem, description_length: i32, links_count: usize, parent_ids: Vec<Uuid>, child_count: usize) -> TaskListItem {
        let (text, length) = cut(task.description.as_deref(), description_length);
        let truncated = match (&text, &task.description) {
            (Some(t), Some(d)) => t.chars().count() < d.chars().count(),
            _ => false,
        };
        TaskListItem {
            task: TaskItem { description: text, ..task },
            description_truncated: truncated,
            description_length: length,
            links_count,
            parent_ids,
            child_count,
        }
    }
}

/// Запись списка деревом: задача, глубина (0 — верхний уровень) и признак повтора (`(+)` в консоли).
#[derive(Debug, Clone, PartialEq)]
pub struct TaskTreeItem {
    pub item: TaskListItem,
    pub depth: usize,
    pub repeated: bool,
}

/// Страница списка деревом: `total_count` — уникальные задачи, `top_level_count` — сколько из них на верхнем уровне; страница —
/// `limit` задач верхнего уровня с их поддеревьями целиком.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskTreeList {
    pub total_count: usize,
    pub top_level_count: usize,
    pub offset: usize,
    pub limit: usize,
    pub data: Vec<TaskTreeItem>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cut_keeps_graphemes_whole() {
        assert_eq!(cut(None, 5), (None, 0));
        assert_eq!(cut(Some(""), 5), (Some(String::new()), 0));
        assert_eq!(cut(Some("abcdef"), -1), (Some("abcdef".into()), 6));
        assert_eq!(cut(Some("abcdef"), 0), (Some(String::new()), 6));
        assert_eq!(cut(Some("abcdef"), 3), (Some("abc".into()), 6));
        assert_eq!(cut(Some("abcdef"), 6), (Some("abcdef".into()), 6));
        // Эмодзи с модификатором — один кластер из двух знаков: во второй знак не влезает — отбрасывается целиком.
        assert_eq!(cut(Some("a👍🏽b"), 2), (Some("a".into()), 4));
        assert_eq!(cut(Some("a👍🏽b"), 3), (Some("a👍🏽".into()), 4));
        assert_eq!(check(None, FULL).unwrap(), FULL);
        assert!(check(Some(-2), FULL).is_err());
    }
}
