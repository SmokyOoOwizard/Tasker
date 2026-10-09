//! Файлы `.tasker` на Rust (TSK-129, фаза 1 плана `docs/rust-migration-plan.md`): разбор YAML через saphyr в модели
//! `tasker-core`, собственный эмиттер, повторяющий YamlDotNet байт в байт, версия формата `formatVersion` с апгрейдом в памяти.
//!
//! Модели файлов работают с байтами: [`parse`](files::project::parse) принимает содержимое файла и путь (для текстов ошибок),
//! [`serialize`](files::project::serialize) возвращает байты для записи (LF, UTF-8 без BOM). Поверх них (TSK-130): имена файлов
//! `<slug>-<id8>.yaml` ([`names`]), раскладка `.tasker` ([`layout`]), запись под блокировками ([`write`]), блокировки правки
//! ([`edit_locks`]) и `tasker migrate` ([`migration`]); индекс области в SQLite `.cache/index-rs.db` ([`index`], TSK-131).
//!
//! Правило переноса: поведение `Tasker.Storage.Files` (.NET) воспроизводится буквально — ключи, их порядок, правила пропуска
//! (null, false, пустые списки, пустое описание), стиль скаляров YamlDotNet, тексты ошибок. Эталоны — `rust/tests/golden`.

pub mod edit_locks;
pub mod error;
pub mod files;
pub mod format;
pub mod index;
pub mod layout;
pub mod migration;
pub mod names;
pub mod write;
pub mod yaml;

pub use error::{Error, Result};
pub use files::Versioned;

#[cfg(test)]
pub(crate) mod test_support {
    use std::path::PathBuf;

    /// Пустой каталог для теста в `target/tmp` (как в `tasker-core`): системный временный каталог не трогаем.
    pub fn temp_dir() -> PathBuf {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }
}
