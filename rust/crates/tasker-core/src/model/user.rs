use crate::time::Timestamp;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum UserKind {
    Human,
    Agent,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct User {
    pub id: Uuid,
    pub username: String,
    pub kind: UserKind,
    pub owner_id: Option<Uuid>,
    pub email: Option<String>,
    pub is_admin: bool,
    pub created_at: Timestamp,
    pub version: String,
}

impl User {
    pub fn is_agent(&self) -> bool {
        self.kind == UserKind::Agent
    }
}
