use crate::time::Timestamp;
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UserKind {
    Human,
    Agent,
}

#[derive(Debug, Clone, PartialEq)]
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
