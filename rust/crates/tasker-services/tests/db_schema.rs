//! Схема файла SQLite, который создаёт Rust, — та же, что у `EnsureCreated` .NET `Tasker.Storage.Db` (TSK-159): эталон — `.schema`
//! новой базы (`tests/golden/sqlite/schema.sql`). `TASKER_GOLDEN_UPDATE=1` переписывает эталон схемой новой базы, а сравнение идёт
//! с записанным текстом.
mod common;

use common::temp_dir;
use rusqlite::Connection;
use tasker_services::Workspace;

const GOLDEN: &str = include_str!("../../../../tests/golden/sqlite/schema.sql");

fn golden_update() -> bool {
    std::env::var("TASKER_GOLDEN_UPDATE").is_ok_and(|v| !v.is_empty() && v != "0")
}

fn expected(actual: &str) -> String {
    if golden_update() {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../tests/golden/sqlite/schema.sql");
        std::fs::write(path, actual).unwrap();
        return actual.to_string();
    }
    GOLDEN.to_string()
}

/// Текст `.schema` оболочки sqlite3: строки `sqlite_master` по порядку создания, у таблиц — `CREATE TABLE IF NOT EXISTS`.
fn dot_schema(file: &std::path::Path) -> String {
    let connection = Connection::open(file).unwrap();
    let mut statement = connection
        .prepare("SELECT type, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid")
        .unwrap();
    let rows = statement
        .query_map([], |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)))
        .unwrap();
    let mut text = String::new();
    for row in rows {
        let (kind, sql) = row.unwrap();
        let sql = match sql.strip_prefix("CREATE TABLE ") {
            Some(rest) if kind == "table" => format!("CREATE TABLE IF NOT EXISTS {rest}"),
            _ => sql,
        };
        text.push_str(&sql);
        text.push_str(";\n");
    }
    text
}

#[test]
fn a_new_file_gets_the_ef_core_schema_in_wal_mode() {
    let dir = temp_dir();
    let file = dir.join("new.db");
    Workspace::open_sqlite(&file).unwrap();
    assert_eq!(dot_schema(&file), expected(&dot_schema(&file)));
    let connection = Connection::open(&file).unwrap();
    let mode: String = connection.query_row("PRAGMA journal_mode", [], |row| row.get(0)).unwrap();
    assert_eq!(mode, "wal");
    // Повторное открытие ничего не меняет и не создаёт своих таблиц.
    drop(connection);
    Workspace::open_sqlite(&file).unwrap();
    assert_eq!(dot_schema(&file), expected(&dot_schema(&file)));
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn an_empty_existing_file_gets_the_schema_and_keeps_its_journal() {
    let dir = temp_dir();
    let file = dir.join("empty.db");
    std::fs::write(&file, b"").unwrap();
    Workspace::open_sqlite(&file).unwrap();
    assert_eq!(dot_schema(&file), expected(&dot_schema(&file)));
    let mode: String = Connection::open(&file)
        .unwrap()
        .query_row("PRAGMA journal_mode", [], |row| row.get(0))
        .unwrap();
    assert_eq!(mode, "delete");
    std::fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn an_old_file_gets_the_missing_link_type_columns_like_db_initializer() {
    let dir = temp_dir();
    let file = dir.join("old.db");
    let project = uuid::Uuid::parse_str("11111111-1111-4111-8111-111111111111").unwrap();
    let blocks = tasker_core::ids::default_link_type_id(&project, "blocks");
    let relates = tasker_core::ids::default_link_type_id(&project, "relates");
    {
        let connection = Connection::open(&file).unwrap();
        // Схема до признаков типов связей: без AllowCycles и Hierarchical.
        let old = tasker_services::db::SCHEMA
            .replace("    \"AllowCycles\" INTEGER NOT NULL,\n", "")
            .replace("    \"Hierarchical\" INTEGER NOT NULL,\n", "");
        connection.execute_batch(&old).unwrap();
        connection
            .execute(
                "INSERT INTO projects (Id, Name, CreatedAt, Version) VALUES (?1, 'P', '2026-01-01 00:00:00', 1)",
                [project.to_string().to_uppercase()],
            )
            .unwrap();
        for (id, name) in [(blocks, "Blocks"), (relates, "Relates")] {
            connection
                .execute(
                    "INSERT INTO link_types (Id, ProjectId, Name, OutwardName, InwardName, Version) VALUES (?1, ?2, ?3, 'o', 'i', 1)",
                    [id.to_string().to_uppercase(), project.to_string().to_uppercase(), name.to_string()],
                )
                .unwrap();
        }
    }
    let ws = Workspace::open_sqlite(&file).unwrap();
    let types: Vec<tasker_core::model::LinkType> = {
        use tasker_services::Storage;
        ws.get_all(&project).unwrap()
    };
    let flags: Vec<(String, bool, bool, String)> = types
        .iter()
        .map(|t| (t.name.clone(), t.allow_cycles, t.hierarchical, t.version.clone()))
        .collect();
    assert_eq!(
        flags,
        vec![
            ("Blocks".to_string(), false, false, "1".to_string()),
            ("Relates".to_string(), true, false, "1".to_string())
        ]
    );
    // Соединение закрывается до удаления папки: Windows не удаляет открытый файл базы.
    drop(types);
    drop(ws);
    std::fs::remove_dir_all(&dir).unwrap();
}
