//! Аргументы вызова после проверки по схеме ([`crate::schema`]): типизированный доступ, как связывание параметров в .NET SDK —
//! отсутствующий или `null` необязательный параметр — значение по умолчанию (`None`), остальное — по типу. Несоответствие (его
//! схема уже отсеяла) — [`ToolError::Failed`], то есть `An error occurred invoking`.
use crate::error::{Result, ToolError};
use serde_json::{Map, Value};
use tasker_core::ids::parse_guid;
use uuid::Uuid;

#[derive(Debug, Clone, Copy)]
pub struct Args<'a>(pub &'a Map<String, Value>);

impl<'a> Args<'a> {
    /// Значение, если оно есть и не `null`.
    pub fn get(&self, name: &str) -> Option<&'a Value> {
        self.0.get(name).filter(|v| !v.is_null())
    }

    pub fn string(&self, name: &str) -> Result<Option<String>> {
        match self.get(name) {
            None => Ok(None),
            Some(Value::String(s)) => Ok(Some(s.clone())),
            Some(_) => Err(ToolError::Failed),
        }
    }

    pub fn required_string(&self, name: &str) -> Result<String> {
        self.string(name)?.ok_or(ToolError::Failed)
    }

    pub fn guid(&self, name: &str) -> Result<Option<Uuid>> {
        match self.string(name)? {
            None => Ok(None),
            Some(text) => parse_guid(&text).map(Some).ok_or(ToolError::Failed),
        }
    }

    pub fn required_guid(&self, name: &str) -> Result<Uuid> {
        self.guid(name)?.ok_or(ToolError::Failed)
    }

    pub fn int(&self, name: &str) -> Result<Option<i32>> {
        match self.get(name) {
            None => Ok(None),
            Some(Value::Number(n)) => n.as_i64().and_then(|i| i32::try_from(i).ok()).map(Some).ok_or(ToolError::Failed),
            Some(_) => Err(ToolError::Failed),
        }
    }

    pub fn int_or(&self, name: &str, default: i32) -> Result<i32> {
        Ok(self.int(name)?.unwrap_or(default))
    }

    pub fn bool(&self, name: &str) -> Result<Option<bool>> {
        match self.get(name) {
            None => Ok(None),
            Some(Value::Bool(b)) => Ok(Some(*b)),
            Some(_) => Err(ToolError::Failed),
        }
    }

    pub fn array(&self, name: &str) -> Result<Option<&'a Vec<Value>>> {
        match self.get(name) {
            None => Ok(None),
            Some(Value::Array(items)) => Ok(Some(items)),
            Some(_) => Err(ToolError::Failed),
        }
    }

    pub fn object(&self, name: &str) -> Result<Option<Args<'a>>> {
        match self.get(name) {
            None => Ok(None),
            Some(Value::Object(object)) => Ok(Some(Args(object))),
            Some(_) => Err(ToolError::Failed),
        }
    }

    pub fn string_array(&self, name: &str) -> Result<Option<Vec<String>>> {
        self.array(name)?
            .map(|items| items.iter().map(string_value).collect::<Result<Vec<_>>>())
            .transpose()
    }

    pub fn guid_array(&self, name: &str) -> Result<Option<Vec<Uuid>>> {
        self.array(name)?
            .map(|items| items.iter().map(guid_value).collect::<Result<Vec<_>>>())
            .transpose()
    }

    /// Массив объектов, каждый разбирается `item`.
    pub fn objects<T>(&self, name: &str, item: impl Fn(Args<'a>) -> Result<T>) -> Result<Option<Vec<T>>> {
        self.array(name)?
            .map(|items| {
                items
                    .iter()
                    .map(|v| match v {
                        Value::Object(o) => item(Args(o)),
                        _ => Err(ToolError::Failed),
                    })
                    .collect::<Result<Vec<_>>>()
            })
            .transpose()
    }
}

/// Строка из элемента массива; `null` — пустая строка (так .NET связывает `string[]` с `null` внутри).
pub fn string_value(value: &Value) -> Result<String> {
    match value {
        Value::String(s) => Ok(s.clone()),
        Value::Null => Ok(String::new()),
        _ => Err(ToolError::Failed),
    }
}

pub fn guid_value(value: &Value) -> Result<Uuid> {
    match value {
        Value::String(s) => parse_guid(s).ok_or(ToolError::Failed),
        _ => Err(ToolError::Failed),
    }
}

/// `Page.Of(offset, limit)`: смещение не меньше 0, размер страницы 1–200, по умолчанию 50.
pub fn page(offset: Option<i32>, limit: Option<i32>) -> tasker_core::tasks::Page {
    let offset = offset.unwrap_or(0).max(0) as usize;
    let limit = limit.unwrap_or(PAGE_DEFAULT_LIMIT).clamp(1, PAGE_MAX_LIMIT) as usize;
    tasker_core::tasks::Page::new(offset, limit)
}

pub const PAGE_DEFAULT_LIMIT: i32 = 50;
pub const PAGE_MAX_LIMIT: i32 = 200;

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn page_clamps_like_dotnet() {
        assert_eq!(page(None, None), tasker_core::tasks::Page::new(0, 50));
        assert_eq!(page(Some(-5), Some(0)), tasker_core::tasks::Page::new(0, 1));
        assert_eq!(page(Some(3), Some(999)), tasker_core::tasks::Page::new(3, 200));
    }

    #[test]
    fn typed_access() {
        let value = json!({"a": "x", "n": 7, "b": true, "g": "00000035-0000-4000-8000-000000000035", "z": null, "l": ["1", null]});
        let args = Args(value.as_object().unwrap());
        assert_eq!(args.string("a").unwrap().as_deref(), Some("x"));
        assert_eq!(args.string("z").unwrap(), None);
        assert_eq!(args.string("missing").unwrap(), None);
        assert_eq!(args.required_string("missing"), Err(ToolError::Failed));
        assert_eq!(args.int("n").unwrap(), Some(7));
        assert_eq!(args.bool("b").unwrap(), Some(true));
        assert!(args.required_guid("g").is_ok());
        assert_eq!(args.string_array("l").unwrap(), Some(vec!["1".to_string(), String::new()]));
        assert_eq!(args.int("a"), Err(ToolError::Failed));
    }
}
