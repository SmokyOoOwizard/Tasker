use crate::model::{FieldType, TaskLink, TaskSeriesNumber};
use crate::time::Timestamp;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// Собственное поле задачи (не из каталога проекта).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnField {
    pub name: String,
    pub field_type: FieldType,
    pub required: bool,
    pub multiple: bool,
    pub enum_id: Option<Uuid>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskField {
    pub field_id: Uuid,
    /// Канонические значения (`FieldValues`); у enum — id значений в форме D.
    pub values: Vec<String>,
    pub own: Option<OwnField>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskItem {
    pub id: Uuid,
    pub project_id: Uuid,
    pub title: String,
    pub description: Option<String>,
    pub type_id: Uuid,
    pub status_id: Uuid,
    pub series_numbers: Vec<TaskSeriesNumber>,
    pub links: Vec<TaskLink>,
    pub fields: Vec<TaskField>,
    pub created_at: Timestamp,
    pub updated_at: Timestamp,
    pub version: String,
}
