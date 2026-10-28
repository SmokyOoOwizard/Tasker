//! Консоль `tasker` на Rust (TSK-134): каркас — дерево команд ([`spec`]) и разбор
//! clap, справка и ошибки разбора в формате System.CommandLine ([`help`]), подсказки значений из области ([`hints`]), область
//! ([`session`]), вывод ([`context`], [`table`]), терминал и ширина ([`terminal`]), тексты ошибок и коды выхода ([`errors`]),
//! выполнение строки ([`app`]). Синхронно, без tokio. Вывод, тексты ошибок и коды выхода
//! закреплены эталонами в `tests/golden/expected/cli`.
//!
//! Команды дерева — по модулю на группу (TSK-135).
pub mod app;
pub mod cleanup;
pub mod commands;
pub mod context;
pub mod daemon;
pub mod entities;
pub mod errors;
pub mod help;
pub mod hints;
pub mod json;
pub mod kit;
pub mod migrate;
pub use tasker_core::perf;
pub mod session;
pub mod spec;
pub mod suggest;
pub mod sync;
pub mod table;
pub mod terminal;
