//! Домен Tasker на Rust (TSK-128): сущности, валидация, короткие id и ссылки
//! на задачи, версии сущностей, конвенции JSON (`TaskerJson`), канонизация значений полей, глобальные настройки,
//! диагностика переменных `TASKER_*`, атомарная запись и блокировки файлов, блокировки на время правки (`locks`).
//!
//! Поведение совпадает с `dotnet/src/Tasker.Core` буквально (тексты ошибок, форматы, крайние случаи), поскольку над одной
//! папкой `.tasker` работают и Rust-программы, и .NET-десктоп с сервером. Эталоны — `tests/golden`.

pub mod config;
pub mod error;
pub mod fields;
pub mod ids;
pub mod io;
pub mod json;
pub mod locks;
pub mod model;
pub mod perf;
pub mod reference;
pub mod settings;
pub mod tasks;
pub mod time;
pub mod validate;
pub mod versioning;

pub use error::{ConflictCode, TaskerError};
pub use ids::ShortId;
pub use reference::TaskReference;
pub use time::Timestamp;

#[cfg(test)]
pub(crate) mod test_support {
    use std::path::PathBuf;

    /// Пустой каталог для теста в `target/tmp`: системный временный каталог macOS держит десятки тысяч записей, и `CanonicalPath`
    /// (который, как и .NET, перечисляет каталог на каждом компоненте пути) читал бы их все при каждом вызове.
    pub fn temp_dir() -> PathBuf {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }
}
