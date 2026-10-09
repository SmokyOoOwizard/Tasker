//! `status-sets/<id>.yaml` (`StatusSetFile.cs`): `id`, `name`, `statuses` — список id в порядке отображения (пишется всегда).
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::StatusSet;
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<StatusSet>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    Ok(Versioned {
        model: StatusSet {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            status_ids: m.guids("statuses")?.unwrap_or_default(),
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(set: &StatusSet) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &set.id)
            .str("name", &set.name)
            .guids("statuses", &set.status_ids)
            .into_vec(),
    )
}
