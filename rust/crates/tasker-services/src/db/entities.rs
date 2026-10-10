//! Сущности проекта в таблицах БД (`*Storage` в `Tasker.Storage.Db`): чтение одной и списка в порядке .NET, вставка, условное
//! обновление по версии (`UPDATE … WHERE Version = @expected`, затем замена дочерних строк) и удаление по версии (дочерние строки
//! удаляет каскад БД).
use super::tasks;
use super::values::{INITIAL_VERSION, g, guid_col, optional_guid_col};
use rusqlite::{Connection, OptionalExtension, Row, params};
use tasker_core::model::{
    Board, BoardColumn, ColumnFieldFilter, FieldDefinition, FieldEnum, FieldEnumValue, FieldOperator, FieldType, LinkType, Series, Status,
    StatusSet, TaskItem, TaskType, TaskTypeField,
};
use tasker_core::tasks::Page;
use uuid::Uuid;

/// Сущность с таблицами в БД.
pub trait DbEntity: Sized {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>>;
    /// Общее число и сущности проекта в порядке списка; `page` None — все.
    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)>;
    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()>;
    /// false — записи нет или версия уже другая.
    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool>;
    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool>;
}

/// Описание: NULL в столбце — пустая строка в модели (`DbDescription`).
fn description_to_column(text: &str) -> Option<&str> {
    if text.is_empty() { None } else { Some(text) }
}

fn version_text(version: i64) -> String {
    version.to_string()
}

fn next(expected: i32) -> i64 {
    i64::from(expected) + 1
}

/// `LIMIT/OFFSET` страницы; без страницы — всё (`LIMIT -1`).
fn limits(page: Option<Page>) -> (i64, i64) {
    match page {
        Some(p) => (p.limit as i64, p.offset as i64),
        None => (-1, 0),
    }
}

/// Общее число и строки страницы по `SELECT … FROM {from} WHERE "ProjectId" = ?1 ORDER BY {order}`.
fn list<T>(
    c: &Connection,
    columns: &str,
    table: &str,
    order: &str,
    project_id: &Uuid,
    page: Option<Page>,
    map: impl Fn(&Row<'_>) -> rusqlite::Result<T>,
) -> rusqlite::Result<(usize, Vec<T>)> {
    let total: i64 = c.query_row(
        &format!("SELECT COUNT(*) FROM \"{table}\" WHERE \"ProjectId\" = ?1"),
        params![g(project_id)],
        |row| row.get(0),
    )?;
    let (limit, offset) = limits(page);
    let mut statement = c.prepare(&format!(
        "SELECT {columns} FROM \"{table}\" WHERE \"ProjectId\" = ?1 ORDER BY {order} LIMIT ?2 OFFSET ?3"
    ))?;
    let data = statement
        .query_map(params![g(project_id), limit, offset], map)?
        .collect::<rusqlite::Result<Vec<_>>>()?;
    Ok((total.max(0) as usize, data))
}

fn get<T>(
    c: &Connection,
    columns: &str,
    table: &str,
    project_id: &Uuid,
    id: &Uuid,
    map: impl FnOnce(&Row<'_>) -> rusqlite::Result<T>,
) -> rusqlite::Result<Option<T>> {
    c.query_row(
        &format!("SELECT {columns} FROM \"{table}\" WHERE \"ProjectId\" = ?1 AND \"Id\" = ?2 LIMIT 1"),
        params![g(project_id), g(id)],
        map,
    )
    .optional()
}

fn delete(c: &Connection, table: &str, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
    Ok(c.execute(
        &format!("DELETE FROM \"{table}\" WHERE \"ProjectId\" = ?1 AND \"Id\" = ?2 AND \"Version\" = ?3"),
        params![g(project_id), g(id), expected],
    )? > 0)
}

/// Тип поля числом (`(int)FieldType`): string 0, int 1, float 2, bool 3, date 4, enum 5.
pub fn field_type_code(field_type: FieldType) -> i64 {
    match field_type {
        FieldType::String => 0,
        FieldType::Int => 1,
        FieldType::Float => 2,
        FieldType::Bool => 3,
        FieldType::Date => 4,
        FieldType::Enum => 5,
    }
}

pub fn field_type_of_code(code: i64) -> Option<FieldType> {
    [
        FieldType::String,
        FieldType::Int,
        FieldType::Float,
        FieldType::Bool,
        FieldType::Date,
        FieldType::Enum,
    ]
    .get(usize::try_from(code).ok()?)
    .copied()
}

fn field_type_col(row: &Row<'_>, column: usize) -> rusqlite::Result<FieldType> {
    let code: i64 = row.get(column)?;
    field_type_of_code(code).ok_or_else(|| super::values::conversion(column, format!("unknown field type {code}")))
}

/// Id строк дочерней таблицы по родителю, упорядоченные (`Include` + `OrderBy(Position)`).
fn child_ids(c: &Connection, sql: &str, parent: &Uuid) -> rusqlite::Result<Vec<Uuid>> {
    let mut statement = c.prepare(sql)?;
    statement.query_map(params![g(parent)], |row| guid_col(row, 0))?.collect()
}

// ---------------------------------------------------------------------------------------------------------------- статусы

const STATUS_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Color\", \"Description\", \"Version\"";

fn status_row(row: &Row<'_>) -> rusqlite::Result<Status> {
    Ok(Status {
        id: guid_col(row, 0)?,
        project_id: guid_col(row, 1)?,
        name: row.get(2)?,
        color: row.get(3)?,
        description: row.get::<_, Option<String>>(4)?.unwrap_or_default(),
        version: version_text(row.get(5)?),
    })
}

impl DbEntity for Status {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, STATUS_COLUMNS, "statuses", project_id, id, status_row)
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(c, STATUS_COLUMNS, "statuses", "\"Name\", \"Id\"", project_id, page, status_row)
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"statuses\" (\"Id\", \"Color\", \"Description\", \"Name\", \"ProjectId\", \"Version\") \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![
                g(&self.id),
                self.color,
                description_to_column(&self.description),
                self.name,
                g(&self.project_id),
                INITIAL_VERSION
            ],
        )?;
        Ok(())
    }

    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        Ok(c.execute(
            "UPDATE \"statuses\" SET \"Name\" = ?1, \"Color\" = ?2, \"Description\" = ?3, \"Version\" = ?4 \
             WHERE \"ProjectId\" = ?5 AND \"Id\" = ?6 AND \"Version\" = ?7",
            params![
                self.name,
                self.color,
                description_to_column(&self.description),
                next(expected),
                g(&self.project_id),
                g(&self.id),
                expected
            ],
        )? > 0)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "statuses", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- наборы статусов

const SET_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Version\"";

fn status_set_row(c: &Connection) -> impl Fn(&Row<'_>) -> rusqlite::Result<StatusSet> + '_ {
    move |row| {
        let id = guid_col(row, 0)?;
        Ok(StatusSet {
            id,
            project_id: guid_col(row, 1)?,
            name: row.get(2)?,
            status_ids: child_ids(
                c,
                "SELECT \"StatusId\" FROM \"status_set_statuses\" WHERE \"StatusSetId\" = ?1 ORDER BY \"Position\"",
                &id,
            )?,
            version: version_text(row.get(3)?),
        })
    }
}

fn insert_set_items(c: &Connection, set: &StatusSet) -> rusqlite::Result<()> {
    for (i, status_id) in set.status_ids.iter().enumerate() {
        c.execute(
            "INSERT INTO \"status_set_statuses\" (\"StatusSetId\", \"StatusId\", \"Position\") VALUES (?1, ?2, ?3)",
            params![g(&set.id), g(status_id), i as i64],
        )?;
    }
    Ok(())
}

impl DbEntity for StatusSet {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, SET_COLUMNS, "status_sets", project_id, id, status_set_row(c))
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(
            c,
            SET_COLUMNS,
            "status_sets",
            "\"Name\", \"Id\"",
            project_id,
            page,
            status_set_row(c),
        )
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"status_sets\" (\"Id\", \"Name\", \"ProjectId\", \"Version\") VALUES (?1, ?2, ?3, ?4)",
            params![g(&self.id), self.name, g(&self.project_id), INITIAL_VERSION],
        )?;
        insert_set_items(c, self)
    }

    // Строку набора не пересоздаём: на неё ссылаются типы задач и доски. Список статусов заменяем целиком.
    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        let updated = c.execute(
            "UPDATE \"status_sets\" SET \"Name\" = ?1, \"Version\" = ?2 WHERE \"ProjectId\" = ?3 AND \"Id\" = ?4 AND \"Version\" = ?5",
            params![self.name, next(expected), g(&self.project_id), g(&self.id), expected],
        )?;
        if updated == 0 {
            return Ok(false);
        }
        c.execute(
            "DELETE FROM \"status_set_statuses\" WHERE \"StatusSetId\" = ?1",
            params![g(&self.id)],
        )?;
        insert_set_items(c, self)?;
        Ok(true)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "status_sets", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- типы задач

const TYPE_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Description\", \"StatusSetId\", \"Version\"";

fn task_type_row(c: &Connection) -> impl Fn(&Row<'_>) -> rusqlite::Result<TaskType> + '_ {
    move |row| {
        let id = guid_col(row, 0)?;
        let mut statement =
            c.prepare("SELECT \"FieldId\", \"Required\" FROM \"task_type_fields\" WHERE \"TypeId\" = ?1 ORDER BY \"Position\"")?;
        let fields = statement
            .query_map(params![g(&id)], |r| {
                Ok(TaskTypeField {
                    field_id: guid_col(r, 0)?,
                    required: r.get(1)?,
                })
            })?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        Ok(TaskType {
            id,
            project_id: guid_col(row, 1)?,
            name: row.get(2)?,
            description: row.get::<_, Option<String>>(3)?.unwrap_or_default(),
            status_set_id: guid_col(row, 4)?,
            fields,
            version: version_text(row.get(5)?),
        })
    }
}

fn insert_type_fields(c: &Connection, task_type: &TaskType) -> rusqlite::Result<()> {
    for (i, field) in task_type.fields.iter().enumerate() {
        c.execute(
            "INSERT INTO \"task_type_fields\" (\"TypeId\", \"FieldId\", \"Position\", \"Required\") VALUES (?1, ?2, ?3, ?4)",
            params![g(&task_type.id), g(&field.field_id), i as i64, field.required],
        )?;
    }
    Ok(())
}

impl DbEntity for TaskType {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, TYPE_COLUMNS, "task_types", project_id, id, task_type_row(c))
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(
            c,
            TYPE_COLUMNS,
            "task_types",
            "\"Name\", \"Id\"",
            project_id,
            page,
            task_type_row(c),
        )
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"task_types\" (\"Id\", \"Description\", \"Name\", \"ProjectId\", \"StatusSetId\", \"Version\") \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![
                g(&self.id),
                description_to_column(&self.description),
                self.name,
                g(&self.project_id),
                g(&self.status_set_id),
                INITIAL_VERSION
            ],
        )?;
        insert_type_fields(c, self)
    }

    // Строку типа не пересоздаём: на неё ссылаются задачи. Список полей заменяем целиком.
    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        let updated = c.execute(
            "UPDATE \"task_types\" SET \"Name\" = ?1, \"Description\" = ?2, \"StatusSetId\" = ?3, \"Version\" = ?4 \
             WHERE \"ProjectId\" = ?5 AND \"Id\" = ?6 AND \"Version\" = ?7",
            params![
                self.name,
                description_to_column(&self.description),
                g(&self.status_set_id),
                next(expected),
                g(&self.project_id),
                g(&self.id),
                expected
            ],
        )?;
        if updated == 0 {
            return Ok(false);
        }
        c.execute("DELETE FROM \"task_type_fields\" WHERE \"TypeId\" = ?1", params![g(&self.id)])?;
        insert_type_fields(c, self)?;
        Ok(true)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "task_types", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- серии

const SERIES_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Prefix\", \"Version\"";

fn series_row(row: &Row<'_>) -> rusqlite::Result<Series> {
    Ok(Series {
        id: guid_col(row, 0)?,
        project_id: guid_col(row, 1)?,
        name: row.get(2)?,
        prefix: row.get(3)?,
        version: version_text(row.get(4)?),
    })
}

impl DbEntity for Series {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, SERIES_COLUMNS, "series", project_id, id, series_row)
    }

    // Порядок — по префиксу посимвольно (ordinal), затем по id; .NET сортирует в памяти, префиксы — латиница и цифры, поэтому
    // порядок BINARY совпадает.
    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(c, SERIES_COLUMNS, "series", "\"Prefix\", \"Id\"", project_id, page, series_row)
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"series\" (\"Id\", \"Name\", \"Prefix\", \"ProjectId\", \"Version\") VALUES (?1, ?2, ?3, ?4, ?5)",
            params![g(&self.id), self.name, self.prefix, g(&self.project_id), INITIAL_VERSION],
        )?;
        Ok(())
    }

    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        Ok(c.execute(
            "UPDATE \"series\" SET \"Name\" = ?1, \"Prefix\" = ?2, \"Version\" = ?3 \
             WHERE \"ProjectId\" = ?4 AND \"Id\" = ?5 AND \"Version\" = ?6",
            params![self.name, self.prefix, next(expected), g(&self.project_id), g(&self.id), expected],
        )? > 0)
    }

    /// Номера задач в этой серии остаются недействительными ссылками — их уберёт чистка.
    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "series", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- типы связей

const LINK_TYPE_COLUMNS: &str =
    "\"Id\", \"ProjectId\", \"Name\", \"OutwardName\", \"InwardName\", \"AllowCycles\", \"Hierarchical\", \"Version\"";

fn link_type_row(row: &Row<'_>) -> rusqlite::Result<LinkType> {
    Ok(LinkType {
        id: guid_col(row, 0)?,
        project_id: guid_col(row, 1)?,
        name: row.get(2)?,
        outward_name: row.get(3)?,
        inward_name: row.get(4)?,
        allow_cycles: row.get(5)?,
        hierarchical: row.get(6)?,
        version: version_text(row.get(7)?),
    })
}

impl DbEntity for LinkType {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, LINK_TYPE_COLUMNS, "link_types", project_id, id, link_type_row)
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(
            c,
            LINK_TYPE_COLUMNS,
            "link_types",
            "\"Name\", \"Id\"",
            project_id,
            page,
            link_type_row,
        )
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"link_types\" (\"Id\", \"AllowCycles\", \"Hierarchical\", \"InwardName\", \"Name\", \"OutwardName\", \
             \"ProjectId\", \"Version\") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![
                g(&self.id),
                self.allow_cycles,
                self.hierarchical,
                self.inward_name,
                self.name,
                self.outward_name,
                g(&self.project_id),
                INITIAL_VERSION
            ],
        )?;
        Ok(())
    }

    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        Ok(c.execute(
            "UPDATE \"link_types\" SET \"Name\" = ?1, \"OutwardName\" = ?2, \"InwardName\" = ?3, \"AllowCycles\" = ?4, \
             \"Hierarchical\" = ?5, \"Version\" = ?6 WHERE \"ProjectId\" = ?7 AND \"Id\" = ?8 AND \"Version\" = ?9",
            params![
                self.name,
                self.outward_name,
                self.inward_name,
                self.allow_cycles,
                self.hierarchical,
                next(expected),
                g(&self.project_id),
                g(&self.id),
                expected
            ],
        )? > 0)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "link_types", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- поля каталога

const FIELD_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Type\", \"Multiple\", \"EnumId\", \"Version\"";

fn field_row(row: &Row<'_>) -> rusqlite::Result<FieldDefinition> {
    Ok(FieldDefinition {
        id: guid_col(row, 0)?,
        project_id: guid_col(row, 1)?,
        name: row.get(2)?,
        field_type: field_type_col(row, 3)?,
        multiple: row.get(4)?,
        enum_id: optional_guid_col(row, 5)?,
        version: version_text(row.get(6)?),
    })
}

impl DbEntity for FieldDefinition {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, FIELD_COLUMNS, "fields", project_id, id, field_row)
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(c, FIELD_COLUMNS, "fields", "\"Name\", \"Id\"", project_id, page, field_row)
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"fields\" (\"Id\", \"EnumId\", \"Multiple\", \"Name\", \"ProjectId\", \"Type\", \"Version\") \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                g(&self.id),
                self.enum_id.as_ref().map(g),
                self.multiple,
                self.name,
                g(&self.project_id),
                field_type_code(self.field_type),
                INITIAL_VERSION
            ],
        )?;
        Ok(())
    }

    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        Ok(c.execute(
            "UPDATE \"fields\" SET \"Name\" = ?1, \"Type\" = ?2, \"Multiple\" = ?3, \"EnumId\" = ?4, \"Version\" = ?5 \
             WHERE \"ProjectId\" = ?6 AND \"Id\" = ?7 AND \"Version\" = ?8",
            params![
                self.name,
                field_type_code(self.field_type),
                self.multiple,
                self.enum_id.as_ref().map(g),
                next(expected),
                g(&self.project_id),
                g(&self.id),
                expected
            ],
        )? > 0)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "fields", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- перечисления

const ENUM_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Version\"";

fn enum_row(c: &Connection) -> impl Fn(&Row<'_>) -> rusqlite::Result<FieldEnum> + '_ {
    move |row| {
        let id = guid_col(row, 0)?;
        let mut statement = c.prepare("SELECT \"Id\", \"Name\" FROM \"field_enum_values\" WHERE \"EnumId\" = ?1 ORDER BY \"Position\"")?;
        let values = statement
            .query_map(params![g(&id)], |r| {
                Ok(FieldEnumValue {
                    id: guid_col(r, 0)?,
                    name: r.get(1)?,
                })
            })?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        Ok(FieldEnum {
            id,
            project_id: guid_col(row, 1)?,
            name: row.get(2)?,
            values,
            version: version_text(row.get(3)?),
        })
    }
}

fn insert_enum_values(c: &Connection, value: &FieldEnum) -> rusqlite::Result<()> {
    for (i, item) in value.values.iter().enumerate() {
        c.execute(
            "INSERT INTO \"field_enum_values\" (\"EnumId\", \"Id\", \"Name\", \"Position\") VALUES (?1, ?2, ?3, ?4)",
            params![g(&value.id), g(&item.id), item.name, i as i64],
        )?;
    }
    Ok(())
}

impl DbEntity for FieldEnum {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, ENUM_COLUMNS, "field_enums", project_id, id, enum_row(c))
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(c, ENUM_COLUMNS, "field_enums", "\"Name\", \"Id\"", project_id, page, enum_row(c))
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"field_enums\" (\"Id\", \"Name\", \"ProjectId\", \"Version\") VALUES (?1, ?2, ?3, ?4)",
            params![g(&self.id), self.name, g(&self.project_id), INITIAL_VERSION],
        )?;
        insert_enum_values(c, self)
    }

    // Строку перечисления не пересоздаём: на неё ссылаются поля. Значения заменяем целиком.
    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        let updated = c.execute(
            "UPDATE \"field_enums\" SET \"Name\" = ?1, \"Version\" = ?2 WHERE \"ProjectId\" = ?3 AND \"Id\" = ?4 AND \"Version\" = ?5",
            params![self.name, next(expected), g(&self.project_id), g(&self.id), expected],
        )?;
        if updated == 0 {
            return Ok(false);
        }
        c.execute("DELETE FROM \"field_enum_values\" WHERE \"EnumId\" = ?1", params![g(&self.id)])?;
        insert_enum_values(c, self)?;
        Ok(true)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "field_enums", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- доски

const BOARD_COLUMNS: &str = "\"Id\", \"ProjectId\", \"Name\", \"Version\"";

fn board_row(c: &Connection) -> impl Fn(&Row<'_>) -> rusqlite::Result<Board> + '_ {
    move |row| {
        let id = guid_col(row, 0)?;
        let status_set_ids = child_ids(
            c,
            "SELECT \"StatusSetId\" FROM \"board_status_sets\" WHERE \"BoardId\" = ?1 ORDER BY \"Position\"",
            &id,
        )?;
        let mut statement = c.prepare("SELECT \"Id\", \"Name\" FROM \"board_columns\" WHERE \"BoardId\" = ?1 ORDER BY \"Position\"")?;
        let heads = statement
            .query_map(params![g(&id)], |r| Ok((guid_col(r, 0)?, r.get::<_, String>(1)?)))?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        let mut columns = Vec::with_capacity(heads.len());
        for (column_id, name) in heads {
            let status_ids = child_ids(
                c,
                "SELECT \"StatusId\" FROM \"board_column_statuses\" WHERE \"ColumnId\" = ?1 ORDER BY \"Position\"",
                &column_id,
            )?;
            let mut drops = c.prepare(
                "SELECT \"StatusSetId\", \"StatusId\" FROM \"board_column_drop_statuses\" WHERE \"ColumnId\" = ?1 \
                 ORDER BY \"StatusSetId\"",
            )?;
            let drop_statuses = drops
                .query_map(params![g(&column_id)], |r| Ok((guid_col(r, 0)?, guid_col(r, 1)?)))?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            let mut filters = c.prepare(
                "SELECT \"FieldId\", \"Operator\", \"Value\" FROM \"board_column_field_filters\" WHERE \"ColumnId\" = ?1 \
                 ORDER BY \"Position\"",
            )?;
            let field_conditions = filters
                .query_map(params![g(&column_id)], |r| {
                    let operator: String = r.get(1)?;
                    Ok(ColumnFieldFilter {
                        field_id: guid_col(r, 0)?,
                        operator: FieldOperator::ALL
                            .into_iter()
                            .find(|x| x.pascal() == operator)
                            .ok_or_else(|| super::values::conversion(1, format!("unknown operator '{operator}'")))?,
                        value: r.get(2)?,
                    })
                })?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            columns.push(BoardColumn {
                id: column_id,
                name,
                status_ids,
                field_conditions,
                drop_statuses,
            });
        }
        Ok(Board {
            id,
            project_id: guid_col(row, 1)?,
            name: row.get(2)?,
            status_set_ids,
            columns,
            version: version_text(row.get(3)?),
        })
    }
}

fn insert_board_parts(c: &Connection, board: &Board) -> rusqlite::Result<()> {
    for (i, set_id) in board.status_set_ids.iter().enumerate() {
        c.execute(
            "INSERT INTO \"board_status_sets\" (\"BoardId\", \"StatusSetId\", \"Position\") VALUES (?1, ?2, ?3)",
            params![g(&board.id), g(set_id), i as i64],
        )?;
    }
    for (i, column) in board.columns.iter().enumerate() {
        c.execute(
            "INSERT INTO \"board_columns\" (\"Id\", \"BoardId\", \"Name\", \"Position\") VALUES (?1, ?2, ?3, ?4)",
            params![g(&column.id), g(&board.id), column.name, i as i64],
        )?;
        for (j, status_id) in column.status_ids.iter().enumerate() {
            c.execute(
                "INSERT INTO \"board_column_statuses\" (\"ColumnId\", \"StatusId\", \"Position\") VALUES (?1, ?2, ?3)",
                params![g(&column.id), g(status_id), j as i64],
            )?;
        }
        for (set_id, status_id) in &column.drop_statuses {
            c.execute(
                "INSERT INTO \"board_column_drop_statuses\" (\"ColumnId\", \"StatusSetId\", \"StatusId\") VALUES (?1, ?2, ?3)",
                params![g(&column.id), g(set_id), g(status_id)],
            )?;
        }
        for (j, filter) in column.field_conditions.iter().enumerate() {
            c.execute(
                "INSERT INTO \"board_column_field_filters\" (\"ColumnId\", \"Position\", \"FieldId\", \"Operator\", \"Value\") \
                 VALUES (?1, ?2, ?3, ?4, ?5)",
                params![g(&column.id), j as i64, g(&filter.field_id), filter.operator.pascal(), filter.value],
            )?;
        }
    }
    Ok(())
}

impl DbEntity for Board {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        get(c, BOARD_COLUMNS, "boards", project_id, id, board_row(c))
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        list(c, BOARD_COLUMNS, "boards", "\"Name\", \"Id\"", project_id, page, board_row(c))
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        c.execute(
            "INSERT INTO \"boards\" (\"Id\", \"Name\", \"ProjectId\", \"Version\") VALUES (?1, ?2, ?3, ?4)",
            params![g(&self.id), self.name, g(&self.project_id), INITIAL_VERSION],
        )?;
        insert_board_parts(c, self)
    }

    // Наборы и колонки (со статусами и правилами переноса — каскадом) заменяются целиком.
    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        let updated = c.execute(
            "UPDATE \"boards\" SET \"Name\" = ?1, \"Version\" = ?2 WHERE \"ProjectId\" = ?3 AND \"Id\" = ?4 AND \"Version\" = ?5",
            params![self.name, next(expected), g(&self.project_id), g(&self.id), expected],
        )?;
        if updated == 0 {
            return Ok(false);
        }
        c.execute("DELETE FROM \"board_status_sets\" WHERE \"BoardId\" = ?1", params![g(&self.id)])?;
        c.execute("DELETE FROM \"board_columns\" WHERE \"BoardId\" = ?1", params![g(&self.id)])?;
        insert_board_parts(c, self)?;
        Ok(true)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "boards", project_id, id, expected)
    }
}

// ---------------------------------------------------------------------------------------------------------------- задачи

impl DbEntity for TaskItem {
    fn db_get(c: &Connection, project_id: &Uuid, id: &Uuid) -> rusqlite::Result<Option<Self>> {
        tasks::get(c, project_id, id)
    }

    fn db_list(c: &Connection, project_id: &Uuid, page: Option<Page>) -> rusqlite::Result<(usize, Vec<Self>)> {
        tasks::list(c, project_id, None, page)
    }

    fn db_insert(&self, c: &Connection) -> rusqlite::Result<()> {
        tasks::insert(c, self)
    }

    fn db_update(&self, c: &Connection, expected: i32) -> rusqlite::Result<bool> {
        tasks::update(c, self, expected)
    }

    fn db_delete(c: &Connection, project_id: &Uuid, id: &Uuid, expected: i32) -> rusqlite::Result<bool> {
        delete(c, "tasks", project_id, id, expected)
    }
}
