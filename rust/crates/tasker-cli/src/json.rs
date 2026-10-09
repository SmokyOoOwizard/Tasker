//! Мелочи для сборки `--json`: объект с полями в заданном порядке (анонимные объекты .NET сериализуются в порядке объявления),
//! id в форме `D` (`guid_d`), необязательные значения как `null`.
use serde_json::{Map, Value};
use tasker_core::ids::guid_d;
use uuid::Uuid;

pub fn object(pairs: Vec<(&str, Value)>) -> Value {
    Value::Object(pairs.into_iter().map(|(k, v)| (k.to_string(), v)).collect::<Map<_, _>>())
}

pub fn id(id: &Uuid) -> Value {
    Value::String(guid_d(id))
}

pub fn ids(list: &[Uuid]) -> Value {
    Value::Array(list.iter().map(id).collect())
}

pub fn opt_id(id: Option<&Uuid>) -> Value {
    id.map(self::id).unwrap_or(Value::Null)
}

pub fn opt_str(text: Option<&str>) -> Value {
    text.map(|t| Value::String(t.to_string())).unwrap_or(Value::Null)
}

pub fn opt_int(value: Option<i32>) -> Value {
    value.map(Value::from).unwrap_or(Value::Null)
}
