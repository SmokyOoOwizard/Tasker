//! `statuses/<id>.yaml` (`StatusFile.cs`): `id`, `name`, `color`, `description` (только непустое).
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::Status;
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<Status>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    Ok(Versioned {
        model: Status {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            color: or_empty(m.str_opt("color")?),
            description: or_empty(m.str_opt("description")?),
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(status: &Status) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &status.id)
            .str("name", &status.name)
            .str("color", &status.color)
            .str_opt("description", Some(status.description.as_str()).filter(|d| !d.is_empty()))
            .into_vec(),
    )
}
