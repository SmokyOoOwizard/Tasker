//! `enums/<id>.yaml` (`FieldEnumFile.cs`): `id`, `name`, `values` — список `id`/`name` в порядке отображения (пишется всегда).
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::{FieldEnum, FieldEnumValue};
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<FieldEnum>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    let values = m
        .maps("values")?
        .unwrap_or_default()
        .into_iter()
        .map(|v| {
            Ok(FieldEnumValue {
                id: v.guid("id")?,
                name: or_empty(v.str_opt("name")?),
            })
        })
        .collect::<Result<Vec<_>>>()?;
    Ok(Versioned {
        model: FieldEnum {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            values,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(value: &FieldEnum) -> Vec<u8> {
    let values = value
        .values
        .iter()
        .map(|v| Entries::new().guid("id", &v.id).str("name", &v.name).into_node())
        .collect();
    write_document(
        Entries::new()
            .guid("id", &value.id)
            .str("name", &value.name)
            .push("values", crate::yaml::Node::Seq(values))
            .into_vec(),
    )
}
