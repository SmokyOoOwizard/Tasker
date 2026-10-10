//! Сценарии `cleanup_links.rs` на области в файле SQLite (TSK-159): тестовый файл с префиксом `sqlite_` открывает `Env` в SQLite
//! (`common::sqlite`), как .NET-тесты, параметризованные хранилищем (`TestWorkspace.Storages()`).
#[path = "cleanup_links.rs"]
mod scenarios;
