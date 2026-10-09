use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkType {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub outward_name: String,
    pub inward_name: String,
    pub allow_cycles: bool,
    pub hierarchical: bool,
    pub version: String,
}

impl LinkType {
    /// Связь без направления: названия совпадают без учёта регистра (`OrdinalIgnoreCase`).
    pub fn is_symmetric(&self) -> bool {
        crate::validate::eq_ignore_case(&self.outward_name, &self.inward_name)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskLink {
    pub type_id: Uuid,
    pub target_id: Uuid,
}
