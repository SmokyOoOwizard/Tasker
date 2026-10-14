//! `tasks/<заголовок>-<id8>.yaml` (`TaskFile.cs`): `id`, `title`, `typeId`, `statusId`, `createdAt`, `updatedAt`, затем списки
//! только когда не пусты: `series` (`seriesId`, `number`), `links` (`typeId`, `taskId`; одна и та же связь, записанная дважды, —
//! одна связь), `fields` (`id` — поле каталога, а с `name` — собственное поле задачи: `name`, `type`, `enum`, `required` и
//! `multiple` только когда true; `values` — канонические строки, только когда есть), и последним — `description` (если есть):
//! длинный текст не отодвигает служебные поля. Читаются ключи в любом порядке; [`reorder_keys`] переставляет описание в конец
//! файла, записанного в прежнем порядке (`tasker migrate`).
use super::{Entries, Versioned, or_empty, read_document, text_or_empty, write_document};
use crate::error::Result;
use crate::yaml::Node;
use std::path::Path;
use tasker_core::model::{OwnField, TaskField, TaskItem, TaskLink, TaskSeriesNumber};
use uuid::Uuid;

pub fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Versioned<TaskItem>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    let series_numbers = m
        .maps("series")?
        .unwrap_or_default()
        .into_iter()
        .map(|s| {
            Ok(TaskSeriesNumber {
                series_id: s.guid("seriesId")?,
                number: s.int("number")?,
            })
        })
        .collect::<Result<Vec<_>>>()?;
    let mut links: Vec<TaskLink> = Vec::new();
    for l in m.maps("links")?.unwrap_or_default() {
        let link = TaskLink {
            type_id: l.guid("typeId")?,
            target_id: l.guid("taskId")?,
        };
        if !links.contains(&link) {
            links.push(link);
        }
    }
    let fields = m
        .maps("fields")?
        .unwrap_or_default()
        .into_iter()
        .map(|f| {
            let values = f
                .seq("values")?
                .unwrap_or_default()
                .iter()
                .filter(|v| !v.is_null())
                .map(|v| text_or_empty(v).to_string())
                .collect();
            let own = match f.str_opt("name")? {
                None => None,
                Some(name) => Some(OwnField {
                    name,
                    field_type: super::field::field_type(f.str_opt("type")?.as_deref(), f.file())?,
                    required: f.bool_opt("required")?.unwrap_or(false),
                    multiple: f.bool_opt("multiple")?.unwrap_or(false),
                    enum_id: f.guid_opt("enum")?,
                }),
            };
            Ok(TaskField {
                field_id: f.guid("id")?,
                values,
                own,
            })
        })
        .collect::<Result<Vec<_>>>()?;
    Ok(Versioned {
        model: TaskItem {
            id: m.guid("id")?,
            project_id,
            title: or_empty(m.str_opt("title")?),
            description: m.str_opt("description")?,
            type_id: m.guid("typeId")?,
            status_id: m.guid("statusId")?,
            series_numbers,
            links,
            fields,
            created_at: m.timestamp("createdAt")?,
            updated_at: m.timestamp("updatedAt")?,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(task: &TaskItem) -> Vec<u8> {
    let series = task
        .series_numbers
        .iter()
        .map(|s| {
            Entries::new()
                .guid("seriesId", &s.series_id)
                .push("number", Node::Int(s.number as i64))
        })
        .collect();
    let mut distinct: Vec<&TaskLink> = Vec::new();
    for link in &task.links {
        if !distinct.contains(&link) {
            distinct.push(link);
        }
    }
    let links = distinct
        .into_iter()
        .map(|l| Entries::new().guid("typeId", &l.type_id).guid("taskId", &l.target_id))
        .collect();
    let fields = task
        .fields
        .iter()
        .map(|f| {
            let own = f.own.as_ref();
            Entries::new()
                .guid("id", &f.field_id)
                .str_opt("name", own.map(|o| o.name.as_str()))
                .str_opt("type", own.map(|o| o.field_type.name()))
                .guid_opt("enum", own.and_then(|o| o.enum_id.as_ref()))
                .bool_if("required", own.is_some_and(|o| o.required))
                .bool_if("multiple", own.is_some_and(|o| o.multiple))
                .opt(
                    "values",
                    (!f.values.is_empty()).then(|| Node::Seq(f.values.iter().map(Node::str).collect())),
                )
        })
        .collect();
    write_document(
        Entries::new()
            .guid("id", &task.id)
            .str("title", &task.title)
            .guid("typeId", &task.type_id)
            .guid("statusId", &task.status_id)
            .timestamp("createdAt", &task.created_at)
            .timestamp("updatedAt", &task.updated_at)
            .maps_if_any("series", series)
            .maps_if_any("links", links)
            .maps_if_any("fields", fields)
            .str_opt("description", task.description.as_deref())
            .into_vec(),
    )
}

/// Текст файла задачи с `description` последним ключом (`TaskFile.ReorderKeys` в .NET): блок описания — строка ключа и все строки
/// значения до следующей строки без отступа — переносится в конец файла, остальной текст (порядок прочих ключей, кавычки,
/// окончания строк) не меняется. None — описания нет или после него нет других ключей.
pub fn reorder_keys(text: &str) -> Option<String> {
    let lines: Vec<&str> = text.split_inclusive('\n').collect();
    // Строка верхнего уровня: без отступа и не пустая (пустые строки и строки с отступом — продолжение значения).
    let top_level = |line: &&str| line.starts_with(|c: char| !c.is_whitespace());
    let start = lines.iter().position(|line| {
        line.strip_prefix("description:")
            .is_some_and(|rest| rest.is_empty() || rest.starts_with(|c: char| c.is_whitespace()))
    })?;
    let end = lines[start + 1..].iter().position(top_level).map_or(lines.len(), |i| start + 1 + i);
    if !lines[end..].iter().any(|line| top_level(line) && !line.starts_with('#')) {
        return None;
    }
    let mut out = String::with_capacity(text.len() + 2);
    lines[..start].iter().chain(&lines[end..]).for_each(|line| out.push_str(line));
    if !out.ends_with('\n') {
        out.push_str(if text.contains("\r\n") { "\r\n" } else { "\n" });
    }
    lines[start..end].iter().for_each(|line| out.push_str(line));
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reorder_keys_moves_the_description_block_to_the_end() {
        let old = "formatVersion: 9\nid: 1\ntitle: x\ndescription: |-\n  a\n\n  b\ntypeId: 2\nfields:\n- id: 3\n";
        assert_eq!(
            reorder_keys(old).as_deref(),
            Some("formatVersion: 9\nid: 1\ntitle: x\ntypeId: 2\nfields:\n- id: 3\ndescription: |-\n  a\n\n  b\n")
        );
        let crlf = "id: 1\r\ndescription: 'a:\r\n  b'\r\ntypeId: 2";
        assert_eq!(
            reorder_keys(crlf).as_deref(),
            Some("id: 1\r\ntypeId: 2\r\ndescription: 'a:\r\n  b'\r\n")
        );
        // Уже последним, нет описания, после описания только комментарий, `description` внутри значения — не трогаем.
        assert_eq!(reorder_keys("id: 1\ndescription: |+\n  a\n\n"), None);
        assert_eq!(reorder_keys("id: 1\ntitle: description: x\n"), None);
        assert_eq!(reorder_keys("id: 1\ndescription: a\n# note\n"), None);
        assert_eq!(reorder_keys("id: 1\ndescriptionX: a\ntitle: b\n"), None);
        assert_eq!(reorder_keys("title: |\n  description: a\nid: 1\n"), None);
    }
}
