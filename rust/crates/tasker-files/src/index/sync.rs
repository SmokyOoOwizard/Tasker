//! Сверка индекса с диском (`WorkspaceIndex.Sync` в .NET): внутри указанных путей удаляются записи о пропавших файлах и
//! перечитываются новые и изменившиеся (по размеру и времени изменения). Одна транзакция.
use super::{db_error, kind_name};
use crate::error::Error;
use crate::files;
use crate::layout::{self, EntityKind, LayoutEntry, TaskerDirectory};
use crate::names;
use crate::write::read_bytes;
use rusqlite::{Connection, Transaction, params};
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldType, TaskField, TaskLink, TaskSeriesNumber, UserKind};
use tasker_core::tasks::{field_name_key, field_number};
use tasker_core::validate::to_lower_invariant;
use uuid::Uuid;

/// Что изменилось в индексе за одну сверку (`WorkspaceFileChange`): путь относительно `.tasker` и id сущности, если известен
/// (из содержимого файла, а у удалённого — из индекса). Наблюдатель (TSK-133) и вкладки десктопа узнают по этому списку, что
/// перечитать.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileChange {
    pub path: String,
    pub id: Option<Uuid>,
}

/// Итог сверки: сколько файлов перечитано, сколько записей удалено и что изменилось.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SyncReport {
    pub updated: usize,
    pub removed: usize,
    pub changed: Vec<FileChange>,
}

/// Отпечаток файла: размер и время изменения (наносекунды от эпохи Unix).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Stamp {
    size: i64,
    mtime: i64,
}

struct DiskFile {
    full_path: PathBuf,
    layout: LayoutEntry,
    stamp: Stamp,
}

/// Что индекс знает о файле: отпечаток и значение колонки id.
struct Known {
    stamp: Stamp,
    id: String,
}

pub(super) fn run(connection: &mut Connection, directory: &TaskerDirectory, scopes: &[String]) -> io::Result<SyncReport> {
    let transaction = connection.transaction().map_err(db_error)?;
    let indexed = indexed_stamps(&transaction, scopes)?;
    let started = tasker_core::perf::start();
    let on_disk = scan_disk(directory, scopes);
    tasker_core::perf::count("sync-scan", started);
    let started = tasker_core::perf::start();

    let mut report = SyncReport::default();
    for (path, known) in &indexed {
        if !on_disk.contains_key(path) {
            delete(&transaction, path)?;
            report.changed.push(FileChange {
                path: path.clone(),
                id: Uuid::try_parse(&known.id).ok(),
            });
            report.removed += 1;
        }
    }

    let mut paths: Vec<&String> = on_disk.keys().collect();
    paths.sort();
    for path in paths {
        let file = &on_disk[path];
        let known = indexed.get(path);
        if known.is_some_and(|k| k.stamp == file.stamp) {
            continue;
        }
        match read(path, file) {
            Some(row) => {
                let id = row.entity_id.or(file.layout.id);
                upsert(&transaction, &row)?;
                report.changed.push(FileChange { path: path.clone(), id });
            }
            None => {
                delete(&transaction, path)?;
                report.changed.push(FileChange {
                    path: path.clone(),
                    id: known.and_then(|k| Uuid::try_parse(&k.id).ok()).or(file.layout.id),
                });
            }
        }
        report.updated += 1;
    }
    tasker_core::perf::count("sync-indexed", started);
    let started = tasker_core::perf::start();
    transaction.commit().map_err(db_error)?;
    tasker_core::perf::count("sync-commit", started);
    Ok(report)
}

fn indexed_stamps(transaction: &Transaction<'_>, scopes: &[String]) -> io::Result<HashMap<String, Known>> {
    let mut result = HashMap::new();
    for scope in scopes {
        let (sql, prefix) = if scope.is_empty() {
            ("SELECT path, size, mtime, id FROM files", String::new())
        } else {
            (
                "SELECT path, size, mtime, id FROM files WHERE path = ?1 OR substr(path, 1, length(?2)) = ?2",
                format!("{scope}/"),
            )
        };
        let mut statement = transaction.prepare(sql).map_err(db_error)?;
        let bindings: Vec<&dyn rusqlite::types::ToSql> = if scope.is_empty() { vec![] } else { vec![scope, &prefix] };
        let rows: Vec<(String, i64, i64, String)> = statement
            .query_map(bindings.as_slice(), |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)))
            .map_err(db_error)?
            .collect::<Result<_, _>>()
            .map_err(db_error)?;
        for (path, size, mtime, id) in rows {
            result.insert(
                path,
                Known {
                    stamp: Stamp { size, mtime },
                    id,
                },
            );
        }
    }
    Ok(result)
}

fn stamp_of(metadata: &std::fs::Metadata) -> Stamp {
    let mtime = metadata
        .modified()
        .ok()
        .and_then(|t| match t.duration_since(std::time::UNIX_EPOCH) {
            Ok(d) => i64::try_from(d.as_nanos()).ok(),
            Err(e) => i64::try_from(e.duration().as_nanos()).ok().map(|n| -n),
        })
        .unwrap_or(0);
    Stamp {
        size: metadata.len() as i64,
        mtime,
    }
}

fn scan_disk(directory: &TaskerDirectory, scopes: &[String]) -> HashMap<String, DiskFile> {
    let mut result = HashMap::new();
    for scope in scopes {
        let full = if scope.is_empty() {
            directory.root().to_path_buf()
        } else {
            let mut path = directory.root().to_path_buf();
            path.extend(scope.split('/'));
            path
        };
        let mut files = Vec::new();
        if full.is_file() {
            files.push(full);
        } else if full.is_dir() {
            // Папку могут удалить посреди обхода (git checkout) — тогда берём, что успели: её события придут следом.
            walk(&full, &mut files);
        } else {
            continue;
        }
        for file in files {
            let Some(relative) = relative_to(directory.root(), &file) else {
                continue;
            };
            let Some(layout) = layout::classify(&relative) else {
                continue;
            };
            if let Ok(metadata) = std::fs::metadata(&file)
                && metadata.is_file()
            {
                result.insert(
                    relative,
                    DiskFile {
                        full_path: file,
                        layout,
                        stamp: stamp_of(&metadata),
                    },
                );
            }
        }
    }
    result
}

// Все *.yaml в папке и её подпапках; недоступные папки пропускаются (IgnoreInaccessible).
fn walk(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if file_type.is_dir() {
            walk(&path, out);
        } else if path.extension().is_some_and(|e| e == &names::EXTENSION[1..]) {
            out.push(path);
        }
    }
}

fn relative_to(root: &Path, file: &Path) -> Option<String> {
    let rest = file.strip_prefix(root).ok()?;
    Some(
        rest.components()
            .map(|c| c.as_os_str().to_string_lossy().into_owned())
            .collect::<Vec<_>>()
            .join("/"),
    )
}

/// Строка `files` и строки таблиц задачи.
struct Row<'a> {
    path: &'a str,
    stamp: Stamp,
    layout: &'a LayoutEntry,
    columns: Columns,
    data: Option<String>,
    error: Option<String>,
    entity_id: Option<Uuid>,
}

impl Row<'_> {
    /// Значение колонки id: id сущности; у нечитаемого файла — из имени (полный Guid) или само имя.
    fn id_key(&self) -> String {
        match (self.entity_id, self.layout.id) {
            (Some(id), _) | (None, Some(id)) => guid_d(&id),
            (None, None) => self.layout.stem.clone(),
        }
    }
}

#[derive(Default)]
struct Columns {
    sort_text: Option<String>,
    sort_num: Option<i64>,
    sort_updated: Option<i64>,
    type_id: Option<Uuid>,
    status_id: Option<Uuid>,
    user_kind: Option<UserKind>,
    numbers: Vec<TaskSeriesNumber>,
    links: Vec<TaskLink>,
    fields: Vec<TaskField>,
}

/// Ключ имени пользователя в индексе: уникальность и сортировка — без учёта регистра и пробелов по краям (`UserStorage.Normalize`).
pub fn normalize_username(username: &str) -> String {
    to_lower_invariant(username.trim())
}

/// Тип поля числом (`(int)FieldType` в .NET): порядок перечисления.
pub(super) fn field_type_code(field_type: FieldType) -> i64 {
    match field_type {
        FieldType::String => 0,
        FieldType::Int => 1,
        FieldType::Float => 2,
        FieldType::Bool => 3,
        FieldType::Date => 4,
        FieldType::Enum => 5,
    }
}

pub(super) fn field_type_of_code(code: i64) -> Option<FieldType> {
    [
        FieldType::String,
        FieldType::Int,
        FieldType::Float,
        FieldType::Bool,
        FieldType::Date,
        FieldType::Enum,
    ]
    .into_iter()
    .find(|t| field_type_code(*t) == code)
}

/// None — файла уже нет. Ошибки разбора — в `error` с текстом .NET: маркер конфликта git, неподдерживаемый формат, битый
/// YAML, пустой файл (`File is empty`), id внутри не тот, что в имени.
fn read<'a>(path: &'a str, file: &'a DiskFile) -> Option<Row<'a>> {
    let layout = &file.layout;
    let problem = |error: String| Row {
        path,
        stamp: file.stamp,
        layout,
        columns: Columns::default(),
        data: None,
        error: Some(error),
        entity_id: None,
    };
    let bytes = match read_bytes(&file.full_path) {
        Ok(Some(bytes)) => bytes,
        Ok(None) => return None,
        Err(e) => return Some(problem(e.to_string())),
    };
    let parsed = parse(layout, &bytes, &file.full_path);
    let (id, columns, data) = match parsed {
        Ok(x) => x,
        Err(Error::Yaml(message)) if message.ends_with(": the file is empty") => return Some(problem("File is empty".into())),
        Err(e) => return Some(problem(e.message())),
    };
    if let Some(named) = layout.id
        && id != named
    {
        return Some(problem(format!("Id in the file ({}) does not match the file name", guid_d(&id))));
    }
    // Имя по заголовку: из id в нём только начало.
    if let Some(prefix) = &layout.id_prefix
        && names::id_prefix(&id) != *prefix
    {
        return Some(problem(format!(
            "Id in the file ({}) does not match the end of the file name ({prefix})",
            guid_d(&id)
        )));
    }
    Some(Row {
        path,
        stamp: file.stamp,
        layout,
        columns,
        data: Some(data),
        error: None,
        entity_id: Some(id),
    })
}

fn json<T: serde::Serialize>(model: &T) -> String {
    serde_json::to_string(model).expect("entity models serialize")
}

fn named(name: &str) -> Columns {
    Columns {
        sort_text: Some(name.to_string()),
        ..Columns::default()
    }
}

type Parsed = (Uuid, Columns, String);

fn parse(layout: &LayoutEntry, bytes: &[u8], path: &Path) -> crate::error::Result<Parsed> {
    let project = layout.project_id.unwrap_or_default();
    Ok(match layout.kind {
        EntityKind::Project => {
            let x = files::project::parse(bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        EntityKind::User => {
            let x = files::user::parse(bytes, path)?.model;
            let columns = Columns {
                sort_text: Some(normalize_username(&x.username)),
                user_kind: Some(x.kind),
                ..Columns::default()
            };
            (x.id, columns, json(&x))
        }
        EntityKind::Status => {
            let x = files::status::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        EntityKind::StatusSet => {
            let x = files::status_set::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        EntityKind::TaskType => {
            let x = files::task_type::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        EntityKind::Board => {
            let x = files::board::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        // Префикс — ключ сортировки; сравнение в SQLite двоичное, поэтому TSK и tsk — разные, а порядок один во всех клонах.
        EntityKind::Series => {
            let x = files::series::parse(project, bytes, path)?.model;
            (x.id, named(&x.prefix), json(&x))
        }
        EntityKind::Task => {
            let x = files::task::parse(project, bytes, path)?.model;
            let columns = Columns {
                sort_text: Some(to_lower_invariant(&x.title)),
                sort_num: Some(x.created_at.unix_ticks()),
                sort_updated: Some(x.updated_at.unix_ticks()),
                type_id: Some(x.type_id),
                status_id: Some(x.status_id),
                user_kind: None,
                numbers: x.series_numbers.clone(),
                links: x.links.clone(),
                fields: x.fields.clone(),
            };
            (x.id, columns, json(&x))
        }
        EntityKind::LinkType => {
            let x = files::link_type::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        EntityKind::Field => {
            let x = files::field::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
        EntityKind::FieldEnum => {
            let x = files::field_enum::parse(project, bytes, path)?.model;
            (x.id, named(&x.name), json(&x))
        }
    })
}

fn user_kind_name(kind: UserKind) -> &'static str {
    match kind {
        UserKind::Human => "Human",
        UserKind::Agent => "Agent",
    }
}

fn upsert(transaction: &Transaction<'_>, row: &Row<'_>) -> io::Result<()> {
    let id_key = row.id_key();
    let project = row.layout.project_id.map(|p| guid_d(&p));
    transaction
        .execute(
            "INSERT OR REPLACE INTO files
                (path, size, mtime, kind, project_id, id, sort_text, sort_num, sort_updated, type_id, status_id, user_kind, data, error)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)",
            params![
                row.path,
                row.stamp.size,
                row.stamp.mtime,
                kind_name(row.layout.kind),
                project,
                id_key,
                row.columns.sort_text,
                row.columns.sort_num,
                row.columns.sort_updated,
                row.columns.type_id.map(|x| guid_d(&x)),
                row.columns.status_id.map(|x| guid_d(&x)),
                row.columns.user_kind.map(user_kind_name),
                row.data,
                row.error,
            ],
        )
        .map_err(db_error)?;
    delete_task_rows(transaction, row.path)?;
    let Some(project) = project else {
        return Ok(());
    };

    let mut distinct_links: Vec<&TaskLink> = Vec::new();
    for link in &row.columns.links {
        if !distinct_links.contains(&link) {
            distinct_links.push(link);
        }
    }
    for link in distinct_links {
        transaction
            .execute(
                "INSERT INTO task_links (path, project_id, type_id, source_id, target_id) VALUES (?1, ?2, ?3, ?4, ?5)",
                params![row.path, project, guid_d(&link.type_id), id_key, guid_d(&link.target_id)],
            )
            .map_err(db_error)?;
    }

    let mut seen_fields: Vec<Uuid> = Vec::new();
    for field in &row.columns.fields {
        if seen_fields.contains(&field.field_id) {
            continue;
        }
        seen_fields.push(field.field_id);
        let own_name = field.own.as_ref().map(|o| field_name_key(&o.name));
        let own_type = field.own.as_ref().map(|o| field_type_code(o.field_type));
        let enum_id = field.own.as_ref().and_then(|o| o.enum_id).map(|e| guid_d(&e));
        transaction
            .execute(
                "INSERT INTO task_fields (path, project_id, field_id, enum_id, own_name, own_type) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                params![row.path, project, guid_d(&field.field_id), enum_id, own_name, own_type],
            )
            .map_err(db_error)?;
        let mut seen_values: Vec<&String> = Vec::new();
        for value in &field.values {
            if seen_values.contains(&value) {
                continue;
            }
            seen_values.push(value);
            transaction
                .execute(
                    "INSERT INTO task_field_values (path, project_id, field_id, value, number, own_name, own_type)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                    params![
                        row.path,
                        project,
                        guid_d(&field.field_id),
                        value,
                        field_number(value),
                        own_name,
                        own_type
                    ],
                )
                .map_err(db_error)?;
        }
    }

    for number in &row.columns.numbers {
        transaction
            .execute(
                "INSERT INTO task_series (path, project_id, series_id, number, task_id, created_ticks) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                params![
                    row.path,
                    project,
                    guid_d(&number.series_id),
                    number.number,
                    id_key,
                    row.columns.sort_num.unwrap_or(0)
                ],
            )
            .map_err(db_error)?;
    }
    Ok(())
}

fn delete_task_rows(transaction: &Transaction<'_>, path: &str) -> io::Result<()> {
    for table in ["task_series", "task_links", "task_fields", "task_field_values"] {
        transaction
            .execute(&format!("DELETE FROM {table} WHERE path = ?1"), params![path])
            .map_err(db_error)?;
    }
    Ok(())
}

fn delete(transaction: &Transaction<'_>, path: &str) -> io::Result<()> {
    transaction
        .execute("DELETE FROM files WHERE path = ?1", params![path])
        .map_err(db_error)?;
    delete_task_rows(transaction, path)
}
