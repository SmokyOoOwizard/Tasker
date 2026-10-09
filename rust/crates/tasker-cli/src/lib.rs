//! Консоль `tasker` на Rust (фаза 3 плана `docs/rust-migration-plan.md`, TSK-134): каркас — дерево команд ([`spec`]) и разбор
//! clap, справка и ошибки разбора в формате System.CommandLine ([`help`]), подсказки значений из области ([`hints`]), область
//! ([`session`]), вывод ([`context`], [`table`]), терминал и ширина ([`terminal`]), тексты ошибок и коды выхода ([`errors`]),
//! выполнение строки ([`app`]). Синхронно, без tokio. Поведение .NET-консоли (`Tasker.Cli`) воспроизводится буквально:
//! эталоны в `rust/tests/golden/expected/cli`.
//!
//! Команды дерева — по модулю на группу (TSK-135): поведение .NET-консоли (`Tasker.Cli/Commands/*.cs`) воспроизводится буквально.
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
pub mod perf;
pub mod session;
pub mod spec;
pub mod suggest;
pub mod sync;
pub mod table;
pub mod terminal;
