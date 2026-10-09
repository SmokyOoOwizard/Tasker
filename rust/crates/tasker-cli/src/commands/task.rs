//! `tasker task …` (`TaskCommands` в .NET): создание, списки (деревом и плоско), карточка, правка, удаление, связи.
use super::series::{prefixes, references};
use super::{find_status, find_task_type, task_fields};
use crate::context::{Context, TreeRow};
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use serde_json::Value;
use std::collections::HashMap;
use tasker_core::ids::guid_d;
use tasker_core::model::{LinkType, TaskItem, TaskSeriesNumber};
use tasker_core::tasks::TaskFilter;
use tasker_core::{ShortId, TaskReference};
use tasker_services::links::{LinkDirection, MAX_VIEWED, TaskLinkView};
use tasker_services::task::{CreateTask, TaskDetails, UpdateTask};
use tasker_services::task_fields::{TaskFieldSource, TaskFieldView};
use uuid::Uuid;

/// Задачи по ссылке: id (полный или короткий) или `ПРЕФИКС-номер`; у ссылки-дубликата (после слияния веток) — все с этим номером.
pub fn find_all(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<Vec<TaskItem>> {
    if TaskReference::try_parse(Some(reference), true).is_none() {
        return Err(CliError::new(format!(
            "Task is given by id (the full one, or the first {}+ hex characters shown by 'task list') or by a reference like TSK-5, not '{reference}': find it with 'task list'",
            ShortId::LENGTH
        )));
    }
    let found = ctx.session().workspace().tasks().resolve(project_id, reference, true)?;
    if found.is_empty() {
        return Err(CliError::new(format!("No task '{reference}'")));
    }
    Ok(found)
}

/// Ровно одна задача: несколько с одним номером — ошибка со списком id.
pub fn find_one(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<TaskItem> {
    let mut found = find_all(ctx, project_id, reference)?;
    if found.len() > 1 {
        return Err(CliError::new(format!(
            "Several tasks are '{reference}' (a duplicate number after a merge), use the id: {}",
            found.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
        )));
    }
    Ok(found.remove(0))
}

/// Ручка задачи — ссылка серии (`TSK-12`); у задачи без серии другой ручки нет, поэтому короткий id.
fn handle(id: &Uuid, numbers: &[TaskSeriesNumber], prefixes: &HashMap<Uuid, String>) -> String {
    let refs = references(numbers, prefixes);
    if refs.is_empty() { ShortId::of(id) } else { refs.join(",") }
}

/// Ячейки строки задачи в списках: ссылка, статус, тип, заголовок. Задача с неизвестным типом — с пустой ячейкой типа.
pub struct LineFormat {
    prefixes: HashMap<Uuid, String>,
    status_names: HashMap<Uuid, String>,
    type_names: HashMap<Uuid, String>,
}

impl LineFormat {
    pub fn load(ctx: &Context<'_>, project_id: &Uuid) -> Result<LineFormat> {
        let ws = ctx.session().workspace();
        Ok(LineFormat {
            prefixes: prefixes(ctx, project_id)?,
            status_names: ws.statuses().get_all(project_id)?.into_iter().map(|x| (x.id, x.name)).collect(),
            type_names: ws.task_types().get_all(project_id)?.into_iter().map(|x| (x.id, x.name)).collect(),
        })
    }

    pub fn cells(&self, x: &TaskItem) -> Vec<String> {
        vec![
            handle(&x.id, &x.series_numbers, &self.prefixes),
            self.status_names.get(&x.status_id).cloned().unwrap_or_else(|| guid_d(&x.status_id)),
            self.type_names.get(&x.type_id).cloned().unwrap_or_default(),
            x.title.clone(),
        ]
    }

    /// Связь одной строкой (`blocks  TSK-7  Бэклог  Название`): название стороны, ссылка, статус, заголовок.
    pub fn link_cells(&self, link: &TaskLinkView) -> Vec<String> {
        vec![
            link.name.clone(),
            handle(&link.task.id, &link.task.series_numbers, &self.prefixes),
            self.status_names
                .get(&link.task.status_id)
                .cloned()
                .unwrap_or_else(|| guid_d(&link.task.status_id)),
            link.task.title.clone(),
        ]
    }
}

/// Поле задачи одной строкой: `Priority (enum, required): High`; `-` — нет значения.
fn field_line(field: &TaskFieldView) -> String {
    let mut notes = vec![super::type_text(field.field_type, field.multiple, None)];
    if field.required {
        notes.push("required".into());
    }
    if field.source != TaskFieldSource::Type {
        notes.push(field.source.name().into());
    }
    format!(
        "{} ({}): {}",
        field.name,
        notes.join(", "),
        if field.texts.is_empty() {
            "-".to_string()
        } else {
            field.texts.join(", ")
        }
    )
}

/// Текст задачи для `get`: поля, номера в сериях и пометка дубликата; связи — те же и с тем же пределом, что в `--json`.
fn describe(ctx: &Context<'_>, project_id: &Uuid, task: &TaskItem, line: &LineFormat) -> Result<(String, TaskDetails)> {
    let ws = ctx.session().workspace();
    let types = ws.task_types().get_all(project_id)?;
    let statuses = ws.statuses().get_all(project_id)?;
    let refs = references(&task.series_numbers, &line.prefixes);
    let mut text = kit::fields(&[
        ("id", Some(guid_d(&task.id))),
        ("title", Some(task.title.clone())),
        ("type", Some(kit::named(&types, &task.type_id, |x| x.id, |x| &x.name))),
        ("status", Some(kit::named(&statuses, &task.status_id, |x| x.id, |x| &x.name))),
        ("series", if refs.is_empty() { None } else { Some(refs.join(", ")) }),
        ("created", Some(task.created_at.format_console())),
        ("updated", Some(task.updated_at.format_console())),
        ("version", Some(task.version.clone())),
    ]);

    for number in task.series_numbers.iter().filter(|x| line.prefixes.contains_key(&x.series_id)) {
        let others: Vec<String> = ws
            .index()
            .tasks_by_number(project_id, &number.series_id, number.number)?
            .into_iter()
            .filter(|x| x.id != task.id)
            .map(|x| guid_d(&x.id))
            .collect();
        if !others.is_empty() {
            text.push_str(&format!(
                "\nconflict: {}-{} is also used by {}",
                line.prefixes[&number.series_id],
                number.number,
                others.join(", ")
            ));
        }
    }

    let details = ws.tasks().describe(project_id, task)?;
    if !details.field_views.is_empty() {
        let lines: Vec<String> = details.field_views.iter().map(|x| format!("  {}", field_line(x))).collect();
        text.push_str(&format!("\nfields:\n{}", lines.join("\n")));
    }
    if !details.link_views.is_empty() {
        let rows: Vec<Vec<String>> = details.link_views.iter().map(|x| line.link_cells(x)).collect();
        text.push_str(&format!("\nlinks:\n{}", ctx.table(&rows, "  ").join("\n")));
        if details.link_count > MAX_VIEWED {
            text.push_str(&format!("\n  ... and {} more: see 'task links'", details.link_count - MAX_VIEWED));
        }
    }
    let description = task.description.as_deref().unwrap_or("");
    Ok((kit::with_description(text, description), details))
}

/// Задача для `--json`: как в API, плюс `fieldViews`, `linkViews` и `linkCount`.
fn task_json(details: &TaskDetails) -> Value {
    let mut map = match entities::task(&details.task) {
        Value::Object(map) => map,
        _ => serde_json::Map::new(),
    };
    map.insert(
        "fieldViews".into(),
        Value::Array(details.field_views.iter().map(entities::field_view).collect()),
    );
    map.insert(
        "linkViews".into(),
        Value::Array(details.link_views.iter().map(entities::link_view).collect()),
    );
    map.insert("linkCount".into(), Value::from(details.link_count));
    Value::Object(map)
}

/// Пропущенное обязательное поле: к сообщению ядра добавляется, чем его задать.
fn with_field_hint<T>(result: tasker_services::Result<T>) -> Result<T> {
    const PREFIX: &str = "Required fields have no value: ";
    match result {
        Err(e) if e.is_validation() && e.message().starts_with(PREFIX) => {
            let message = e.message();
            let names: Vec<&str> = message[PREFIX.len()..].split(", ").collect();
            let example = if names.len() == 1 {
                format!(" (e.g. --field \"{}=...\")", names[0].trim_matches('\''))
            } else {
                String::new()
            };
            Err(CliError::new(format!("{message}: set them with --field Name=value{example}")))
        }
        other => Ok(other?),
    }
}

/// Иерархический тип для `--parent`, `--add-parent`, `--remove-parent`: названный в `--parent-type` или единственный в проекте.
fn parent_type(ctx: &Context<'_>, project_id: &Uuid, name: Option<&str>) -> Result<LinkType> {
    let all: Vec<LinkType> = ctx
        .session()
        .workspace()
        .link_types()
        .get_all(project_id)?
        .into_iter()
        .filter(|x| x.hierarchical)
        .collect();
    if let Some(name) = name {
        return refs::find_item(&all, name, |x| x.id, |x| &x.name, "hierarchical link type", true).cloned();
    }
    match all.len() {
        1 => Ok(all[0].clone()),
        0 => Err(CliError::new(
            "The project has no hierarchical link type: create one with 'tasker link-type create <name> --outward includes --inward \"is part of\" --hierarchical true'",
        )),
        _ => Err(CliError::new(format!(
            "The project has several hierarchical link types ({}): choose one with --parent-type",
            all.iter().map(|x| x.name.as_str()).collect::<Vec<_>>().join(", ")
        ))),
    }
}

/// Родители из параметров: каждая ссылка находится до любых изменений.
fn find_parents(ctx: &Context<'_>, project_id: &Uuid, references: &[String]) -> Result<Vec<TaskItem>> {
    let mut found: Vec<TaskItem> = Vec::new();
    for reference in references {
        let task = find_one(ctx, project_id, reference)?;
        if !found.iter().any(|x| x.id == task.id) {
            found.push(task);
        }
    }
    Ok(found)
}

/// Связь «родитель —включает→ задача» для каждого из `add` и снятие её для `remove` (версия не нужна: связи коммутативны).
fn change_parents(
    ctx: &Context<'_>,
    project_id: &Uuid,
    link_type: &LinkType,
    task: &TaskItem,
    add: &[TaskItem],
    remove: &[TaskItem],
) -> Result<()> {
    let links = ctx.session().workspace().links();
    for parent in add {
        links.add(project_id, &parent.id, &link_type.id, &task.id, None)?;
    }
    for parent in remove {
        links.remove(project_id, &parent.id, &link_type.id, &task.id, None)?;
    }
    Ok(())
}

/// `task link A blocks B`: фраза решается в тип и сторону; «is blocked by» — это «B blocks A».
fn change_link(ctx: &mut Context<'_>, leaf: &ArgMatches, add: bool) -> Result<()> {
    let project_id = ctx.project_id()?;
    let task = find_one(ctx, &project_id, &kit::required(leaf, "task"))?;
    let other = find_one(ctx, &project_id, &kit::required(leaf, "other-task"))?;
    let ws = ctx.session().workspace().clone();
    let (link_type, direction) = ws.link_types().resolve(&project_id, &kit::required(leaf, "link"))?;
    let (source, target) = if direction == LinkDirection::Outward {
        (&task, &other)
    } else {
        (&other, &task)
    };
    if add {
        ws.links().add(&project_id, &source.id, &link_type.id, &target.id, None)?;
    } else {
        ws.links().remove(&project_id, &source.id, &link_type.id, &target.id, None)?;
    }
    let prefixes = prefixes(ctx, &project_id)?;
    let side = if direction == LinkDirection::Outward {
        &link_type.outward_name
    } else {
        &link_type.inward_name
    };
    ctx.print(
        &crate::json::object(vec![
            ("type", entities::link_type(&link_type)),
            ("direction", Value::String(direction.name().into())),
            ("source", crate::json::id(&source.id)),
            ("target", crate::json::id(&target.id)),
            ("linked", Value::Bool(add)),
        ]),
        &format!(
            "{}: {} {side} {}",
            if add { "Linked" } else { "Unlinked" },
            handle(&task.id, &task.series_numbers, &prefixes),
            handle(&other.id, &other.series_numbers, &prefixes)
        ),
    );
    Ok(())
}

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    match command {
        "create" => create(ctx, leaf),
        "list" => list(ctx, leaf),
        "get" => get(ctx, leaf),
        "update" => update(ctx, leaf),
        "delete" => {
            let project_id = ctx.project_id()?;
            let task = find_one(ctx, &project_id, &kit::required(leaf, "task"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(task.version.clone());
            ctx.session().workspace().tasks().delete(&project_id, &task.id, Some(&version))?;
            ctx.print(&entities::deleted(&task.id), &kit::deleted("task", &task.title, &task.id));
            Ok(())
        }
        "link" => change_link(ctx, leaf, true),
        "unlink" => change_link(ctx, leaf, false),
        "links" => {
            let project_id = ctx.project_id()?;
            let task = find_one(ctx, &project_id, &kit::required(leaf, "task"))?;
            let links = ctx
                .session()
                .workspace()
                .links()
                .get_links(&project_id, &task.id)?
                .unwrap_or_default();
            let line = LineFormat::load(ctx, &project_id)?;
            ctx.print_all(
                || crate::json::object(vec![("links", Value::Array(links.iter().map(entities::link_view).collect()))]),
                &links,
                |x| line.link_cells(x),
            );
            Ok(())
        }
        _ => Err(CliError::new("not implemented in this build")),
    }
}

fn create(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    let type_id = find_task_type(ctx, &project_id, &kit::required(leaf, "type"))?.id;
    let status_id = match kit::text(leaf, "status") {
        Some(reference) => Some(find_status(ctx, &project_id, &reference)?.id),
        None => None,
    };
    let series_refs = kit::values_or_empty(leaf, "series");
    let series_ids = if series_refs.is_empty() {
        None
    } else {
        let mut ids = Vec::new();
        for reference in &series_refs {
            ids.push(super::find_series(ctx, &project_id, reference)?.id);
        }
        Some(ids)
    };
    // Родители находятся до создания: неверная ссылка не оставит задачу без эпика.
    let parents = find_parents(ctx, &project_id, &kit::values_or_empty(leaf, "parent"))?;
    let hierarchy_type = if parents.is_empty() {
        None
    } else {
        Some(parent_type(ctx, &project_id, kit::text(leaf, "parent-type").as_deref())?)
    };
    let fields = task_fields::build(leaf, ctx, &project_id, None, false)?;
    let task = with_field_hint(ws.tasks().create(
        &project_id,
        &CreateTask {
            title: kit::required(leaf, "title"),
            description: kit::text(leaf, "description"),
            type_id,
            status_id,
            series_ids,
            fields,
        },
    ))?;
    if let Some(link_type) = &hierarchy_type {
        change_parents(ctx, &project_id, link_type, &task, &parents, &[])?;
    }
    let refs = references(&task.series_numbers, &prefixes(ctx, &project_id)?);
    let suffix = if refs.is_empty() {
        String::new()
    } else {
        format!(" ({})", refs.join(", "))
    };
    ctx.print(
        &entities::task(&task),
        &format!("Created task '{}' {}{suffix}", task.title, guid_d(&task.id)),
    );
    Ok(())
}

fn list(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    let type_refs = kit::values_or_empty(leaf, "type");
    let type_ids = if type_refs.is_empty() {
        None
    } else {
        let all = ws.task_types().get_all(&project_id)?;
        Some(refs::find_distinct(&all, &type_refs, |x| x.id, |x| &x.name, "task type")?)
    };
    let status_refs = kit::values_or_empty(leaf, "status");
    let status_ids = if status_refs.is_empty() {
        None
    } else {
        let all = ws.statuses().get_all(&project_id)?;
        Some(refs::find_distinct(&all, &status_refs, |x| x.id, |x| &x.name, "status")?)
    };
    let series_refs = kit::values_or_empty(leaf, "series");
    let series_ids = if series_refs.is_empty() {
        None
    } else {
        Some(super::find_series_distinct(ctx, &project_id, &series_refs)?)
    };
    let filter = TaskFilter {
        type_ids,
        status_ids,
        series_ids,
        ..TaskFilter::default()
    };
    let field_filters = kit::values_or_empty(leaf, "field");
    let field_filters = if field_filters.is_empty() { None } else { Some(field_filters) };
    let sort = kit::text(leaf, "sort");
    let length = kit::description_length(leaf);
    let line = LineFormat::load(ctx, &project_id)?;

    // --json остаётся плоским списком (parentIds, childCount в записях): клиенты не должны сломаться.
    if ctx.json || kit::flag(leaf, "flat") {
        let list = kit::Paging::load(leaf, |page| {
            Ok(ws
                .tasks()
                .list(&project_id, Some(&filter), field_filters.as_deref(), page, length, sort.as_deref())?)
        })?;
        ctx.print_list(&list, entities::task_list_item, |x| line.cells(&x.task));
        return Ok(());
    }
    let tree = kit::Paging::load_tree(leaf, |page| {
        Ok(ws
            .tasks()
            .list_tree(&project_id, Some(&filter), field_filters.as_deref(), page, length, sort.as_deref())?)
    })?;
    let rows: Vec<TreeRow> = tree
        .data
        .iter()
        .map(|x| TreeRow {
            depth: x.depth,
            repeated: x.repeated,
            cells: line.cells(&x.item.task),
        })
        .collect();
    ctx.print_tree(tree.total_count, tree.top_level_count, tree.offset, &rows);
    Ok(())
}

fn get(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    crate::perf::mark("get.project");
    let found = find_all(ctx, &project_id, &kit::required(leaf, "task"))?;
    crate::perf::mark("get.find");
    let line = LineFormat::load(ctx, &project_id)?;
    let mut texts: Vec<String> = Vec::new();
    let mut nodes: Vec<Value> = Vec::new();
    for task in &found {
        let (text, details) = describe(ctx, &project_id, task, &line)?;
        texts.push(text);
        crate::perf::mark("get.describe");
        if ctx.json {
            nodes.push(task_json(&details));
        }
    }
    if ctx.json {
        let value = if nodes.len() == 1 { nodes.remove(0) } else { Value::Array(nodes) };
        ctx.print(&value, "");
    } else {
        ctx.print(&Value::Null, &texts.join("\n\n----\n\n"));
    }
    Ok(())
}

fn update(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let task_options = ["title", "description", "type", "status"];
    let mut all_options: Vec<&str> = vec!["title", "description", "type", "status", "add-parent", "remove-parent"];
    all_options.extend(task_fields::all(true));
    kit::require_change(leaf, &all_options)?;
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    let task = find_one(ctx, &project_id, &kit::required(leaf, "task"))?;
    let parents_to_add = find_parents(ctx, &project_id, &kit::values_or_empty(leaf, "add-parent"))?;
    let parents_to_remove = find_parents(ctx, &project_id, &kit::values_or_empty(leaf, "remove-parent"))?;
    let hierarchy_type = if parents_to_add.is_empty() && parents_to_remove.is_empty() {
        None
    } else {
        Some(parent_type(ctx, &project_id, kit::text(leaf, "parent-type").as_deref())?)
    };
    let changes_task = task_options
        .iter()
        .chain(task_fields::all(true).iter())
        .any(|id| kit::given(leaf, id));
    if !changes_task {
        // Только родители: сама задача не меняется (её версия и время остаются), связи лежат у родителей.
        if let Some(link_type) = &hierarchy_type {
            change_parents(ctx, &project_id, link_type, &task, &parents_to_add, &parents_to_remove)?;
        }
        ctx.print(
            &entities::task(&task),
            &format!("Updated task '{}' {}", task.title, guid_d(&task.id)),
        );
        return Ok(());
    }

    let type_id = match kit::text(leaf, "type") {
        Some(reference) => Some(find_task_type(ctx, &project_id, &reference)?.id),
        None => None,
    };
    let status_id = match kit::text(leaf, "status") {
        Some(reference) => Some(find_status(ctx, &project_id, &reference)?.id),
        None => None,
    };
    let fields = task_fields::build(leaf, ctx, &project_id, Some(&task), true)?;
    let updated = with_field_hint(ws.tasks().update(
        &project_id,
        &task.id,
        &UpdateTask {
            title: kit::text(leaf, "title"),
            description: kit::text(leaf, "description"),
            type_id,
            status_id,
            version: Some(kit::text(leaf, "expected-version").unwrap_or(task.version.clone())),
            fields,
        },
    ))?
    .ok_or_else(|| CliError::new(format!("No task '{}'", guid_d(&task.id))))?;
    if let Some(link_type) = &hierarchy_type {
        change_parents(ctx, &project_id, link_type, &updated, &parents_to_add, &parents_to_remove)?;
    }
    ctx.print(
        &entities::task(&updated),
        &format!("Updated task '{}' {}", updated.title, guid_d(&updated.id)),
    );
    Ok(())
}
