//! Сценарии `series.rs` на области в файле SQLite (TSK-159): тестовый файл с префиксом `sqlite_` открывает `Env` в SQLite
//! (`common::sqlite`), как .NET-тесты, параметризованные хранилищем (`TestWorkspace.Storages()`).
#[path = "series.rs"]
mod scenarios;
