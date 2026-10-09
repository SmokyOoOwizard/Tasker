//! `GlobalSettings`: содержимое `settings.json`. JSON как у .NET (`JsonSerializerDefaults.Web` + `TaskerJson`): camelCase, имена
//! свойств читаются без учёта регистра, числа принимаются и строкой, `kind` — строкой `files`/`sqlite` (без учёта регистра) или
//! числом, неизвестные поля игнорируются.
use crate::settings::workspace_location::{WorkspaceKind, WorkspaceLocation};
use crate::validate::eq_ignore_case;
use serde_json::{Map, Value, json};

pub const MAX_USER_NAME_LENGTH: usize = 100;
pub const DEFAULT_PORT: i32 = 5719;

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct GlobalSettings {
    /// Как зовут человека за десктопом и консолью, где нет входа: это имя видят другие в «правит Иван». `None` — имя пользователя ОС.
    pub user_name: Option<String>,
    pub mcp: McpSettings,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpSettings {
    /// Порт MCP на 127.0.0.1 — постоянный, чтобы адрес можно было один раз прописать агенту.
    pub port: i32,
    /// Рабочие области, доступные через MCP демона.
    pub workspaces: Vec<WorkspaceEntry>,
}

impl Default for McpSettings {
    fn default() -> Self {
        McpSettings {
            port: DEFAULT_PORT,
            workspaces: Vec::new(),
        }
    }
}

/// Рабочая область в настройках: папка (данные в `.tasker`) или файл SQLite.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkspaceEntry {
    pub kind: WorkspaceKind,
    pub path: String,
}

impl WorkspaceEntry {
    pub fn of(location: &WorkspaceLocation) -> WorkspaceEntry {
        WorkspaceEntry {
            kind: location.kind(),
            path: location.path().to_string(),
        }
    }

    pub fn location(&self) -> WorkspaceLocation {
        match self.kind {
            WorkspaceKind::Files => WorkspaceLocation::files(&self.path),
            WorkspaceKind::Sqlite => WorkspaceLocation::sqlite(&self.path),
        }
    }
}

impl GlobalSettings {
    /// JSON в порядке свойств .NET.
    pub fn to_json(&self) -> Value {
        json!({
            "userName": self.user_name,
            "mcp": {
                "port": self.mcp.port,
                "workspaces": self.mcp.workspaces.iter().map(|w| json!({"kind": w.kind.json_name(), "path": w.path})).collect::<Vec<_>>(),
            }
        })
    }

    /// Разбор JSON; ошибка — текст в духе `JsonException`.
    pub fn from_json(value: &Value) -> Result<GlobalSettings, String> {
        let root = object(value, "Tasker.Global.GlobalSettings")?;
        let mut settings = GlobalSettings::default();
        if let Some(name) = get(root, "userName") {
            settings.user_name = match name {
                Value::Null => None,
                Value::String(s) => Some(s.clone()),
                other => return Err(convert_error(other, "System.String")),
            };
        }
        if let Some(mcp) = get(root, "mcp")
            && !mcp.is_null()
        {
            let mcp = object(mcp, "Tasker.Global.McpSettings")?;
            if let Some(port) = get(mcp, "port") {
                settings.mcp.port = int(port)?;
            }
            if let Some(workspaces) = get(mcp, "workspaces")
                && !workspaces.is_null()
            {
                let Value::Array(items) = workspaces else {
                    return Err(convert_error(
                        workspaces,
                        "System.Collections.Generic.IReadOnlyList`1[Tasker.Global.WorkspaceEntry]",
                    ));
                };
                settings.mcp.workspaces = items.iter().map(workspace_entry).collect::<Result<_, _>>()?;
            }
        }
        Ok(settings)
    }
}

fn workspace_entry(value: &Value) -> Result<WorkspaceEntry, String> {
    let entry = object(value, "Tasker.Global.WorkspaceEntry")?;
    let kind = match get(entry, "kind") {
        None | Some(Value::Null) => WorkspaceKind::Files,
        Some(Value::String(s)) => {
            WorkspaceKind::parse(s).ok_or_else(|| convert_error(value, "Tasker.Storage.Files.Workspaces.WorkspaceKind"))?
        }
        Some(Value::Number(n)) => match n.as_i64() {
            Some(0) => WorkspaceKind::Files,
            Some(1) => WorkspaceKind::Sqlite,
            _ => return Err(convert_error(value, "Tasker.Storage.Files.Workspaces.WorkspaceKind")),
        },
        Some(other) => return Err(convert_error(other, "Tasker.Storage.Files.Workspaces.WorkspaceKind")),
    };
    let path = match get(entry, "path") {
        None | Some(Value::Null) => String::new(),
        Some(Value::String(s)) => s.clone(),
        Some(other) => return Err(convert_error(other, "System.String")),
    };
    Ok(WorkspaceEntry { kind, path })
}

fn object<'a>(value: &'a Value, type_name: &str) -> Result<&'a Map<String, Value>, String> {
    value.as_object().ok_or_else(|| convert_error(value, type_name))
}

/// Свойство без учёта регистра (`PropertyNameCaseInsensitive`); при нескольких совпадениях — последнее, как у System.Text.Json.
fn get<'a>(map: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    map.iter().filter(|(k, _)| eq_ignore_case(k, name)).map(|(_, v)| v).next_back()
}

/// `int` с `NumberHandling.AllowReadingFromString`.
fn int(value: &Value) -> Result<i32, String> {
    match value {
        Value::Number(n) => n
            .as_i64()
            .and_then(|n| i32::try_from(n).ok())
            .ok_or_else(|| convert_error(value, "System.Int32")),
        Value::String(s) => s.parse::<i32>().map_err(|_| convert_error(value, "System.Int32")),
        _ => Err(convert_error(value, "System.Int32")),
    }
}

fn convert_error(value: &Value, type_name: &str) -> String {
    let kind = match value {
        Value::Null => "null",
        Value::Bool(_) => "boolean",
        Value::Number(_) => "number",
        Value::String(_) => "string",
        Value::Array(_) => "array",
        Value::Object(_) => "object",
    };
    format!("The JSON value ({kind}) could not be converted to {type_name}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_like_system_text_json_web_defaults() {
        let v: Value = serde_json::from_str(
            r#"{"MCP":{"Port":"6001","future":true,"workspaces":[{"kind":"SQLITE","path":"/a.db"},{"kind":1,"path":"/b"}]},"other":1}"#,
        )
        .unwrap();
        let s = GlobalSettings::from_json(&v).unwrap();
        assert_eq!(s.mcp.port, 6001);
        assert_eq!(s.mcp.workspaces.len(), 2);
        assert_eq!(s.mcp.workspaces[0].kind, WorkspaceKind::Sqlite);
        assert_eq!(s.mcp.workspaces[1].kind, WorkspaceKind::Sqlite);
        assert_eq!(GlobalSettings::from_json(&json!({})).unwrap(), GlobalSettings::default());
        assert!(GlobalSettings::from_json(&json!({"mcp": {"port": 1.5}})).is_err());
        assert!(GlobalSettings::from_json(&json!({"mcp": {"port": "x"}})).is_err());
        assert!(GlobalSettings::from_json(&json!({"userName": 1})).is_err());
        assert!(GlobalSettings::from_json(&json!([])).is_err());
    }

    #[test]
    fn writes_in_dotnet_order() {
        let s = GlobalSettings {
            user_name: None,
            mcp: McpSettings {
                port: 5719,
                workspaces: vec![WorkspaceEntry {
                    kind: WorkspaceKind::Files,
                    path: "/x".into(),
                }],
            },
        };
        assert_eq!(
            crate::json::to_string_pretty(&s.to_json()),
            "{\n  \"userName\": null,\n  \"mcp\": {\n    \"port\": 5719,\n    \"workspaces\": [\n      {\n        \"kind\": \"files\",\n        \"path\": \"/x\"\n      }\n    ]\n  }\n}"
        );
    }
}
