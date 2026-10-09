//! `link-types/<id>.yaml` (`LinkTypeFile.cs`): `id`, `name`, `outwardName`, `inwardName`, `allowCycles`, `hierarchical`.
//! В файлах до формата 7 `allowCycles` нет — он выводится по умолчанию (`DefaultLinkTypes.AllowCyclesWhenUnset`: циклы
//! запрещены только у Blocks); до формата 9 нет `hierarchical` — тип обычный. `inwardName` без значения равен `outwardName`.
//! При записи оба признака пишутся всегда.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::ids::allow_cycles_when_unset;
use tasker_core::model::LinkType;
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<LinkType>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    let id = m.guid("id")?;
    let outward = m.str_opt("outwardName")?;
    let inward = m.str_opt("inwardName")?;
    Ok(Versioned {
        model: LinkType {
            id,
            project_id,
            name: or_empty(m.str_opt("name")?),
            inward_name: inward.or_else(|| outward.clone()).unwrap_or_default(),
            outward_name: or_empty(outward),
            allow_cycles: m
                .bool_opt("allowCycles")?
                .unwrap_or_else(|| allow_cycles_when_unset(&project_id, &id)),
            hierarchical: m.bool_opt("hierarchical")?.unwrap_or(false),
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(link_type: &LinkType) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &link_type.id)
            .str("name", &link_type.name)
            .str("outwardName", &link_type.outward_name)
            .str("inwardName", &link_type.inward_name)
            .bool("allowCycles", link_type.allow_cycles)
            .bool("hierarchical", link_type.hierarchical)
            .into_vec(),
    )
}
