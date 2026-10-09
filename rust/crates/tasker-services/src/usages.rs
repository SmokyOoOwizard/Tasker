//! Сбор мест, где используется сущность, в одну ошибку 409 (`Usages` в .NET): пользователь сразу видит всё, что мешает удалению
//! или изменению.
use crate::error::{Error, Result};
use tasker_core::model::{Board, BoardColumn};

pub struct Usages {
    subject: String,
    items: Vec<String>,
}

impl Usages {
    pub fn new(subject: impl Into<String>) -> Usages {
        Usages {
            subject: subject.into(),
            items: Vec::new(),
        }
    }

    pub fn add(&mut self, usage: impl Into<String>) {
        self.items.push(usage.into());
    }

    pub fn add_tasks(&mut self, count: usize) {
        if count > 0 {
            self.items.push(format!("{count} task(s)"));
        }
    }

    pub fn add_board_column(&mut self, board: &Board, column: &BoardColumn) {
        self.items.push(format!("board '{}' (column '{}')", board.name, column.name));
    }

    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    /// `{subject} is used by {a; b} and cannot be {action}` — конфликт `InUse`.
    pub fn throw_if_any(&self, action: &str) -> Result<()> {
        if self.items.is_empty() {
            Ok(())
        } else {
            Err(Error::in_use(format!(
                "{} is used by {} and cannot be {action}",
                self.subject,
                self.items.join("; ")
            )))
        }
    }
}
