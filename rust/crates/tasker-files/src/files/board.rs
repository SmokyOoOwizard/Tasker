//! `boards/<id>.yaml` (`BoardFile.cs`): `id`, `name`, `statusSets`, `columns` (слева направо): `id`, `name`, `statuses`,
//! `onDrop` (набор статусов → статус при перетаскивании), `fieldFilters` (только у колонок с условиями): `field`, `op`
//! (слово: equal, notEqual, greater, greaterOrEqual, less, lessOrEqual, set, unset, attached, detached), `value` (нет у set,
//! unset, attached, detached). Неизвестный оператор — файл записан более новым Tasker.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::{Error, Result};
use crate::yaml::Node;
use std::path::Path;
use tasker_core::ids::guid_d;
use tasker_core::model::{Board, BoardColumn, ColumnFieldFilter, FieldOperator};
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<Board>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    let columns = m
        .maps("columns")?
        .unwrap_or_default()
        .into_iter()
        .map(|c| {
            let filters = c
                .maps("fieldFilters")?
                .unwrap_or_default()
                .into_iter()
                .map(|f| {
                    let op = f.str_opt("op")?;
                    Ok(ColumnFieldFilter {
                        field_id: f.guid("field")?,
                        operator: op.as_deref().and_then(FieldOperator::parse).ok_or_else(|| {
                            Error::UnsupportedFormat(format!(
                                "{}: unknown field filter operator '{}': update Tasker",
                                f.file(),
                                op.as_deref().unwrap_or("")
                            ))
                        })?,
                        value: f.str_opt("value")?,
                    })
                })
                .collect::<Result<Vec<_>>>()?;
            Ok(BoardColumn {
                id: c.guid("id")?,
                name: or_empty(c.str_opt("name")?),
                status_ids: c.guids("statuses")?.unwrap_or_default(),
                field_conditions: filters,
                drop_statuses: c.guid_pairs("onDrop")?.unwrap_or_default(),
            })
        })
        .collect::<Result<Vec<_>>>()?;
    Ok(Versioned {
        model: Board {
            id: m.guid("id")?,
            project_id,
            name: or_empty(m.str_opt("name")?),
            status_set_ids: m.guids("statusSets")?.unwrap_or_default(),
            columns,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(board: &Board) -> Vec<u8> {
    let columns = board
        .columns
        .iter()
        .map(|c| {
            let on_drop = c
                .drop_statuses
                .iter()
                .map(|(set, status)| (guid_d(set), Node::str(guid_d(status))))
                .collect();
            let filters = c
                .field_conditions
                .iter()
                .map(|f| {
                    Entries::new()
                        .guid("field", &f.field_id)
                        .str("op", &f.operator.word())
                        .str_opt("value", f.value.as_deref())
                })
                .collect();
            Entries::new()
                .guid("id", &c.id)
                .str("name", &c.name)
                .guids("statuses", &c.status_ids)
                .push("onDrop", Node::Map(on_drop))
                .maps_if_any("fieldFilters", filters)
                .into_node()
        })
        .collect();
    write_document(
        Entries::new()
            .guid("id", &board.id)
            .str("name", &board.name)
            .guids("statusSets", &board.status_set_ids)
            .push("columns", Node::Seq(columns))
            .into_vec(),
    )
}
