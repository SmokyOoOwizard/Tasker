//! Каталог областей для MCP (.NET `McpWorkspaceCatalog` + `McpDaemon.ConfigureCatalog`): открытые области реестра — по ключу,
//! остальные из статуса демона (ещё открываются или не открылись) — по имени папки; агент других папок открыть не может.
use crate::registry::WorkspaceRegistry;
use crate::state::{DaemonState, WorkspaceState};
use std::sync::Arc;
use tasker_core::validate::to_upper_invariant;
use tasker_mcp::{McpWorkspace, McpWorkspaceScope, McpWorkspaceStatus, McpWorkspaces};

pub struct DaemonWorkspaces {
    registry: Arc<WorkspaceRegistry>,
    state: Arc<DaemonState>,
}

impl DaemonWorkspaces {
    pub fn new(registry: Arc<WorkspaceRegistry>, state: Arc<DaemonState>) -> DaemonWorkspaces {
        DaemonWorkspaces { registry, state }
    }
}

/// Имя неоткрытой области: имя папки (`Path.GetFileName` без завершающих разделителей), пусто — сам путь.
fn folder_name(path: &str) -> String {
    let trimmed = path.trim_end_matches(['/', '\\']);
    match trimmed.rsplit(['/', '\\']).next() {
        Some(name) if !name.is_empty() => name.to_string(),
        _ => path.to_string(),
    }
}

impl McpWorkspaces for DaemonWorkspaces {
    fn list(&self) -> Vec<McpWorkspace> {
        let mut all: Vec<McpWorkspace> = self
            .registry
            .opened()
            .into_iter()
            .map(|w| McpWorkspace {
                name: w.key().to_string(),
                path: w.location().path().to_string(),
                status: McpWorkspaceStatus::Open,
                projects: None,
                error: None,
            })
            .collect();
        all.extend(
            self.state
                .workspaces()
                .into_iter()
                .filter(|w| w.state != WorkspaceState::Open)
                .map(|w| McpWorkspace {
                    name: folder_name(&w.path),
                    path: w.path.clone(),
                    status: if w.state == WorkspaceState::Opening {
                        McpWorkspaceStatus::Opening
                    } else {
                        McpWorkspaceStatus::Failed
                    },
                    projects: None,
                    error: w.error.clone(),
                }),
        );
        // `OrderBy(Name, OrdinalIgnoreCase).ThenBy(Path, Ordinal)`.
        all.sort_by(|a, b| {
            to_upper_invariant(&a.name)
                .cmp(&to_upper_invariant(&b.name))
                .then_with(|| a.path.cmp(&b.path))
        });
        all
    }

    fn enter(&self, name_or_path: &str) -> Option<McpWorkspaceScope> {
        let opened = self.registry.find_by_name_or_path(name_or_path)?;
        Some(McpWorkspaceScope {
            name: opened.key().to_string(),
            workspace: opened.workspace().clone(),
            agent: opened.local_agent().clone(),
            lease: opened,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folder_names() {
        assert_eq!(folder_name("/w/broken"), "broken");
        assert_eq!(folder_name("/w/broken/"), "broken");
        assert_eq!(folder_name("C:\\w\\Tasker\\"), "Tasker");
        assert_eq!(folder_name("/"), "/");
    }
}
