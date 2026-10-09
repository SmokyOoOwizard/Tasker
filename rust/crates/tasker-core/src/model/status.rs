use uuid::Uuid;

#[derive(Debug, Clone, PartialEq)]
pub struct Status {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    /// `#RRGGBB`, заглавные hex.
    pub color: String,
    /// Пустая строка — описания нет (в файле ключ опускается).
    pub description: String,
    pub version: String,
}

#[derive(Debug, Clone, PartialEq)]
pub struct StatusSet {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub status_ids: Vec<Uuid>,
    pub version: String,
}
