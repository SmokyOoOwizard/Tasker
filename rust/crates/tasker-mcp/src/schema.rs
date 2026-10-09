//! Проверка аргументов по `inputSchema` инструмента. rmcp аргументы по схеме не проверяет; .NET SDK тоже не проверяет схему, но
//! связывает аргументы с параметрами через `JsonSerializer`, и пропущенный обязательный или значение не того типа — ошибка
//! `An error occurred invoking '<tool>'.` Здесь то же самое выражено схемой: `required`, `type` (в том числе списком с `null`),
//! `format: uuid` (форма D, как `Utf8JsonReader.GetGuid`), `enum` (без учёта регистра, как `JsonStringEnumConverter`), `items`,
//! вложенные `properties` и `additionalProperties`. Лишние свойства не мешают — SDK их игнорирует. `required` проверяется только у
//! самих аргументов: вложенные объекты (`columns[]`, `fields`) — записи C#, которые `JsonSerializer` собирает и без части свойств
//! (пропущенное — null/значение по умолчанию), хотя схема .NET и перечисляет их в `required`.
use serde_json::{Map, Value};
use tasker_core::ids::parse_guid;

/// true — аргументы подходят под схему.
pub fn validate(schema: &Map<String, Value>, value: &Value) -> bool {
    if let (Some(required), Value::Object(object)) = (schema.get("required").and_then(Value::as_array), value)
        && required.iter().filter_map(Value::as_str).any(|name| !object.contains_key(name))
    {
        return false;
    }
    matches(schema, value)
}

/// Тип, формат, перечисление и вложенные значения — без `required` (см. описание модуля).
fn matches(schema: &Map<String, Value>, value: &Value) -> bool {
    if !type_matches(schema, value) {
        return false;
    }
    match value {
        Value::Null => true,
        Value::String(text) => {
            if schema.get("format").and_then(Value::as_str) == Some("uuid") && !is_guid_d(text) {
                return false;
            }
            match schema.get("enum").and_then(Value::as_array) {
                Some(allowed) => allowed.iter().any(|a| a.as_str().is_some_and(|a| a.eq_ignore_ascii_case(text))),
                None => true,
            }
        }
        Value::Array(items) => match schema.get("items").and_then(Value::as_object) {
            Some(item_schema) => items.iter().all(|item| matches(item_schema, item)),
            None => true,
        },
        Value::Object(object) => {
            let properties = schema.get("properties").and_then(Value::as_object);
            let additional = schema.get("additionalProperties").and_then(Value::as_object);
            object.iter().all(|(name, item)| {
                match properties.and_then(|p| p.get(name)).and_then(Value::as_object) {
                    Some(property) => matches(property, item),
                    None => match additional {
                        // Словарь `Dictionary<Guid, Guid>`: ключи — Guid, значения по схеме.
                        Some(additional) => parse_guid(name).is_some() && matches(additional, item),
                        None => true,
                    },
                }
            })
        }
        Value::Bool(_) | Value::Number(_) => true,
    }
}

fn type_matches(schema: &Map<String, Value>, value: &Value) -> bool {
    let Some(declared) = schema.get("type") else {
        return true;
    };
    let names: Vec<&str> = match declared {
        Value::String(s) => vec![s.as_str()],
        Value::Array(list) => list.iter().filter_map(Value::as_str).collect(),
        _ => return true,
    };
    names.iter().any(|name| match (*name, value) {
        ("null", Value::Null) => true,
        ("string", Value::String(_)) => true,
        ("boolean", Value::Bool(_)) => true,
        ("integer", Value::Number(n)) => n.as_i64().is_some_and(|i| i32::try_from(i).is_ok()),
        ("number", Value::Number(_)) => true,
        ("array", Value::Array(_)) => true,
        ("object", Value::Object(_)) => true,
        _ => false,
    })
}

/// Guid в форме D: 8-4-4-4-12 hex — единственная, которую читает `Utf8JsonReader.GetGuid`.
pub fn is_guid_d(text: &str) -> bool {
    text.len() == 36
        && text.bytes().enumerate().all(|(i, b)| match i {
            8 | 13 | 18 | 23 => b == b'-',
            _ => b.is_ascii_hexdigit(),
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn schema() -> Map<String, Value> {
        json!({
            "type": "object",
            "properties": {
                "projectId": {"type": "string", "format": "uuid"},
                "taskId": {"type": "string"},
                "limit": {"type": "integer", "default": 50},
                "flag": {"type": ["boolean", "null"], "default": null},
                "ids": {"type": ["array", "null"], "items": {"type": "string", "format": "uuid"}},
                "kind": {"type": "string", "enum": ["string", "int"]},
                "fields": {"type": ["object", "null"], "properties": {
                    "values": {"type": ["array", "null"], "items": {"type": ["object", "null"], "properties": {
                        "fieldId": {"type": "string", "format": "uuid"}}, "required": ["fieldId"]}}}},
                "drop": {"type": ["object", "null"], "additionalProperties": {"type": "string", "format": "uuid"}}
            },
            "required": ["taskId"]
        })
        .as_object()
        .unwrap()
        .clone()
    }

    const G: &str = "00000035-0000-4000-8000-000000000035";

    #[test]
    fn required_types_formats_and_enums() {
        let s = schema();
        assert!(validate(&s, &json!({"taskId": "x"})));
        assert!(validate(&s, &json!({"taskId": "x", "unknown": 1})));
        assert!(!validate(&s, &json!({})));
        assert!(!validate(&s, &json!({"taskId": null})));
        assert!(!validate(&s, &json!({"taskId": 5})));
        assert!(validate(&s, &json!({"taskId": "x", "projectId": G})));
        assert!(!validate(&s, &json!({"taskId": "x", "projectId": "not-a-guid"})));
        assert!(!validate(&s, &json!({"taskId": "x", "projectId": G.replace('-', "")})));
        assert!(validate(&s, &json!({"taskId": "x", "limit": 5})));
        assert!(!validate(&s, &json!({"taskId": "x", "limit": 5.5})));
        assert!(!validate(&s, &json!({"taskId": "x", "limit": "5"})));
        assert!(!validate(&s, &json!({"taskId": "x", "limit": 5_000_000_000i64})));
        assert!(validate(&s, &json!({"taskId": "x", "flag": null})));
        assert!(validate(&s, &json!({"taskId": "x", "ids": [G]})));
        assert!(!validate(&s, &json!({"taskId": "x", "ids": ["x"]})));
        assert!(validate(&s, &json!({"taskId": "x", "kind": "INT"})));
        assert!(!validate(&s, &json!({"taskId": "x", "kind": "float"})));
        assert!(validate(&s, &json!({"taskId": "x", "fields": {"values": [{"fieldId": G}]}})));
        // Вложенный `required` не проверяется: запись C# собирается и без `fieldId` (Guid.Empty).
        assert!(validate(&s, &json!({"taskId": "x", "fields": {"values": [{"values": []}]}})));
        assert!(validate(&s, &json!({"taskId": "x", "drop": {G: G}})));
        assert!(!validate(&s, &json!({"taskId": "x", "drop": {"a": G}})));
        assert!(!validate(&s, &json!({"taskId": "x", "drop": {G: "b"}})));
    }
}
