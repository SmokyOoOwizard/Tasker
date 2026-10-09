//! Консоль `tasker` на Rust (фаза 3 плана `docs/rust-migration-plan.md`, TSK-134): каркас — дерево команд ([`spec`]) и разбор
//! clap, справка и ошибки разбора в формате System.CommandLine ([`help`]), подсказки значений из области ([`hints`]), область
//! ([`session`]), вывод ([`context`], [`table`]), терминал и ширина ([`terminal`]), тексты ошибок и коды выхода ([`errors`]),
//! выполнение строки ([`app`]). Синхронно, без tokio. Поведение .NET-консоли (`Tasker.Cli`) воспроизводится буквально:
//! эталоны в `rust/tests/golden/expected/cli`.
//!
//! Реализованы пока `migrate` (TSK-130) и каркас; остальные команды дерева отвечают `Error: not implemented in this build`
//! (их заполняет TSK-135).
pub mod app;
pub mod context;
pub mod errors;
pub mod help;
pub mod hints;
pub mod migrate;
pub mod session;
pub mod spec;
pub mod table;
pub mod terminal;
