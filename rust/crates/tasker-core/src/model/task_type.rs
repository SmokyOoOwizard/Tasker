use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TaskTypeField {
    pub field_id: Uuid,
    pub required: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct TaskType {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub description: String,
    pub status_set_id: Uuid,
    pub fields: Vec<TaskTypeField>,
    pub version: String,
}
