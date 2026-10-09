//! Файлы `.tasker` на Rust (TSK-129, фаза 1 плана `docs/rust-migration-plan.md`): разбор YAML через saphyr в модели
//! `tasker-core`, собственный эмиттер, повторяющий YamlDotNet байт в байт, версия формата `formatVersion` с апгрейдом в памяти.
//!
//! Крейт работает только с байтами: [`parse`](files::project::parse) принимает содержимое файла и путь (для текстов ошибок),
//! [`serialize`](files::project::serialize) возвращает байты для записи (LF, UTF-8 без BOM). Запись на диск, блокировки и
//! имена файлов — в следующей задаче (TSK-130).
//!
//! Правило переноса: поведение `Tasker.Storage.Files` (.NET) воспроизводится буквально — ключи, их порядок, правила пропуска
//! (null, false, пустые списки, пустое описание), стиль скаляров YamlDotNet, тексты ошибок. Эталоны — `rust/tests/golden`.

pub mod error;
pub mod files;
pub mod format;
pub mod yaml;

pub use error::{Error, Result};
pub use files::Versioned;
