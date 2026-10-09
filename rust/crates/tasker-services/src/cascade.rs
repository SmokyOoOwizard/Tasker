//! Итог правки сущности, которая могла затронуть задачи (`CascadeResult<T>` в .NET): сама сущность, число задач, которым выбор
//! вызвавшего что-то изменил, и число колонок досок. В JSON — плоско: поля сущности плюс `affectedTasks` (и `affectedColumns`,
//! только когда оно не 0).
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq)]
pub struct CascadeResult<T> {
    pub value: T,
    pub affected_tasks: usize,
    pub affected_columns: usize,
}

impl<T> CascadeResult<T> {
    pub fn new(value: T, affected_tasks: usize) -> CascadeResult<T> {
        CascadeResult {
            value,
            affected_tasks,
            affected_columns: 0,
        }
    }

    pub fn with_columns(mut self, affected_columns: usize) -> CascadeResult<T> {
        self.affected_columns = affected_columns;
        self
    }
}

impl<T: serde::Serialize> CascadeResult<T> {
    /// Как пишет `CascadeResultConverter`: свойства сущности, затем `affectedTasks`, затем `affectedColumns` при ненулевом.
    pub fn to_json(&self) -> Value {
        let mut map = match serde_json::to_value(&self.value) {
            Ok(Value::Object(map)) => map,
            _ => Map::new(),
        };
        map.insert("affectedTasks".into(), Value::from(self.affected_tasks));
        if self.affected_columns > 0 {
            map.insert("affectedColumns".into(), Value::from(self.affected_columns));
        }
        Value::Object(map)
    }
}
