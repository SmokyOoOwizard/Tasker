//! Задачи в БД (`TaskStorage` и `TaskSorting` в `Tasker.Storage.Db`): строка `tasks` и дочерние таблицы — номера в сериях
//! (`task_series_numbers`), исходящие связи (`task_links`), поля задачи (`task_fields`) и их значения (`task_field_values`).
//! Отбор и порядок списка — одним запросом, как их строит EF Core: условия — подзапросы `EXISTS`, ключи порядка — выражения в
//! `ORDER BY` (у ключа, которого может не быть, сначала признак «нет значения»), в конце — создание и id.
use super::entities::{field_type_code, field_type_of_code};
use super::values::{INITIAL_VERSION, db_time, g, guid_col, json_ids, optional_guid_col, time_col};
use rusqlite::types::{ToSql, Value};
use rusqlite::{Connection, OptionalExtension, params};
use std::collections::HashMap;
use tasker_core::model::{FieldOperator, FieldType, OwnField, TaskField, TaskItem, TaskLink, TaskSeriesNumber};
use tasker_core::tasks::{FieldCondition, Page, SortField, SortTarget, TaskFilter, TaskSortKey, field_name_key, field_number};
use tasker_files::index::{LinkEdge, NumberConflict, OwnFieldKind};
use uuid::Uuid;

const COLUMNS: &str = "t.\"Id\", t.\"ProjectId\", t.\"Title\", t.\"Description\", t.\"TypeId\", t.\"StatusId\", t.\"CreatedAt\", t.\"UpdatedAt\", t.\"Version\"";

/// Порядок по умолчанию: создание, затем id.
const DEFAULT_ORDER: &str = "t.\"CreatedAt\", t.\"Id\"";

/// Номер серии в одном числе: место серии в старшей части, номер — в младшей.
const SERIES_NUMBER_RANGE: &str = "4294967296.0";

/// Строка `tasks` без дочерних строк.
struct Row {
    task: TaskItem,
}

fn task_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<Row> {
    Ok(Row {
        task: TaskItem {
            id: guid_col(row, 0)?,
            project_id: guid_col(row, 1)?,
            title: row.get(2)?,
            description: row.get(3)?,
            type_id: guid_col(row, 4)?,
            status_id: guid_col(row, 5)?,
            series_numbers: vec![],
            links: vec![],
            fields: vec![],
            created_at: time_col(row, 6)?,
            updated_at: time_col(row, 7)?,
            version: row.get::<_, i64>(8)?.to_string(),
        },
    })
}

/// SQL с именованными параметрами (`@name`).
struct Select {
    wheres: Vec<String>,
    params: Vec<(String, Value)>,
}

impl Select {
    fn new(project_id: &Uuid) -> Select {
        Select {
            wheres: vec!["t.\"ProjectId\" = @project".into()],
            params: vec![("@project".into(), Value::Text(g(project_id)))],
        }
    }

    fn param(&mut self, name: &str, value: Value) {
        self.params.push((name.into(), value));
    }

    fn where_sql(&self) -> String {
        self.wheres.join(" AND ")
    }

    // Только параметры, которые есть в SQL: SQLite отвергает лишние.
    fn bindings<'a>(&'a self, sql: &str) -> Vec<(&'a str, &'a dyn ToSql)> {
        self.params
            .iter()
            .filter(|(name, _)| uses(sql, name))
            .map(|(name, value)| (name.as_str(), value as &dyn ToSql))
            .collect()
    }
}

fn uses(sql: &str, name: &str) -> bool {
    sql.match_indices(name)
        .any(|(at, _)| !sql[at + name.len()..].starts_with(|c: char| c.is_ascii_alphanumeric() || c == '_'))
}

fn ids_value(ids: &[Uuid]) -> Value {
    Value::Text(json_ids(ids))
}

/// Условия фильтра (`TaskStorage.Filter`).
fn select(project_id: &Uuid, filter: Option<&TaskFilter>) -> Select {
    let mut s = Select::new(project_id);
    let Some(f) = filter else {
        return s;
    };
    let add_in = |s: &mut Select, column: &str, name: &str, ids: &Option<Vec<Uuid>>| {
        if let Some(ids) = ids {
            s.wheres.push(format!("t.\"{column}\" IN (SELECT value FROM json_each({name}))"));
            s.param(name, ids_value(ids));
        }
    };
    add_in(&mut s, "Id", "@ids", &f.ids);
    add_in(&mut s, "TypeId", "@types", &f.type_ids);
    add_in(&mut s, "StatusId", "@statuses", &f.status_ids);
    let exists = |s: &mut Select, table: &str, condition: &str, name: &str, ids: &Option<Vec<Uuid>>| {
        if let Some(ids) = ids {
            s.wheres.push(format!(
                "EXISTS (SELECT 1 FROM \"{table}\" AS x WHERE x.\"TaskId\" = t.\"Id\" AND {condition} IN (SELECT value FROM json_each({name})))"
            ));
            s.param(name, ids_value(ids));
        }
    };
    exists(&mut s, "task_series_numbers", "x.\"SeriesId\"", "@series", &f.series_ids);
    exists(&mut s, "task_links", "x.\"TypeId\"", "@linkTypes", &f.link_type_ids);
    exists(&mut s, "task_fields", "x.\"FieldId\"", "@fields", &f.field_ids);
    exists(
        &mut s,
        "task_fields",
        "x.\"OwnEnumId\" IS NOT NULL AND x.\"OwnEnumId\"",
        "@enums",
        &f.enum_ids,
    );
    for (i, condition) in f.field_values.as_deref().unwrap_or(&[]).iter().enumerate() {
        field_condition(&mut s, condition, i);
    }
    s
}

/// Условие по полю (`TaskStorage.FieldCondition`): «есть значение, которое…» — подзапрос к значениям, «нет значения…» — его
/// отрицание (поэтому `!=` и `:unset` берут и задачи без значения). Строка относится к условию, если это поле каталога или
/// собственное поле с тем же именем и типом. Подключённость: тип задачи из типов, где поле каталога есть, или запись о поле в задаче.
fn field_condition(s: &mut Select, c: &FieldCondition, i: usize) {
    let (value, number) = (format!("@value{i}"), format!("@number{i}"));
    let own = format!("(v.\"OwnKey\" = @own{i} AND v.\"OwnType\" = @ownType{i})");
    let row = match c.field_id {
        Some(_) => format!("(v.\"FieldId\" = @field{i} OR {own})"),
        None => own,
    };
    let entry_own = format!("(f.\"OwnKey\" = @own{i} AND f.\"OwnType\" = @ownType{i})");
    let entry = match c.field_id {
        Some(_) => format!("(f.\"FieldId\" = @field{i} OR {entry_own})"),
        None => entry_own,
    };
    let any = |extra: &str| format!("EXISTS (SELECT 1 FROM \"task_field_values\" AS v WHERE {row} AND v.\"TaskId\" = t.\"Id\"{extra})");
    let entries = format!("EXISTS (SELECT 1 FROM \"task_fields\" AS f WHERE {entry} AND f.\"TaskId\" = t.\"Id\")");
    let types = format!("t.\"TypeId\" IN (SELECT value FROM json_each(@fieldTypes{i}))");
    let ordered = |sign: &str| {
        if c.number.is_some() {
            any(&format!(" AND v.\"Number\" {sign} {number}"))
        } else {
            any(&format!(" AND v.\"Value\" {sign} {value}"))
        }
    };
    let sql = match c.operator {
        FieldOperator::Equal => any(&format!(" AND v.\"Value\" = {value}")),
        FieldOperator::NotEqual => format!("NOT {}", any(&format!(" AND v.\"Value\" = {value}"))),
        FieldOperator::Set => any(""),
        FieldOperator::Unset => format!("NOT {}", any("")),
        FieldOperator::Attached => format!("({types} OR {entries})"),
        FieldOperator::Detached => format!("(NOT {types} AND NOT {entries})"),
        FieldOperator::Greater => ordered(">"),
        FieldOperator::GreaterOrEqual => ordered(">="),
        FieldOperator::Less => ordered("<"),
        FieldOperator::LessOrEqual => ordered("<="),
    };
    s.wheres.push(sql);
    if let Some(catalog) = &c.field_id {
        s.param(&format!("@field{i}"), Value::Text(g(catalog)));
    }
    s.param(&format!("@own{i}"), Value::Text(c.key()));
    s.param(&format!("@ownType{i}"), Value::Integer(field_type_code(c.field_type)));
    s.param(&format!("@value{i}"), c.value.clone().map(Value::Text).unwrap_or(Value::Null));
    if let Some(n) = c.number {
        s.param(&format!("@number{i}"), Value::Real(n));
    }
    s.param(&format!("@fieldTypes{i}"), ids_value(c.type_ids.as_deref().unwrap_or(&[])));
}

// ---------------------------------------------------------------------------------------------------------------- порядок

/// `TaskSorting.Apply`: ключи по порядку, у ключа, которого может не быть, сначала «нет значения» (false раньше true) — такие
/// задачи в конце при любом направлении; в конце — создание и id.
fn order_by(keys: Option<&[TaskSortKey]>) -> String {
    let mut parts: Vec<String> = Vec::new();
    for key in keys.unwrap_or(&[]) {
        let direction = if key.descending { " DESC" } else { "" };
        let (expression, nullable) = match key.target {
            SortTarget::Title => ("lower(t.\"Title\")".to_string(), false),
            SortTarget::Created => ("t.\"CreatedAt\"".to_string(), false),
            SortTarget::Updated => ("t.\"UpdatedAt\"".to_string(), false),
            SortTarget::Type => (
                case(key.type_ranks.as_deref().unwrap_or(&[]), |x| {
                    (format!("t.\"TypeId\" = {}", literal(&g(&x.id))), x.rank)
                }),
                true,
            ),
            SortTarget::Status => (
                case(key.status_ranks.as_deref().unwrap_or(&[]), |x| {
                    (
                        format!(
                            "t.\"TypeId\" = {} AND t.\"StatusId\" = {}",
                            literal(&g(&x.type_id)),
                            literal(&g(&x.status_id))
                        ),
                        x.rank,
                    )
                }),
                true,
            ),
            SortTarget::Series => {
                let rank = case(key.series_ranks.as_deref().unwrap_or(&[]), |x| {
                    (format!("n.\"SeriesId\" = {}", literal(&g(&x.id))), x.rank)
                });
                (
                    format!(
                        "(SELECT MIN(CAST(({rank}) AS REAL) * {SERIES_NUMBER_RANGE} + CAST(n.\"Number\" AS REAL)) \
                         FROM \"task_series_numbers\" AS n WHERE n.\"TaskId\" = t.\"Id\")"
                    ),
                    true,
                )
            }
            SortTarget::Field => (
                field_expression(key.field.as_ref().expect("a field sort key carries its field")),
                true,
            ),
        };
        if nullable {
            parts.push(format!("({expression}) IS NULL"));
        }
        parts.push(format!("({expression}){direction}"));
    }
    parts.push(DEFAULT_ORDER.to_string());
    format!(" ORDER BY {}", parts.join(", "))
}

/// Первое значение поля задачи (по `Position`, затем по полю): число, текст (у строки — без учёта регистра) или место в перечислении.
fn field_expression(field: &SortField) -> String {
    let column = match field.field_type {
        FieldType::Int | FieldType::Float => "v.\"Number\"".to_string(),
        FieldType::String => "lower(v.\"Value\")".to_string(),
        FieldType::Enum => {
            let ranks: Vec<(String, i64)> = field
                .enum_values
                .as_deref()
                .unwrap_or(&[])
                .iter()
                .enumerate()
                .map(|(i, v)| (v.clone(), i as i64))
                .collect();
            case(&ranks, |x| (format!("v.\"Value\" = {}", literal(&x.0)), x.1))
        }
        _ => "v.\"Value\"".to_string(),
    };
    let own = format!(
        "(v.\"OwnKey\" = {} AND v.\"OwnType\" = {})",
        literal(&field.key()),
        field_type_code(field.field_type)
    );
    let row = match &field.field_id {
        Some(catalog) => format!("(v.\"FieldId\" = {} OR {own})", literal(&g(catalog))),
        None => own,
    };
    format!(
        "SELECT {column} FROM \"task_field_values\" AS v WHERE {row} AND v.\"TaskId\" = t.\"Id\" \
         ORDER BY v.\"Position\", v.\"FieldId\" LIMIT 1"
    )
}

/// `CASE WHEN условие THEN место … ELSE NULL END` по таблице мест; пустая — NULL.
fn case<T>(ranks: &[T], when: impl Fn(&T) -> (String, i64)) -> String {
    if ranks.is_empty() {
        return "NULL".into();
    }
    let mut text = String::from("CASE");
    for item in ranks {
        let (condition, rank) = when(item);
        text.push_str(&format!(" WHEN {condition} THEN {rank}"));
    }
    text.push_str(" END");
    text
}

fn literal(text: &str) -> String {
    format!("'{}'", text.replace('\'', "''"))
}

// ---------------------------------------------------------------------------------------------------------------- чтение

fn query_rows(c: &Connection, sql: &str, s: &Select) -> rusqlite::Result<Vec<Row>> {
    let mut statement = c.prepare(sql)?;
    statement.query_map(s.bindings(sql).as_slice(), task_row)?.collect()
}

/// Общее число и задачи страницы в порядке фильтра (`GetRange`, `GetAll`).
pub fn list(
    c: &Connection,
    project_id: &Uuid,
    filter: Option<&TaskFilter>,
    page: Option<Page>,
) -> rusqlite::Result<(usize, Vec<TaskItem>)> {
    let s = select(project_id, filter);
    let order = order_by(filter.and_then(|f| f.sort.as_deref()));
    let (rows, total) = match page {
        Some(page) => {
            let count_sql = format!("SELECT COUNT(*) FROM \"tasks\" AS t WHERE {}", s.where_sql());
            let total: i64 = c.query_row(&count_sql, s.bindings(&count_sql).as_slice(), |row| row.get(0))?;
            let sql = format!(
                "SELECT {COLUMNS} FROM \"tasks\" AS t WHERE {}{order} LIMIT {} OFFSET {}",
                s.where_sql(),
                page.limit as i64,
                page.offset as i64
            );
            (query_rows(c, &sql, &s)?, total.max(0) as usize)
        }
        None => {
            let sql = format!("SELECT {COLUMNS} FROM \"tasks\" AS t WHERE {}{order}", s.where_sql());
            let rows = query_rows(c, &sql, &s)?;
            let total = rows.len();
            (rows, total)
        }
    };
    Ok((total, map(c, rows)?))
}

pub fn ids(c: &Connection, project_id: &Uuid, filter: Option<&TaskFilter>) -> rusqlite::Result<Vec<Uuid>> {
    let s = select(project_id, filter);
    let sql = format!(
        "SELECT t.\"Id\" FROM \"tasks\" AS t WHERE {}{}",
        s.where_sql(),
        order_by(filter.and_then(|f| f.sort.as_deref()))
    );
    let mut statement = c.prepare(&sql)?;
    statement.query_map(s.bindings(&sql).as_slice(), |row| guid_col(row, 0))?.collect()
}

pub fn count(c: &Connection, project_id: &Uuid, filter: &TaskFilter) -> rusqlite::Result<usize> {
    let s = select(project_id, Some(filter));
    let sql = format!("SELECT COUNT(*) FROM \"tasks\" AS t WHERE {}", s.where_sql());
    let total: i64 = c.query_row(&sql, s.bindings(&sql).as_slice(), |row| row.get(0))?;
    Ok(total.max(0) as usize)
}

pub fn get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<TaskItem>> {
    let row = c
        .query_row(
            &format!("SELECT {COLUMNS} FROM \"tasks\" AS t WHERE t.\"ProjectId\" = ?1 AND t.\"Id\" = ?2 LIMIT 1"),
            params![g(project_id), g(id)],
            task_row,
        )
        .optional()?;
    Ok(match row {
        Some(row) => map(c, vec![row])?.pop(),
        None => None,
    })
}

/// Задачи проекта по условию на `t` с параметрами `@project` и своими, по созданию, затем по id.
fn tasks_where(c: &Connection, condition: &str, extra: &[(&str, Value)], project_id: &Uuid) -> rusqlite::Result<Vec<TaskItem>> {
    let mut s = Select::new(project_id);
    s.wheres.push(condition.to_string());
    for (name, value) in extra {
        s.param(name, value.clone());
    }
    let sql = format!(
        "SELECT {COLUMNS} FROM \"tasks\" AS t WHERE {} ORDER BY {DEFAULT_ORDER}",
        s.where_sql()
    );
    let rows = query_rows(c, &sql, &s)?;
    map(c, rows)
}

/// `FindByIdPrefix`: ключ — шестнадцатеричные цифры и дефисы (знаков LIKE в нём нет); LIKE в SQLite не различает регистр ASCII.
pub fn by_id_prefix(c: &Connection, project_id: &Uuid, key: &str) -> rusqlite::Result<Vec<TaskItem>> {
    tasks_where(c, "t.\"Id\" LIKE @key", &[("@key", Value::Text(format!("{key}%")))], project_id)
}

pub fn by_number(c: &Connection, project_id: &Uuid, series_id: &Uuid, number: i32) -> rusqlite::Result<Vec<TaskItem>> {
    tasks_where(
        c,
        "EXISTS (SELECT 1 FROM \"task_series_numbers\" AS n WHERE n.\"TaskId\" = t.\"Id\" AND n.\"SeriesId\" = @series AND n.\"Number\" = @number)",
        &[("@series", Value::Text(g(series_id))), ("@number", Value::Integer(number.into()))],
        project_id,
    )
}

pub fn with_series_not_in(c: &Connection, project_id: &Uuid, known: &[Uuid]) -> rusqlite::Result<Vec<TaskItem>> {
    tasks_where(
        c,
        "EXISTS (SELECT 1 FROM \"task_series_numbers\" AS n WHERE n.\"TaskId\" = t.\"Id\" \
         AND n.\"SeriesId\" NOT IN (SELECT value FROM json_each(@known)))",
        &[("@known", ids_value(known))],
        project_id,
    )
}

pub fn linked_to(c: &Connection, project_id: &Uuid, target_id: &Uuid) -> rusqlite::Result<Vec<TaskItem>> {
    tasks_where(
        c,
        "EXISTS (SELECT 1 FROM \"task_links\" AS l WHERE l.\"TaskId\" = t.\"Id\" AND l.\"TargetId\" = @target)",
        &[("@target", Value::Text(g(target_id)))],
        project_id,
    )
}

/// Задачи со связью на тип, которого нет, или на задачу, которой нет в проекте.
pub fn with_invalid_links(c: &Connection, project_id: &Uuid, known_types: &[Uuid]) -> rusqlite::Result<Vec<TaskItem>> {
    tasks_where(
        c,
        "EXISTS (SELECT 1 FROM \"task_links\" AS l WHERE l.\"TaskId\" = t.\"Id\" AND (\
         l.\"TypeId\" NOT IN (SELECT value FROM json_each(@known)) OR NOT EXISTS (\
         SELECT 1 FROM \"tasks\" AS o WHERE o.\"Id\" = l.\"TargetId\" AND o.\"ProjectId\" = @project)))",
        &[("@known", ids_value(known_types))],
        project_id,
    )
}

/// Номера, которые есть у нескольких задач (в норме их не даёт уникальный индекс): по серии и номеру; задачи — по созданию и id.
pub fn number_conflicts(c: &Connection, project_id: &Uuid) -> rusqlite::Result<Vec<NumberConflict>> {
    let groups: Vec<(Uuid, i32)> = {
        let mut statement = c.prepare(
            "SELECT \"SeriesId\", \"Number\" FROM \"task_series_numbers\" WHERE \"ProjectId\" = ?1 \
             GROUP BY \"SeriesId\", \"Number\" HAVING COUNT(*) > 1 ORDER BY \"SeriesId\", \"Number\"",
        )?;
        statement
            .query_map(params![g(project_id)], |row| Ok((guid_col(row, 0)?, row.get(1)?)))?
            .collect::<rusqlite::Result<_>>()?
    };
    let mut result = Vec::with_capacity(groups.len());
    for (series_id, number) in groups {
        let mut statement = c.prepare(
            "SELECT t.\"Id\" FROM \"tasks\" AS t WHERE t.\"ProjectId\" = ?1 AND EXISTS (SELECT 1 FROM \"task_series_numbers\" AS n \
             WHERE n.\"TaskId\" = t.\"Id\" AND n.\"SeriesId\" = ?2 AND n.\"Number\" = ?3) ORDER BY t.\"CreatedAt\", t.\"Id\"",
        )?;
        let task_ids = statement
            .query_map(params![g(project_id), g(&series_id), number], |row| guid_col(row, 0))?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        result.push(NumberConflict {
            series_id,
            number,
            task_ids,
        });
    }
    Ok(result)
}

pub fn link_targets(c: &Connection, project_id: &Uuid, type_id: &Uuid, sources: &[Uuid]) -> rusqlite::Result<HashMap<Uuid, Vec<Uuid>>> {
    let mut statement = c.prepare(
        "SELECT l.\"TaskId\", l.\"TargetId\" FROM \"task_links\" AS l WHERE l.\"TypeId\" = ?1 \
         AND l.\"TaskId\" IN (SELECT value FROM json_each(?2)) \
         AND EXISTS (SELECT 1 FROM \"tasks\" AS t WHERE t.\"Id\" = l.\"TaskId\" AND t.\"ProjectId\" = ?3)",
    )?;
    let rows = statement.query_map(params![g(type_id), json_ids(sources), g(project_id)], |row| {
        Ok((guid_col(row, 0)?, guid_col(row, 1)?))
    })?;
    let mut result: HashMap<Uuid, Vec<Uuid>> = HashMap::new();
    for row in rows {
        let (source, target) = row?;
        result.entry(source).or_default().push(target);
    }
    Ok(result)
}

pub fn link_edges(c: &Connection, project_id: &Uuid, type_id: &Uuid) -> rusqlite::Result<Vec<LinkEdge>> {
    let mut statement = c.prepare(
        "SELECT l.\"TaskId\", l.\"TargetId\" FROM \"task_links\" AS l WHERE l.\"TypeId\" = ?1 \
         AND EXISTS (SELECT 1 FROM \"tasks\" AS t WHERE t.\"Id\" = l.\"TaskId\" AND t.\"ProjectId\" = ?2)",
    )?;
    statement
        .query_map(params![g(type_id), g(project_id)], |row| {
            Ok(LinkEdge {
                source_id: guid_col(row, 0)?,
                target_id: guid_col(row, 1)?,
            })
        })?
        .collect()
}

pub fn count_linked_to(c: &Connection, project_id: &Uuid, targets: &[Uuid]) -> rusqlite::Result<HashMap<Uuid, usize>> {
    let mut statement = c.prepare(
        "SELECT l.\"TargetId\", COUNT(*) FROM \"task_links\" AS l WHERE l.\"TargetId\" IN (SELECT value FROM json_each(?1)) \
         AND EXISTS (SELECT 1 FROM \"tasks\" AS t WHERE t.\"Id\" = l.\"TaskId\" AND t.\"ProjectId\" = ?2) GROUP BY l.\"TargetId\"",
    )?;
    let rows = statement.query_map(params![json_ids(targets), g(project_id)], |row| {
        Ok((guid_col(row, 0)?, row.get::<_, i64>(1)?))
    })?;
    let mut result = HashMap::new();
    for row in rows {
        let (id, count) = row?;
        result.insert(id, count.max(0) as usize);
    }
    Ok(result)
}

/// Различные пары «тип, перечисление» у собственных полей задач проекта с этим именем.
pub fn own_field_kinds(c: &Connection, project_id: &Uuid, name_key: &str) -> rusqlite::Result<Vec<OwnFieldKind>> {
    let mut statement = c.prepare(
        "SELECT DISTINCT f.\"OwnType\", f.\"OwnEnumId\" FROM \"task_fields\" AS f WHERE f.\"OwnKey\" = ?1 \
         AND EXISTS (SELECT 1 FROM \"tasks\" AS t WHERE t.\"Id\" = f.\"TaskId\" AND t.\"ProjectId\" = ?2)",
    )?;
    let rows = statement.query_map(params![name_key, g(project_id)], |row| {
        Ok((row.get::<_, Option<i64>>(0)?, optional_guid_col(row, 1)?))
    })?;
    let mut result = Vec::new();
    for row in rows {
        let (code, enum_id) = row?;
        if let Some(field_type) = code.and_then(field_type_of_code) {
            result.push(OwnFieldKind { field_type, enum_id });
        }
    }
    Ok(result)
}

/// Задачи с номерами (по порядку серий), связями (по типу и цели), полями (по порядку записи) и значениями — четырьмя запросами на
/// всю пачку (`TaskStorage.Map`).
fn map(c: &Connection, rows: Vec<Row>) -> rusqlite::Result<Vec<TaskItem>> {
    if rows.is_empty() {
        return Ok(vec![]);
    }
    let ids: Vec<Uuid> = rows.iter().map(|r| r.task.id).collect();
    let json = json_ids(&ids);

    let mut numbers: HashMap<Uuid, Vec<TaskSeriesNumber>> = HashMap::new();
    {
        let mut statement = c.prepare(
            "SELECT \"TaskId\", \"SeriesId\", \"Number\" FROM \"task_series_numbers\" \
             WHERE \"TaskId\" IN (SELECT value FROM json_each(?1)) ORDER BY \"SeriesId\"",
        )?;
        let found = statement.query_map(params![json], |row| {
            Ok((guid_col(row, 0)?, guid_col(row, 1)?, row.get::<_, i32>(2)?))
        })?;
        for row in found {
            let (task, series_id, number) = row?;
            numbers.entry(task).or_default().push(TaskSeriesNumber { series_id, number });
        }
    }

    let mut links: HashMap<Uuid, Vec<TaskLink>> = HashMap::new();
    {
        let mut statement = c.prepare(
            "SELECT \"TaskId\", \"TypeId\", \"TargetId\" FROM \"task_links\" \
             WHERE \"TaskId\" IN (SELECT value FROM json_each(?1)) ORDER BY \"TypeId\", \"TargetId\"",
        )?;
        let found = statement.query_map(params![json], |row| Ok((guid_col(row, 0)?, guid_col(row, 1)?, guid_col(row, 2)?)))?;
        for row in found {
            let (task, type_id, target_id) = row?;
            links.entry(task).or_default().push(TaskLink { type_id, target_id });
        }
    }

    let mut values: HashMap<(Uuid, Uuid), Vec<String>> = HashMap::new();
    {
        let mut statement = c.prepare(
            "SELECT \"TaskId\", \"FieldId\", \"Value\" FROM \"task_field_values\" \
             WHERE \"TaskId\" IN (SELECT value FROM json_each(?1)) ORDER BY \"Position\"",
        )?;
        let found = statement.query_map(params![json], |row| {
            Ok((guid_col(row, 0)?, guid_col(row, 1)?, row.get::<_, String>(2)?))
        })?;
        for row in found {
            let (task, field, value) = row?;
            values.entry((task, field)).or_default().push(value);
        }
    }

    let mut fields: HashMap<Uuid, Vec<TaskField>> = HashMap::new();
    {
        let mut statement = c.prepare(
            "SELECT \"TaskId\", \"FieldId\", \"OwnName\", \"OwnType\", \"OwnRequired\", \"OwnMultiple\", \"OwnEnumId\" \
             FROM \"task_fields\" WHERE \"TaskId\" IN (SELECT value FROM json_each(?1)) ORDER BY \"Position\"",
        )?;
        let found = statement.query_map(params![json], |row| {
            let own_name: Option<String> = row.get(2)?;
            let own = match own_name {
                None => None,
                Some(name) => Some(OwnField {
                    name,
                    field_type: row
                        .get::<_, Option<i64>>(3)?
                        .and_then(field_type_of_code)
                        .unwrap_or(FieldType::String),
                    required: row.get(4)?,
                    multiple: row.get(5)?,
                    enum_id: optional_guid_col(row, 6)?,
                }),
            };
            Ok((guid_col(row, 0)?, guid_col(row, 1)?, own))
        })?;
        for row in found {
            let (task, field_id, own) = row?;
            let field_values = values.remove(&(task, field_id)).unwrap_or_default();
            fields.entry(task).or_default().push(TaskField {
                field_id,
                values: field_values,
                own,
            });
        }
    }

    Ok(rows
        .into_iter()
        .map(|row| {
            let id = row.task.id;
            TaskItem {
                series_numbers: numbers.remove(&id).unwrap_or_default(),
                links: links.remove(&id).unwrap_or_default(),
                fields: fields.remove(&id).unwrap_or_default(),
                ..row.task
            }
        })
        .collect())
}

// ---------------------------------------------------------------------------------------------------------------- запись

/// Дочерние строки задачи: номера (по серии — первый), связи (без повторов), поля (по полю — первое; `Position` — порядок) и значения
/// (`Number` — значение числом, `OwnKey`/`OwnType` — у собственного поля).
fn insert_children(c: &Connection, task: &TaskItem) -> rusqlite::Result<()> {
    let task_id = g(&task.id);
    let mut seen_series: Vec<Uuid> = Vec::new();
    for number in &task.series_numbers {
        if seen_series.contains(&number.series_id) {
            continue;
        }
        seen_series.push(number.series_id);
        c.execute(
            "INSERT INTO \"task_series_numbers\" (\"TaskId\", \"SeriesId\", \"Number\", \"ProjectId\") VALUES (?1, ?2, ?3, ?4)",
            params![task_id, g(&number.series_id), number.number, g(&task.project_id)],
        )?;
    }
    let mut seen_links: Vec<&TaskLink> = Vec::new();
    for link in &task.links {
        if seen_links.contains(&link) {
            continue;
        }
        seen_links.push(link);
        c.execute(
            "INSERT INTO \"task_links\" (\"TaskId\", \"TypeId\", \"TargetId\") VALUES (?1, ?2, ?3)",
            params![task_id, g(&link.type_id), g(&link.target_id)],
        )?;
    }
    let mut seen_fields: Vec<Uuid> = Vec::new();
    for field in &task.fields {
        if seen_fields.contains(&field.field_id) {
            continue;
        }
        seen_fields.push(field.field_id);
        let own_key = field.own.as_ref().map(|own| field_name_key(&own.name));
        let own_type = field.own.as_ref().map(|own| field_type_code(own.field_type));
        c.execute(
            "INSERT INTO \"task_fields\" (\"TaskId\", \"FieldId\", \"OwnEnumId\", \"OwnKey\", \"OwnMultiple\", \"OwnName\", \
             \"OwnRequired\", \"OwnType\", \"Position\") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                task_id,
                g(&field.field_id),
                field.own.as_ref().and_then(|own| own.enum_id.as_ref().map(g)),
                own_key,
                field.own.as_ref().is_some_and(|own| own.multiple),
                field.own.as_ref().map(|own| own.name.as_str()),
                field.own.as_ref().is_some_and(|own| own.required),
                own_type,
                (seen_fields.len() - 1) as i64
            ],
        )?;
    }
    let mut seen_values: Vec<Uuid> = Vec::new();
    for field in &task.fields {
        if seen_values.contains(&field.field_id) {
            continue;
        }
        seen_values.push(field.field_id);
        let own_key = field.own.as_ref().map(|own| field_name_key(&own.name));
        let own_type = field.own.as_ref().map(|own| field_type_code(own.field_type));
        for (i, value) in field.values.iter().enumerate() {
            c.execute(
                "INSERT INTO \"task_field_values\" (\"TaskId\", \"FieldId\", \"Position\", \"Number\", \"OwnKey\", \"OwnType\", \"Value\") \
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                params![task_id, g(&field.field_id), i as i64, field_number(value), own_key, own_type, value],
            )?;
        }
    }
    Ok(())
}

pub fn insert(c: &Connection, task: &TaskItem) -> rusqlite::Result<()> {
    c.execute(
        "INSERT INTO \"tasks\" (\"Id\", \"CreatedAt\", \"Description\", \"ProjectId\", \"StatusId\", \"Title\", \"TypeId\", \
         \"UpdatedAt\", \"Version\") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
        params![
            g(&task.id),
            db_time(&task.created_at),
            task.description,
            g(&task.project_id),
            g(&task.status_id),
            task.title,
            g(&task.type_id),
            db_time(&task.updated_at),
            INITIAL_VERSION
        ],
    )?;
    insert_children(c, task)
}

/// Строка задачи и её дочерние строки меняются вместе: условное обновление по версии, затем замена номеров, связей и полей.
pub fn update(c: &Connection, task: &TaskItem, expected: i32) -> rusqlite::Result<bool> {
    let updated = c.execute(
        "UPDATE \"tasks\" SET \"Title\" = ?1, \"Description\" = ?2, \"TypeId\" = ?3, \"StatusId\" = ?4, \"UpdatedAt\" = ?5, \
         \"Version\" = ?6 WHERE \"ProjectId\" = ?7 AND \"Id\" = ?8 AND \"Version\" = ?9",
        params![
            task.title,
            task.description,
            g(&task.type_id),
            g(&task.status_id),
            db_time(&task.updated_at),
            i64::from(expected) + 1,
            g(&task.project_id),
            g(&task.id),
            expected
        ],
    )?;
    if updated == 0 {
        return Ok(false);
    }
    let id = g(&task.id);
    c.execute("DELETE FROM \"task_series_numbers\" WHERE \"TaskId\" = ?1", params![id])?;
    c.execute("DELETE FROM \"task_links\" WHERE \"TaskId\" = ?1", params![id])?;
    c.execute("DELETE FROM \"task_field_values\" WHERE \"TaskId\" = ?1", params![id])?;
    c.execute("DELETE FROM \"task_fields\" WHERE \"TaskId\" = ?1", params![id])?;
    insert_children(c, task)?;
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use tasker_core::tasks::IdRank;

    #[test]
    fn order_by_puts_missing_keys_last_and_ends_with_the_default() {
        assert_eq!(order_by(None), " ORDER BY t.\"CreatedAt\", t.\"Id\"");
        let mut key = TaskSortKey::new(SortTarget::Type, true, "type");
        key.type_ranks = Some(vec![IdRank {
            id: Uuid::parse_str("aaaaaaaa-0000-4000-8000-000000000001").unwrap(),
            rank: 0,
        }]);
        assert_eq!(
            order_by(Some(&[TaskSortKey::new(SortTarget::Title, false, "title"), key])),
            " ORDER BY (lower(t.\"Title\")), (CASE WHEN t.\"TypeId\" = 'AAAAAAAA-0000-4000-8000-000000000001' THEN 0 END) IS NULL, \
             (CASE WHEN t.\"TypeId\" = 'AAAAAAAA-0000-4000-8000-000000000001' THEN 0 END) DESC, t.\"CreatedAt\", t.\"Id\""
        );
    }
}
