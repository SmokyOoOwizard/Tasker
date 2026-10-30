mod board;
mod field;
mod link_type;
mod project;
mod series;
mod status;
mod task;
mod task_type;
mod user;
mod view;

use super::app::{Action, Kind};
use super::step::Item;
use super::wizard::{Steps, Wizard};
use crate::errors::Result;
use tasker_core::ids::guid_d;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, LinkType, Series, Status, StatusSet, TaskItem, TaskType};
use tasker_services::Storage;
use tasker_services::storage::Entity;
use uuid::Uuid;

pub fn run(w: &mut Wizard<'_, '_>, action: Action, kind: Kind) -> Steps<Vec<String>> {
    if action == Action::View {
        return view::run(w, kind);
    }
    match kind {
        Kind::Project => project::run(w, action),
        Kind::Task => task::run(w, action),
        Kind::Status => status::run_status(w, action),
        Kind::StatusSet => status::run_status_set(w, action),
        Kind::TaskType => task_type::run(w, action),
        Kind::Series => series::run(w, action),
        Kind::Field => field::run_field(w, action),
        Kind::Enum => field::run_enum(w, action),
        Kind::Board => board::run(w, action),
        Kind::LinkType => link_type::run(w, action),
        Kind::User => user::run(w, action, false),
        Kind::Agent => user::run(w, action, true),
    }
}

fn all<T: Entity>(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<T>> {
    Ok(w.ctx.session.workspace().get_all::<T>(project)?)
}

pub fn statuses(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(all::<Status>(w, project)?
        .iter()
        .map(|x| Item::new(guid_d(&x.id), &x.name).detail(&x.color).version(&x.version))
        .collect())
}

pub fn status_sets(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(all::<StatusSet>(w, project)?
        .iter()
        .map(|x| {
            Item::new(guid_d(&x.id), &x.name)
                .detail(format!("{} statuses", x.status_ids.len()))
                .version(&x.version)
        })
        .collect())
}

pub fn task_types(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(all::<TaskType>(w, project)?
        .iter()
        .map(|x| Item::new(guid_d(&x.id), &x.name).version(&x.version))
        .collect())
}

pub fn fields(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    let enums = crate::commands::enum_names_of(w.ctx.session, project)?;
    Ok(all::<FieldDefinition>(w, project)?
        .iter()
        .map(|x| {
            Item::new(guid_d(&x.id), &x.name)
                .detail(crate::commands::field_type_text(x, &enums))
                .version(&x.version)
        })
        .collect())
}

pub fn enums(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(all::<FieldEnum>(w, project)?
        .iter()
        .map(|x| {
            Item::new(guid_d(&x.id), &x.name)
                .detail(x.values.iter().map(|v| v.name.as_str()).collect::<Vec<_>>().join(", "))
                .version(&x.version)
        })
        .collect())
}

pub fn boards(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(all::<Board>(w, project)?
        .iter()
        .map(|x| {
            Item::new(guid_d(&x.id), &x.name)
                .detail(format!("{} columns", x.columns.len()))
                .version(&x.version)
        })
        .collect())
}

pub fn link_types(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<LinkType>> {
    Ok(w.ctx.session.workspace().link_types().get_all(project)?)
}

pub fn link_type_items(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(link_types(w, project)?
        .iter()
        .map(|x| {
            Item::new(guid_d(&x.id), &x.name)
                .detail(format!("{} / {}", x.outward_name, x.inward_name))
                .version(&x.version)
        })
        .collect())
}

pub fn series(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    Ok(all::<Series>(w, project)?
        .iter()
        .map(|x| Item::new(guid_d(&x.id), &x.prefix).detail(&x.name).version(&x.version))
        .collect())
}

pub struct TaskNames {
    prefixes: std::collections::HashMap<Uuid, String>,
    statuses: std::collections::HashMap<Uuid, String>,
    types: std::collections::HashMap<Uuid, String>,
    definitions: Vec<FieldDefinition>,
    enums: std::collections::HashMap<Uuid, FieldEnum>,
}

impl TaskNames {
    pub fn load(w: &Wizard<'_, '_>, project: &Uuid) -> Result<TaskNames> {
        Ok(TaskNames {
            prefixes: w.ctx.session.workspace().series().prefixes(project)?,
            statuses: all::<Status>(w, project)?.into_iter().map(|s| (s.id, s.name)).collect(),
            types: all::<TaskType>(w, project)?.into_iter().map(|t| (t.id, t.name)).collect(),
            definitions: all::<FieldDefinition>(w, project)?,
            enums: all::<FieldEnum>(w, project)?.into_iter().map(|e| (e.id, e)).collect(),
        })
    }

    fn field_text(&self, task: &TaskItem, name: &str) -> String {
        let Some(definition) = self.definitions.iter().find(|d| d.name == name) else {
            return "-".to_string();
        };
        let Some(field) = task.fields.iter().find(|f| f.field_id == definition.id) else {
            return "-".to_string();
        };
        let enumeration = definition.enum_id.and_then(|e| self.enums.get(&e));
        let texts = tasker_core::fields::texts(definition.field_type, enumeration, &field.values);
        if texts.is_empty() { "-".to_string() } else { texts.join(", ") }
    }

    pub fn items(&self, tasks: &[TaskRow<'_>], columns: &[String]) -> Vec<Item> {
        let rows: Vec<Vec<String>> = tasks
            .iter()
            .map(|row| {
                let task = row.task;
                let refs = crate::commands::series::references(&task.series_numbers, &self.prefixes);
                let handle = if refs.is_empty() {
                    tasker_core::ShortId::of(&task.id)
                } else {
                    refs.join(",")
                };
                let mut row = vec![
                    format!("{}{handle}{}", "    ".repeat(row.depth), if row.repeated { " (+)" } else { "" }),
                    self.statuses.get(&task.status_id).cloned().unwrap_or_default(),
                    self.types.get(&task.type_id).cloned().unwrap_or_default(),
                ];
                row.extend(columns.iter().map(|name| self.field_text(task, name)));
                row
            })
            .collect();
        let widths = (0..3 + columns.len())
            .map(|column| rows.iter().map(|r| crate::table::width(&r[column])).max().unwrap_or(0))
            .collect::<Vec<_>>();
        tasks
            .iter()
            .map(|row| row.task)
            .zip(rows)
            .map(|(task, row)| {
                let mut label = String::new();
                for (cell, width) in row.iter().zip(&widths) {
                    label.push_str(cell);
                    label.push_str(&" ".repeat(width - crate::table::width(cell) + 2));
                }
                label.push_str(&task.title);
                Item::new(guid_d(&task.id), label).version(&task.version)
            })
            .collect()
    }
}

pub struct TaskRow<'a> {
    pub task: &'a TaskItem,
    pub depth: usize,
    pub repeated: bool,
}

impl<'a> TaskRow<'a> {
    pub fn flat(task: &'a TaskItem) -> TaskRow<'a> {
        TaskRow {
            task,
            depth: 0,
            repeated: false,
        }
    }
}

pub fn tasks(w: &Wizard<'_, '_>, project: &Uuid) -> Result<Vec<Item>> {
    let names = TaskNames::load(w, project)?;
    let mut items: Vec<TaskItem> = w.ctx.session.workspace().tasks().get_all(project, None)?;
    items.sort_by_key(|t| std::cmp::Reverse(t.created_at));
    Ok(names.items(&items.iter().map(TaskRow::flat).collect::<Vec<_>>(), &[]))
}

pub fn confirm_delete(w: &mut Wizard<'_, '_>, what: &str, name: &str) -> Steps<()> {
    w.confirm(
        &format!("Delete {what}"),
        &format!("Delete {what} '{name}'? This cannot be undone."),
    )
}

pub fn lines(text: &str) -> Vec<String> {
    text.lines().map(str::trim).filter(|l| !l.is_empty()).map(str::to_string).collect()
}
