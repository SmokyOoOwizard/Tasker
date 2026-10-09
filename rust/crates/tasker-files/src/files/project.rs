//! `projects/<id>/project.yaml` (`ProjectFile.cs`): `id`, `name`, `createdAt`.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::Project;

pub fn parse(bytes: &[u8], path: &Path) -> Result<Versioned<Project>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    Ok(Versioned {
        model: Project {
            id: m.guid("id")?,
            name: or_empty(m.str_opt("name")?),
            created_at: m.timestamp("createdAt")?,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(project: &Project) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &project.id)
            .str("name", &project.name)
            .timestamp("createdAt", &project.created_at)
            .into_vec(),
    )
}
