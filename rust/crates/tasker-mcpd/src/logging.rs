//! Журнал демона — как Serilog в .NET (`McpDaemon.ConfigureLogging`): файл `<data>/logs/mcp-YYYYMMDD.log` (день — файл, хранятся
//! 7 последних), строки `2026-10-09 13:58:27.056 +02:00 [INF] сообщение` с уровнями INF/WRN/ERR/FTL в местном времени; не в фоне —
//! ещё и консоль `[13:58:27 INF] сообщение`. Формат файла важен: консоль при неудачном старте читает хвост журнала и показывает
//! последнюю строку с `[ERR]`/`[FTL]` после «Starting the MCP server» (`DaemonController.LogTail`).
//!
//! `tracing-appender` не используется: он именует файлы `prefix.YYYY-MM-DD.suffix`, а нужен `mcp-YYYYMMDD.log`, — ролик по дате
//! здесь свой ([`DailyFile`]). Файл открыт на дозапись без исключительной блокировки (`shared: true`): в журнал могут писать
//! несколько процессов (при `upgrade` — два). Уровень FTL — событие `error!` с полем `fatal = true`.
use chrono::Local;
use std::fmt::{self, Write as _};
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use tracing::field::{Field, Visit};
use tracing::{Event, Level, Subscriber};
use tracing_subscriber::filter::{LevelFilter, Targets};
use tracing_subscriber::fmt::format::Writer;
use tracing_subscriber::fmt::writer::BoxMakeWriter;
use tracing_subscriber::fmt::{FmtContext, FormatEvent, FormatFields, MakeWriter};
use tracing_subscriber::layer::SubscriberExt as _;
use tracing_subscriber::registry::LookupSpan;
use tracing_subscriber::util::SubscriberInitExt as _;
use tracing_subscriber::{Layer, filter::Filtered, fmt as tracing_fmt};

/// Сколько файлов журнала хранится (`retainedFileCountLimit`).
pub const RETAINED_FILES: usize = 7;

const PREFIX: &str = "mcp-";
const SUFFIX: &str = ".log";

/// Настраивает журнал: файл всегда, консоль — если не в фоне (`detached`): stdout или stderr (`to_stderr`: у рабочего процесса в
/// stdout идёт обмен с супервизором). Повторный вызов (тесты) — ошибка игнорируется.
pub fn configure(logs_dir: &Path, detached: bool, to_stderr: bool) {
    let file = DailyFile::new(logs_dir.to_path_buf());
    let filter = || {
        Targets::new()
            .with_default(LevelFilter::WARN)
            .with_target("tasker_mcpd", LevelFilter::INFO)
            .with_target("tasker_core", LevelFilter::INFO)
            .with_target("tasker_files", LevelFilter::INFO)
            .with_target("tasker_services", LevelFilter::INFO)
    };
    let file_layer = tracing_fmt::layer()
        .event_format(SerilogFormat { console: false })
        .with_writer(file)
        .with_filter(filter());
    let console_layer: Option<Filtered<_, Targets, _>> = (!detached).then(|| {
        tracing_fmt::layer()
            .event_format(SerilogFormat { console: true })
            .with_writer(if to_stderr {
                BoxMakeWriter::new(io::stderr)
            } else {
                BoxMakeWriter::new(io::stdout)
            })
            .with_filter(filter())
    });
    let _ = tracing_subscriber::registry().with(file_layer).with(console_layer).try_init();
}

/// Текст уровня Serilog (`{Level:u3}`); `fatal` — событие `error!(fatal = true, …)`.
fn level_text(level: &Level, fatal: bool) -> &'static str {
    match *level {
        Level::ERROR if fatal => "FTL",
        Level::ERROR => "ERR",
        Level::WARN => "WRN",
        Level::INFO => "INF",
        Level::DEBUG => "DBG",
        Level::TRACE => "VRB",
    }
}

/// `{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}` (файл) или `[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}`
/// (консоль). Поля события, кроме `message` и `fatal`, дописываются как ` ключ=значение`.
struct SerilogFormat {
    console: bool,
}

#[derive(Default)]
struct MessageVisitor {
    message: String,
    fatal: bool,
    extra: String,
}

impl Visit for MessageVisitor {
    fn record_debug(&mut self, field: &Field, value: &dyn fmt::Debug) {
        if field.name() == "message" {
            self.message = format!("{value:?}");
        } else {
            let _ = write!(self.extra, " {}={:?}", field.name(), value);
        }
    }

    fn record_str(&mut self, field: &Field, value: &str) {
        if field.name() == "message" {
            self.message = value.to_string();
        } else {
            let _ = write!(self.extra, " {}={}", field.name(), value);
        }
    }

    fn record_bool(&mut self, field: &Field, value: bool) {
        if field.name() == "fatal" {
            self.fatal = value;
        } else {
            let _ = write!(self.extra, " {}={}", field.name(), value);
        }
    }
}

impl<S, N> FormatEvent<S, N> for SerilogFormat
where
    S: Subscriber + for<'a> LookupSpan<'a>,
    N: for<'a> FormatFields<'a> + 'static,
{
    fn format_event(&self, _ctx: &FmtContext<'_, S, N>, mut writer: Writer<'_>, event: &Event<'_>) -> fmt::Result {
        let mut visitor = MessageVisitor::default();
        event.record(&mut visitor);
        let level = level_text(event.metadata().level(), visitor.fatal);
        let now = Local::now();
        if self.console {
            write!(writer, "[{} {level}] ", now.format("%H:%M:%S"))?;
        } else {
            write!(writer, "{} [{level}] ", now.format("%Y-%m-%d %H:%M:%S%.3f %:z"))?;
        }
        writeln!(writer, "{}{}", visitor.message, visitor.extra)
    }
}

/// Файл журнала текущего дня: `mcp-YYYYMMDD.log`, дозапись; при смене даты открывается новый, старые сверх [`RETAINED_FILES`]
/// удаляются (по имени, как Serilog).
#[derive(Clone)]
pub struct DailyFile {
    inner: Arc<Mutex<DailyState>>,
}

struct DailyState {
    directory: PathBuf,
    date: String,
    file: Option<File>,
}

impl DailyFile {
    pub fn new(directory: PathBuf) -> DailyFile {
        DailyFile {
            inner: Arc::new(Mutex::new(DailyState {
                directory,
                date: String::new(),
                file: None,
            })),
        }
    }

    /// Имя файла за день `yyyyMMdd`.
    pub fn file_name(date: &str) -> String {
        format!("{PREFIX}{date}{SUFFIX}")
    }

    fn write_line(&self, bytes: &[u8]) -> io::Result<()> {
        let mut state = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        let today = Local::now().format("%Y%m%d").to_string();
        if state.file.is_none() || state.date != today {
            std::fs::create_dir_all(&state.directory)?;
            let path = state.directory.join(Self::file_name(&today));
            state.file = Some(OpenOptions::new().append(true).create(true).open(path)?);
            state.date = today;
            retain(&state.directory, RETAINED_FILES);
        }
        match state.file.as_mut() {
            Some(file) => file.write_all(bytes),
            None => Ok(()),
        }
    }
}

/// Оставляет `keep` самых новых по имени файлов `mcp-*.log`.
pub fn retain(directory: &Path, keep: usize) {
    let Ok(entries) = std::fs::read_dir(directory) else { return };
    let mut names: Vec<String> = entries
        .flatten()
        .filter_map(|e| e.file_name().into_string().ok())
        .filter(|n| n.starts_with(PREFIX) && n.ends_with(SUFFIX))
        .collect();
    names.sort_unstable();
    let extra = names.len().saturating_sub(keep);
    for name in names.into_iter().take(extra) {
        let _ = std::fs::remove_file(directory.join(name));
    }
}

/// Запись — через буфер: строка собирается целиком и дописывается одним вызовом, чтобы процессы не перемешивали половинки строк.
pub struct LineWriter {
    file: DailyFile,
    buffer: Vec<u8>,
}

impl Write for LineWriter {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        self.buffer.extend_from_slice(buf);
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        if !self.buffer.is_empty() {
            let bytes = std::mem::take(&mut self.buffer);
            self.file.write_line(&bytes)?;
        }
        Ok(())
    }
}

impl Drop for LineWriter {
    fn drop(&mut self) {
        let _ = self.flush();
    }
}

impl<'a> MakeWriter<'a> for DailyFile {
    type Writer = LineWriter;

    fn make_writer(&'a self) -> LineWriter {
        LineWriter {
            file: self.clone(),
            buffer: Vec::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retain_keeps_the_newest_files_by_name() {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(&dir).unwrap();
        for day in 1..=9 {
            std::fs::write(dir.join(format!("mcp-2026100{day}.log")), "x").unwrap();
        }
        std::fs::write(dir.join("tasker-20261001.log"), "x").unwrap();
        retain(&dir, 7);
        let mut names: Vec<String> = std::fs::read_dir(&dir)
            .unwrap()
            .flatten()
            .map(|e| e.file_name().into_string().unwrap())
            .collect();
        names.sort();
        assert_eq!(
            names,
            [
                "mcp-20261003.log",
                "mcp-20261004.log",
                "mcp-20261005.log",
                "mcp-20261006.log",
                "mcp-20261007.log",
                "mcp-20261008.log",
                "mcp-20261009.log",
                "tasker-20261001.log"
            ]
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn daily_file_writes_whole_lines_to_todays_file() {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        let file = DailyFile::new(dir.clone());
        {
            let mut writer = file.make_writer();
            writer.write_all(b"2026-10-09 13:58:27.056 +02:00 [INF] ").unwrap();
            writer.write_all(b"Starting the MCP server\n").unwrap();
        }
        let today = Local::now().format("%Y%m%d").to_string();
        let text = std::fs::read_to_string(dir.join(DailyFile::file_name(&today))).unwrap();
        assert_eq!(text, "2026-10-09 13:58:27.056 +02:00 [INF] Starting the MCP server\n");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn levels_are_serilog_three_letter_codes() {
        assert_eq!(level_text(&Level::INFO, false), "INF");
        assert_eq!(level_text(&Level::WARN, false), "WRN");
        assert_eq!(level_text(&Level::ERROR, false), "ERR");
        assert_eq!(level_text(&Level::ERROR, true), "FTL");
    }
}
