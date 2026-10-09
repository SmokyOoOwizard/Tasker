//! Запросы к индексу: условия выборки ([`IndexQuery`]), списки с пагинацией, подсчёты, поиск по id и префиксу, таблицы серий,
//! связей и полей задач — те же SQL, что у `WorkspaceIndex` в .NET.
use super::sort::{DEFAULT_ORDER, order_by};
use super::sync::{field_type_code, field_type_of_code};
use super::{WorkspaceIndex, db_error, kind_name};
use crate::layout::EntityKind;
use rusqlite::types::{ToSql, Value};
use rusqlite::{Connection, params};
use serde::de::DeserializeOwned;
use std::collections::HashMap;
use std::io;
use std::path::PathBuf;
use tasker_core::ids::guid_d;
use tasker_core::model::{
    Board, FieldDefinition, FieldEnum, FieldOperator, FieldType, LinkType, Project, Series, Status, StatusSet, TaskItem, TaskType, User,
    UserKind,
};
use tasker_core::tasks::{FieldCondition, ListPage, Page, TaskFilter, TaskSortKey};
use uuid::Uuid;

/// Сущность, которую индекс хранит в `data`: вид файла и формат JSON (serde-модель `tasker-core`).
pub trait IndexEntity: DeserializeOwned {
    const KIND: EntityKind;
}

macro_rules! index_entity {
    ($($t:ty => $k:expr),* $(,)?) => {$(
        impl IndexEntity for $t {
            const KIND: EntityKind = $k;
        }
    )*};
}

index_entity! {
    Project => EntityKind::Project,
    User => EntityKind::User,
    Status => EntityKind::Status,
    StatusSet => EntityKind::StatusSet,
    TaskType => EntityKind::TaskType,
    Board => EntityKind::Board,
    Series => EntityKind::Series,
    TaskItem => EntityKind::Task,
    LinkType => EntityKind::LinkType,
    FieldDefinition => EntityKind::Field,
    FieldEnum => EntityKind::FieldEnum,
}

/// Условия выборки (`IndexQuery` в .NET); None — без ограничения, пустой список — ничего не подходит.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct IndexQuery {
    pub project_id: Option<Uuid>,
    pub ids: Option<Vec<Uuid>>,
    /// Id начинается с этого ключа — началом Guid в форме D строчными (`ShortId::try_key`).
    pub id_prefix: Option<String>,
    pub type_ids: Option<Vec<Uuid>>,
    pub status_ids: Option<Vec<Uuid>>,
    /// Задачи, у которых есть номер хотя бы в одной из этих серий (`task_series`).
    pub series_ids: Option<Vec<Uuid>>,
    /// Задачи с исходящей связью одного из этих типов (`task_links`).
    pub link_type_ids: Option<Vec<Uuid>>,
    /// Задачи, у которых записано хотя бы одно из этих полей (`task_fields`).
    pub field_ids: Option<Vec<Uuid>>,
    /// Задачи с собственным полем с одним из этих перечислений (`task_fields`).
    pub enum_ids: Option<Vec<Uuid>>,
    /// Все условия по значениям полей (И; `task_field_values`).
    pub field_values: Option<Vec<FieldCondition>>,
    /// Порядок задач; None — по времени создания, затем по id. У остальных видов — по имени (`sort_text`), затем по id.
    pub sort: Option<Vec<TaskSortKey>>,
    pub user_kind: Option<UserKind>,
    /// Точное значение ключа сортировки — например, нормализованное имя пользователя ([`super::sync::normalize_username`]).
    pub sort_key: Option<String>,
}

impl IndexQuery {
    /// Все сущности проекта.
    pub fn project(project_id: &Uuid) -> IndexQuery {
        IndexQuery {
            project_id: Some(*project_id),
            ..IndexQuery::default()
        }
    }

    /// Задачи проекта по фильтру (`TaskStorage.Query`).
    pub fn tasks(project_id: &Uuid, filter: Option<&TaskFilter>) -> IndexQuery {
        let f = filter.cloned().unwrap_or_default();
        IndexQuery {
            project_id: Some(*project_id),
            ids: f.ids,
            type_ids: f.type_ids,
            status_ids: f.status_ids,
            series_ids: f.series_ids,
            link_type_ids: f.link_type_ids,
            field_ids: f.field_ids,
            enum_ids: f.enum_ids,
            field_values: f.field_values,
            sort: f.sort,
            ..IndexQuery::default()
        }
    }

    /// Пользователи по виду; None — все.
    pub fn users(kind: Option<UserKind>) -> IndexQuery {
        IndexQuery {
            user_kind: kind,
            ..IndexQuery::default()
        }
    }

    pub fn with_id_prefix(mut self, key: &str) -> IndexQuery {
        self.id_prefix = Some(key.to_string());
        self
    }

    pub fn with_sort_key(mut self, key: &str) -> IndexQuery {
        self.sort_key = Some(key.to_string());
        self
    }
}

/// Файл области, который не удалось прочитать: путь относительно `.tasker` и причина (`WorkspaceProblem`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkspaceProblem {
    pub path: String,
    pub error: String,
}

/// Тип и перечисление собственного поля задач (`OwnFieldKind`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OwnFieldKind {
    pub field_type: FieldType,
    pub enum_id: Option<Uuid>,
}

/// Связь «источник → цель» (`LinkEdge`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LinkEdge {
    pub source_id: Uuid,
    pub target_id: Uuid,
}

/// Номер, который есть у нескольких задач серии (`NumberConflict`): задачи — по createdAt, затем по id.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NumberConflict {
    pub series_id: Uuid,
    pub number: i32,
    pub task_ids: Vec<Uuid>,
}

/// SQL с именованными параметрами (`@name`).
struct Select {
    sql: String,
    params: Vec<(String, Value)>,
}

impl Select {
    // Только параметры, которые есть в SQL: SQLite отвергает лишние (например, @value у сравнения по числу).
    fn bindings(&self) -> Vec<(&str, &dyn ToSql)> {
        self.params
            .iter()
            .filter(|(name, _)| uses(&self.sql, name))
            .map(|(name, value)| (name.as_str(), value as &dyn ToSql))
            .collect()
    }
}

fn uses(sql: &str, name: &str) -> bool {
    sql.match_indices(name)
        .any(|(at, _)| !sql[at + name.len()..].starts_with(|c: char| c.is_ascii_alphanumeric() || c == '_'))
}

fn json_array(ids: &[Uuid]) -> Value {
    let texts: Vec<String> = ids.iter().map(guid_d).collect();
    Value::Text(serde_json::to_string(&texts).expect("strings serialize"))
}

fn key(id: &Uuid) -> Value {
    Value::Text(guid_d(id))
}

/// `SELECT {what} FROM files WHERE …` по условиям (`WorkspaceIndex.Select`).
fn select(kind: EntityKind, query: &IndexQuery, what: &str) -> Select {
    let mut wheres: Vec<String> = vec!["kind = @kind".into(), "error IS NULL".into()];
    let mut params: Vec<(String, Value)> = vec![("@kind".into(), Value::Text(kind_name(kind).into()))];

    if let Some(project) = &query.project_id {
        wheres.push("project_id = @project".into());
        params.push(("@project".into(), key(project)));
    }
    if let Some(prefix) = &query.id_prefix {
        // Ключ — шестнадцатеричные цифры и дефисы: знаков LIKE в нём нет.
        wheres.push("id LIKE @idPrefix".into());
        params.push(("@idPrefix".into(), Value::Text(format!("{prefix}%"))));
    }
    if let Some(user_kind) = query.user_kind {
        wheres.push("user_kind = @userKind".into());
        params.push((
            "@userKind".into(),
            Value::Text(
                match user_kind {
                    UserKind::Human => "Human",
                    UserKind::Agent => "Agent",
                }
                .into(),
            ),
        ));
    }
    if let Some(sort_key) = &query.sort_key {
        wheres.push("sort_text = @sortKey".into());
        params.push(("@sortKey".into(), Value::Text(sort_key.clone())));
    }
    // Список id — одним параметром через json_each, без ограничения на число параметров.
    let mut add_in = |column: &str, name: &str, ids: &Option<Vec<Uuid>>| {
        if let Some(ids) = ids {
            wheres.push(format!("{column} IN (SELECT value FROM json_each({name}))"));
            params.push((name.into(), json_array(ids)));
        }
    };
    add_in("id", "@ids", &query.ids);
    add_in("type_id", "@types", &query.type_ids);
    add_in("status_id", "@statuses", &query.status_ids);
    let mut add_sub = |table: &str, column: &str, name: &str, ids: &Option<Vec<Uuid>>| {
        if let Some(ids) = ids {
            wheres.push(format!(
                "path IN (SELECT path FROM {table} WHERE {column} IN (SELECT value FROM json_each({name})))"
            ));
            params.push((name.into(), json_array(ids)));
        }
    };
    add_sub("task_series", "series_id", "@series", &query.series_ids);
    add_sub("task_links", "type_id", "@linkTypes", &query.link_type_ids);
    add_sub("task_fields", "field_id", "@fields", &query.field_ids);
    add_sub("task_fields", "enum_id", "@enums", &query.enum_ids);

    for (i, condition) in query.field_values.as_deref().unwrap_or(&[]).iter().enumerate() {
        wheres.push(field_sql(condition, i));
        if let Some(catalog) = &condition.field_id {
            params.push((format!("@field{i}"), key(catalog)));
        }
        params.push((format!("@own{i}"), Value::Text(condition.key())));
        params.push((format!("@ownType{i}"), Value::Integer(field_type_code(condition.field_type))));
        if let Some(value) = &condition.value {
            params.push((format!("@value{i}"), Value::Text(value.clone())));
        }
        if let Some(number) = condition.number {
            params.push((format!("@number{i}"), Value::Real(number)));
        }
        if let Some(type_ids) = &condition.type_ids {
            params.push((format!("@fieldTypes{i}"), json_array(type_ids)));
        }
    }

    Select {
        sql: format!("SELECT {what} FROM files WHERE {}", wheres.join(" AND ")),
        params,
    }
}

/// Условие по полю в SQL (`WorkspaceIndex.FieldSql`). «Есть значение, которое…» — подзапрос к `task_field_values`; «нет
/// значения…» — его отрицание (поэтому `!=` и `:unset` берут и задачи без значения). Строка относится к условию, если это поле
/// каталога (`field_id`) или собственное поле с тем же именем и типом. Подключённость поля: тип задачи из списка типов, где поле
/// каталога есть, или запись о поле в самой задаче (`task_fields`).
fn field_sql(c: &FieldCondition, i: usize) -> String {
    let (value, number) = (format!("@value{i}"), format!("@number{i}"));
    let mut row = format!("(own_name = @own{i} AND own_type = @ownType{i})");
    if c.field_id.is_some() {
        row = format!("(field_id = @field{i} OR {row})");
    }
    let values = format!("SELECT path FROM task_field_values WHERE {row}");
    let mut attached = format!("path IN (SELECT path FROM task_fields WHERE {row})");
    if c.type_ids.is_some() {
        attached = format!("(type_id IN (SELECT value FROM json_each(@fieldTypes{i})) OR {attached})");
    }
    // int и float — по числовому столбцу, date — по тексту yyyy-MM-dd (сортируется как текст).
    let ordered = |sign: &str| {
        if c.number.is_some() {
            format!("path IN ({values} AND number {sign} {number})")
        } else {
            format!("path IN ({values} AND value {sign} {value})")
        }
    };
    match c.operator {
        FieldOperator::Equal => format!("path IN ({values} AND value = {value})"),
        FieldOperator::NotEqual => format!("path NOT IN ({values} AND value = {value})"),
        FieldOperator::Greater => ordered(">"),
        FieldOperator::GreaterOrEqual => ordered(">="),
        FieldOperator::Less => ordered("<"),
        FieldOperator::LessOrEqual => ordered("<="),
        FieldOperator::Set => format!("path IN ({values})"),
        FieldOperator::Unset => format!("path NOT IN ({values})"),
        FieldOperator::Attached => attached,
        FieldOperator::Detached => format!("NOT {attached}"),
    }
}

fn order(kind: EntityKind, query: &IndexQuery) -> String {
    if kind != EntityKind::Task {
        " ORDER BY sort_text, id".into()
    } else {
        order_by(query.sort.as_deref())
    }
}

fn read_data<T: DeserializeOwned>(connection: &Connection, select: &Select) -> io::Result<Vec<T>> {
    let mut statement = connection.prepare(&select.sql).map_err(db_error)?;
    let rows = statement
        .query_map(select.bindings().as_slice(), |row| row.get::<_, String>(0))
        .map_err(db_error)?;
    let mut result = Vec::new();
    for json in rows {
        let json = json.map_err(db_error)?;
        result.push(serde_json::from_str(&json).map_err(|e| io::Error::other(format!("Index data: {e}")))?);
    }
    Ok(result)
}

fn read_ids(connection: &Connection, select: &Select) -> io::Result<Vec<Uuid>> {
    let mut statement = connection.prepare(&select.sql).map_err(db_error)?;
    let rows = statement
        .query_map(select.bindings().as_slice(), |row| row.get::<_, String>(0))
        .map_err(db_error)?;
    let mut result = Vec::new();
    for id in rows {
        let id = id.map_err(db_error)?;
        result.push(Uuid::try_parse(&id).map_err(|e| io::Error::other(format!("Index id '{id}': {e}")))?);
    }
    Ok(result)
}

fn scalar_count(connection: &Connection, select: &Select) -> io::Result<usize> {
    let count: i64 = connection
        .query_row(&select.sql, select.bindings().as_slice(), |row| row.get(0))
        .map_err(db_error)?;
    Ok(count.max(0) as usize)
}

fn parse_id(text: &str) -> io::Result<Uuid> {
    Uuid::try_parse(text).map_err(|e| io::Error::other(format!("Index id '{text}': {e}")))
}

/// Задачи по подзапросу путей, по createdAt, затем по id.
fn tasks_where(connection: &Connection, path_subquery: &str, bindings: &[(&str, &dyn ToSql)]) -> io::Result<Vec<TaskItem>> {
    let sql = format!("SELECT data FROM files WHERE path IN ({path_subquery}) ORDER BY {DEFAULT_ORDER}");
    let mut statement = connection.prepare(&sql).map_err(db_error)?;
    let rows = statement.query_map(bindings, |row| row.get::<_, String>(0)).map_err(db_error)?;
    let mut result = Vec::new();
    for json in rows {
        let json = json.map_err(db_error)?;
        result.push(serde_json::from_str(&json).map_err(|e| io::Error::other(format!("Index data: {e}")))?);
    }
    Ok(result)
}

impl WorkspaceIndex {
    /// Все сущности вида `T` по условиям, в порядке запроса.
    pub fn all<T: IndexEntity>(&self, query: &IndexQuery) -> io::Result<Vec<T>> {
        let connection = self.connect()?;
        let mut select = select(T::KIND, query, "data");
        select.sql.push_str(&order(T::KIND, query));
        read_data(&connection, &select)
    }

    /// Страница сущностей с общим числом (`GetRange`).
    pub fn range<T: IndexEntity>(&self, query: &IndexQuery, page: Page) -> io::Result<ListPage<T>> {
        let connection = self.connect()?;
        let total_count = scalar_count(&connection, &select(T::KIND, query, "count(*)"))?;
        let mut select = select(T::KIND, query, "data");
        select.sql.push_str(&order(T::KIND, query));
        select.sql.push_str(" LIMIT @limit OFFSET @offset");
        select.params.push(("@limit".into(), Value::Integer(page.limit as i64)));
        select.params.push(("@offset".into(), Value::Integer(page.offset as i64)));
        Ok(ListPage {
            total_count,
            offset: page.offset,
            limit: page.limit,
            data: read_data(&connection, &select)?,
        })
    }

    /// Id сущностей по условиям, в порядке запроса; сами сущности не читаются.
    pub fn ids(&self, kind: EntityKind, query: &IndexQuery) -> io::Result<Vec<Uuid>> {
        let connection = self.connect()?;
        let mut select = select(kind, query, "id");
        select.sql.push_str(&order(kind, query));
        read_ids(&connection, &select)
    }

    pub fn count(&self, kind: EntityKind, query: &IndexQuery) -> io::Result<usize> {
        scalar_count(&self.connect()?, &select(kind, query, "count(*)"))
    }

    /// Первая сущность по условиям в порядке запроса; None — нет.
    pub fn first<T: IndexEntity>(&self, query: &IndexQuery) -> io::Result<Option<T>> {
        let connection = self.connect()?;
        let mut select = select(T::KIND, query, "data");
        select.sql.push_str(&order(T::KIND, query));
        select.sql.push_str(" LIMIT 1");
        Ok(read_data(&connection, &select)?.into_iter().next())
    }

    /// Файлы, которые не удалось прочитать, по пути; страница с общим числом.
    pub fn problems(&self, page: Page) -> io::Result<ListPage<WorkspaceProblem>> {
        let connection = self.connect()?;
        let total: i64 = connection
            .query_row("SELECT count(*) FROM files WHERE error IS NOT NULL", [], |row| row.get(0))
            .map_err(db_error)?;
        let mut statement = connection
            .prepare("SELECT path, error FROM files WHERE error IS NOT NULL ORDER BY path LIMIT ?1 OFFSET ?2")
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![page.limit as i64, page.offset as i64], |row| {
                Ok(WorkspaceProblem {
                    path: row.get(0)?,
                    error: row.get(1)?,
                })
            })
            .map_err(db_error)?;
        let data = rows.collect::<Result<Vec<_>, _>>().map_err(db_error)?;
        Ok(ListPage {
            total_count: total.max(0) as usize,
            offset: page.offset,
            limit: page.limit,
            data,
        })
    }

    /// Сколько файлов внутри папки не удалось прочитать (`CountProblems`).
    pub fn count_problems(&self, folder: &std::path::Path) -> io::Result<usize> {
        let prefix = format!("{}/", self.relative(folder).unwrap_or_default());
        let count: i64 = self
            .connect()?
            .query_row(
                "SELECT count(*) FROM files WHERE error IS NOT NULL AND substr(path, 1, length(?1)) = ?1",
                params![prefix],
                |row| row.get(0),
            )
            .map_err(db_error)?;
        Ok(count.max(0) as usize)
    }

    /// Путь файла сущности проекта (полный) по id — из индекса; None — индекс такого файла не знает. Подсказка, а не истина:
    /// индекс мог отстать, поэтому вызывающий проверяет, что файл есть, и ищет по имени, если нет
    /// (`ProjectDirectory::find_files`).
    pub fn entity_path(&self, kind: EntityKind, project_id: &Uuid, id: &Uuid) -> io::Result<Option<PathBuf>> {
        let found: Option<String> = self
            .connect()?
            .query_row(
                "SELECT path FROM files WHERE kind = ?1 AND project_id = ?2 AND id = ?3 AND error IS NULL LIMIT 1",
                params![kind_name(kind), guid_d(project_id), guid_d(id)],
                |row| row.get(0),
            )
            .optional_io()?;
        Ok(found.map(|relative| self.absolute(&relative)))
    }

    /// Наибольший номер в серии; 0 — номеров нет. Серию не проверяем: недействительные ссылки тоже считаются. Для выдачи
    /// номера — внутри [`WorkspaceIndex::exclusive`].
    pub fn max_number(&self, project_id: &Uuid, series_id: &Uuid) -> io::Result<i32> {
        let max: i64 = self
            .connect()?
            .query_row(
                "SELECT coalesce(max(number), 0) FROM task_series WHERE project_id = ?1 AND series_id = ?2",
                params![guid_d(project_id), guid_d(series_id)],
                |row| row.get(0),
            )
            .map_err(db_error)?;
        Ok(max.clamp(i32::MIN as i64, i32::MAX as i64) as i32)
    }

    /// Различные пары «тип, перечисление» у собственных полей задач проекта с этим именем (ключ [`tasker_core::tasks::field_name_key`]).
    pub fn own_field_kinds(&self, project_id: &Uuid, name_key: &str) -> io::Result<Vec<OwnFieldKind>> {
        let connection = self.connect()?;
        let mut statement = connection
            .prepare("SELECT DISTINCT own_type, enum_id FROM task_fields WHERE own_name = ?1 AND project_id = ?2")
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![name_key, guid_d(project_id)], |row| {
                Ok((row.get::<_, i64>(0)?, row.get::<_, Option<String>>(1)?))
            })
            .map_err(db_error)?;
        let mut result = Vec::new();
        for row in rows {
            let (code, enum_id) = row.map_err(db_error)?;
            let Some(field_type) = field_type_of_code(code) else {
                continue;
            };
            result.push(OwnFieldKind {
                field_type,
                enum_id: enum_id.as_deref().map(parse_id).transpose()?,
            });
        }
        Ok(result)
    }

    /// Задачи с этим номером в серии: по createdAt, затем по id.
    pub fn tasks_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> io::Result<Vec<TaskItem>> {
        let (project, series) = (guid_d(project_id), guid_d(series_id));
        let number = number as i64;
        tasks_where(
            &self.connect()?,
            "SELECT path FROM task_series WHERE project_id = @project AND series_id = @series AND number = @number",
            &[("@project", &project), ("@series", &series), ("@number", &number)],
        )
    }

    /// Число входящих связей у каждой из задач (`task_links` по цели); у задач без входящих связей записи нет.
    pub fn count_linked_to(&self, project_id: &Uuid, target_ids: &[Uuid]) -> io::Result<HashMap<Uuid, usize>> {
        let mut counts = HashMap::new();
        if target_ids.is_empty() {
            return Ok(counts);
        }
        let connection = self.connect()?;
        let mut statement = connection
            .prepare(
                "SELECT target_id, count(*) FROM task_links WHERE project_id = ?1 \
                 AND target_id IN (SELECT value FROM json_each(?2)) GROUP BY target_id",
            )
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![guid_d(project_id), json_array(target_ids)], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
            })
            .map_err(db_error)?;
        for row in rows {
            let (target, count) = row.map_err(db_error)?;
            counts.insert(parse_id(&target)?, count.max(0) as usize);
        }
        Ok(counts)
    }

    /// Исходящие связи типа у перечисленных задач: источник → цели.
    pub fn link_targets(&self, project_id: &Uuid, type_id: &Uuid, source_ids: &[Uuid]) -> io::Result<HashMap<Uuid, Vec<Uuid>>> {
        let mut result: HashMap<Uuid, Vec<Uuid>> = HashMap::new();
        if source_ids.is_empty() {
            return Ok(result);
        }
        let connection = self.connect()?;
        let mut statement = connection
            .prepare(
                "SELECT DISTINCT source_id, target_id FROM task_links WHERE project_id = ?1 AND type_id = ?2 \
                 AND source_id IN (SELECT value FROM json_each(?3))",
            )
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![guid_d(project_id), guid_d(type_id), json_array(source_ids)], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(db_error)?;
        for row in rows {
            let (source, target) = row.map_err(db_error)?;
            result.entry(parse_id(&source)?).or_default().push(parse_id(&target)?);
        }
        Ok(result)
    }

    /// Все связи типа проекта (источник → цель).
    pub fn link_edges(&self, project_id: &Uuid, type_id: &Uuid) -> io::Result<Vec<LinkEdge>> {
        let connection = self.connect()?;
        let mut statement = connection
            .prepare("SELECT DISTINCT source_id, target_id FROM task_links WHERE project_id = ?1 AND type_id = ?2")
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![guid_d(project_id), guid_d(type_id)], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(db_error)?;
        let mut result = Vec::new();
        for row in rows {
            let (source, target) = row.map_err(db_error)?;
            result.push(LinkEdge {
                source_id: parse_id(&source)?,
                target_id: parse_id(&target)?,
            });
        }
        Ok(result)
    }

    /// Задачи проекта с исходящей связью на `target_id` (входящие связи задачи): по createdAt, затем по id.
    pub fn tasks_linked_to(&self, project_id: &Uuid, target_id: &Uuid) -> io::Result<Vec<TaskItem>> {
        let (project, target) = (guid_d(project_id), guid_d(target_id));
        tasks_where(
            &self.connect()?,
            "SELECT path FROM task_links WHERE project_id = @project AND target_id = @target",
            &[("@project", &project), ("@target", &target)],
        )
    }

    /// Задачи со связью на задачу или тип, которых нет: по createdAt, затем по id. Задача «есть», если её файл прочитан;
    /// нечитаемые файлы чистка учитывает сама ([`WorkspaceIndex::count_problems`]).
    pub fn tasks_with_invalid_links(&self, project_id: &Uuid, known_type_ids: &[Uuid]) -> io::Result<Vec<TaskItem>> {
        let connection = self.connect()?;
        let sql = format!(
            "SELECT data FROM files WHERE kind = 'Task' AND project_id = @project AND error IS NULL AND path IN (\
             SELECT l.path FROM task_links l WHERE l.project_id = @project AND (\
             l.type_id NOT IN (SELECT value FROM json_each(@known)) OR NOT EXISTS (\
             SELECT 1 FROM files t WHERE t.kind = 'Task' AND t.project_id = @project AND t.error IS NULL AND t.id = l.target_id))) \
             ORDER BY {DEFAULT_ORDER}"
        );
        let select = Select {
            sql,
            params: vec![("@project".into(), key(project_id)), ("@known".into(), json_array(known_type_ids))],
        };
        read_data(&connection, &select)
    }

    /// Номера, которые есть у нескольких задач: по серии и номеру; задачи — по createdAt, затем по id.
    pub fn number_conflicts(&self, project_id: &Uuid) -> io::Result<Vec<NumberConflict>> {
        let connection = self.connect()?;
        let mut statement = connection
            .prepare(
                "SELECT DISTINCT series_id, number, task_id, created_ticks FROM task_series \
                 WHERE project_id = ?1 AND (series_id, number) IN (\
                 SELECT series_id, number FROM task_series WHERE project_id = ?1 \
                 GROUP BY series_id, number HAVING count(DISTINCT task_id) > 1) \
                 ORDER BY series_id, number, created_ticks, task_id",
            )
            .map_err(db_error)?;
        let rows = statement
            .query_map(params![guid_d(project_id)], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?, row.get::<_, String>(2)?))
            })
            .map_err(db_error)?;
        let mut result: Vec<NumberConflict> = Vec::new();
        for row in rows {
            let (series, number, task) = row.map_err(db_error)?;
            let (series_id, task_id) = (parse_id(&series)?, parse_id(&task)?);
            let number = number as i32;
            match result.last_mut() {
                Some(last) if last.series_id == series_id && last.number == number => last.task_ids.push(task_id),
                _ => result.push(NumberConflict {
                    series_id,
                    number,
                    task_ids: vec![task_id],
                }),
            }
        }
        Ok(result)
    }

    /// Задачи с номером в серии вне `known_series_ids`: по createdAt, затем по id.
    pub fn tasks_with_series_not_in(&self, project_id: &Uuid, known_series_ids: &[Uuid]) -> io::Result<Vec<TaskItem>> {
        let project = guid_d(project_id);
        let known = json_array(known_series_ids);
        tasks_where(
            &self.connect()?,
            "SELECT path FROM task_series WHERE project_id = @project AND series_id NOT IN (SELECT value FROM json_each(@known))",
            &[("@project", &project), ("@known", &known)],
        )
    }
}

trait OptionalIo<T> {
    fn optional_io(self) -> io::Result<Option<T>>;
}

impl<T> OptionalIo<T> for Result<T, rusqlite::Error> {
    fn optional_io(self) -> io::Result<Option<T>> {
        match self {
            Ok(value) => Ok(Some(value)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(db_error(e)),
        }
    }
}
