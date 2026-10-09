use crate::time::Timestamp;
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq)]
pub struct Project {
    pub id: Uuid,
    pub name: String,
    pub created_at: Timestamp,
    pub version: String,
}
