use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FieldType {
    String,
    Int,
    Float,
    Bool,
    Date,
    Enum,
}

impl FieldType {
    /// Имя типа в файлах и в консоли: `ToString().ToLowerInvariant()`.
    pub fn name(self) -> &'static str {
        match self {
            Self::String => "string",
            Self::Int => "int",
            Self::Float => "float",
            Self::Bool => "bool",
            Self::Date => "date",
            Self::Enum => "enum",
        }
    }

    /// `Enum.TryParse(ignoreCase: true)`.
    pub fn parse(text: &str) -> Option<Self> {
        match text.to_ascii_lowercase().as_str() {
            "string" => Some(Self::String),
            "int" => Some(Self::Int),
            "float" => Some(Self::Float),
            "bool" => Some(Self::Bool),
            "date" => Some(Self::Date),
            "enum" => Some(Self::Enum),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FieldDefinition {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub field_type: FieldType,
    pub multiple: bool,
    pub enum_id: Option<Uuid>,
    pub version: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FieldEnumValue {
    pub id: Uuid,
    pub name: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FieldEnum {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub values: Vec<FieldEnumValue>,
    pub version: String,
}
