//! `task-types/<id>.yaml` (`TaskTypeFile.cs`): `id`, `name`, `description` (только непустое), `statusSetId`, `fields` (только
//! если есть): `field` — id поля каталога, `required` — только true.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::{TaskType, TaskTypeField};
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<TaskType>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    let fields = m
        .maps("fields")?
        .unwrap_or_default()
        .into_iter()
        .map(|f| {
            Ok(TaskTypeField {
                field_id: f.guid("field")?,
                required: f.bool_opt("required")?.unwrap_or(false),
            })
        })
        .collect::<Result<Vec<_>>>()?;
    Ok(Versioned {
        model: TaskType {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            description: or_empty(m.str_opt("description")?),
            status_set_id: m.guid("statusSetId")?,
            fields,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(task_type: &TaskType) -> Vec<u8> {
    let fields = task_type
        .fields
        .iter()
        .map(|f| Entries::new().guid("field", &f.field_id).bool_if("required", f.required))
        .collect();
    write_document(
        Entries::new()
            .guid("id", &task_type.id)
            .str("name", &task_type.name)
            .str_opt("description", Some(task_type.description.as_str()).filter(|d| !d.is_empty()))
            .guid("statusSetId", &task_type.status_set_id)
            .maps_if_any("fields", fields)
            .into_vec(),
    )
}
