//! Таблица инструментов: имя, описание, `inputSchema` и аннотации каждого из 62 инструментов — `tools.json` рядом, в том виде, в каком
//! их отдаёт .NET-демон в `tools/list` (схемы собраны руками, с `workspace` у всех, кроме `list_workspaces`, и необязательным
//! `projectId`; порядок ключей сохраняется — `serde_json` с `preserve_order`). Эталон — `tests/golden/expected/mcp/tools-list.json`.
use rmcp::model::{Tool, ToolAnnotations};
use serde_json::{Map, Value};
use std::sync::Arc;

/// Аргумент области у всех инструментов, кроме [`LIST_WORKSPACES`] (`WorkspaceArgument.Argument`).
pub const WORKSPACE_ARGUMENT: &str = "workspace";

/// Инструмент, которому область не нужна: он сам показывает области (`WorkspaceArgument.ListTool`).
pub const LIST_WORKSPACES: &str = "list_workspaces";

/// Аргумент проекта (`DefaultProject.Argument`).
pub const PROJECT_ARGUMENT: &str = "projectId";

const TOOLS_JSON: &str = include_str!("tools.json");

#[derive(Debug, Clone)]
pub struct ToolDef {
    pub name: String,
    pub description: String,
    pub schema: Map<String, Value>,
    pub annotations: Option<Map<String, Value>>,
}

impl ToolDef {
    fn properties(&self) -> Option<&Map<String, Value>> {
        self.schema.get("properties").and_then(Value::as_object)
    }

    /// У инструмента есть аргумент `workspace` (`WorkspaceArgument.Takes`).
    pub fn takes_workspace(&self) -> bool {
        self.name != LIST_WORKSPACES && self.properties().is_some_and(|p| p.contains_key(WORKSPACE_ARGUMENT))
    }

    /// У инструмента есть параметр `projectId` (`DefaultProject.TakesProject`).
    pub fn takes_project(&self) -> bool {
        self.properties().is_some_and(|p| p.contains_key(PROJECT_ARGUMENT))
    }

    fn to_rmcp(&self) -> Tool {
        let mut tool = Tool::new(self.name.clone(), self.description.clone(), Arc::new(self.schema.clone()));
        if let Some(annotations) = &self.annotations {
            let mut a = ToolAnnotations::new();
            a.read_only_hint = annotations.get("readOnlyHint").and_then(Value::as_bool);
            a.destructive_hint = annotations.get("destructiveHint").and_then(Value::as_bool);
            a.idempotent_hint = annotations.get("idempotentHint").and_then(Value::as_bool);
            a.open_world_hint = annotations.get("openWorldHint").and_then(Value::as_bool);
            tool = tool.annotate(a);
        }
        tool
    }
}

pub struct Catalog {
    tools: Vec<ToolDef>,
    rmcp: Vec<Tool>,
}

impl Catalog {
    pub fn load() -> Catalog {
        let list: Vec<Value> = serde_json::from_str(TOOLS_JSON).expect("tools.json is a JSON array");
        let tools: Vec<ToolDef> = list
            .into_iter()
            .map(|item| {
                let object = item.as_object().expect("tool is an object");
                ToolDef {
                    name: object["name"].as_str().expect("tool name").to_string(),
                    description: object["description"].as_str().expect("tool description").to_string(),
                    schema: object["inputSchema"].as_object().expect("inputSchema").clone(),
                    annotations: object.get("annotations").and_then(Value::as_object).cloned(),
                }
            })
            .collect();
        let rmcp = tools.iter().map(ToolDef::to_rmcp).collect();
        Catalog { tools, rmcp }
    }

    pub fn get(&self, name: &str) -> Option<&ToolDef> {
        self.tools.iter().find(|t| t.name == name)
    }

    pub fn all(&self) -> &[ToolDef] {
        &self.tools
    }

    /// Инструменты для `tools/list` в порядке таблицы.
    pub fn tools(&self) -> Vec<Tool> {
        self.rmcp.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_table_has_62_tools_with_workspace_and_optional_project() {
        let catalog = Catalog::load();
        assert_eq!(catalog.all().len(), 62);
        for tool in catalog.all() {
            assert_eq!(tool.takes_workspace(), tool.name != LIST_WORKSPACES, "{}", tool.name);
            if let Some(required) = tool.schema.get("required").and_then(Value::as_array) {
                assert!(
                    !required.iter().any(|r| r == PROJECT_ARGUMENT || r == WORKSPACE_ARGUMENT),
                    "{}",
                    tool.name
                );
            }
        }
        assert!(catalog.get("get_task").unwrap().takes_project());
        assert!(!catalog.get("list_projects").unwrap().takes_project());
        assert!(catalog.get("no_such_tool").is_none());
    }

    #[test]
    fn rmcp_tools_serialize_like_the_table() {
        let catalog = Catalog::load();
        let listed = serde_json::to_value(catalog.tools()).unwrap();
        let expected: Value = serde_json::from_str(TOOLS_JSON).unwrap();
        assert_eq!(tasker_core::json::to_string(&listed), tasker_core::json::to_string(&expected));
    }
}
