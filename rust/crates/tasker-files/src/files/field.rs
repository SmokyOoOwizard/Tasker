//! `fields/<id>.yaml` (`FieldFile.cs`): `id`, `name`, `type` (string, int, float, bool, date, enum), `enum` (id перечисления,
//! только у enum), `multiple` (только true). Неизвестный тип — файл записан более новым Tasker.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::{Error, Result};
use std::path::Path;
use tasker_core::model::{FieldDefinition, FieldType};
use uuid::Uuid;

/// `Enum.TryParse<FieldType>(ignoreCase: true)`, иначе `{file}: unknown field type '{x}': update Tasker`.
pub(crate) fn field_type(text: Option<&str>, file: &str) -> Result<FieldType> {
    text.and_then(FieldType::parse)
        .ok_or_else(|| Error::UnsupportedFormat(format!("{file}: unknown field type '{}': update Tasker", text.unwrap_or(""))))
}

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<FieldDefinition>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    Ok(Versioned {
        model: FieldDefinition {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            field_type: field_type(m.str_opt("type")?.as_deref(), m.file())?,
            multiple: m.bool_opt("multiple")?.unwrap_or(false),
            enum_id: m.guid_opt("enum")?,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(field: &FieldDefinition) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &field.id)
            .str("name", &field.name)
            .str("type", field.field_type.name())
            .guid_opt("enum", field.enum_id.as_ref())
            .bool_if("multiple", field.multiple)
            .into_vec(),
    )
}
