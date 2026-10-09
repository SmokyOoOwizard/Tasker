//! `series/<id>.yaml` (`SeriesFile.cs`): `id`, `name`, `prefix`.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::Series;
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<Series>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    Ok(Versioned {
        model: Series {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            prefix: or_empty(m.str_opt("prefix")?),
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(series: &Series) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &series.id)
            .str("name", &series.name)
            .str("prefix", &series.prefix)
            .into_vec(),
    )
}
