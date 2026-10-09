//! Терминал (`Terminal` и `ConsoleSetup` в .NET): идёт ли вывод в терминал, сколько в нём знаков в строке, предел ширины строк
//! списков из `--truncate`/`--no-truncate`/`--width`/`TASKER_WIDTH`/`COLUMNS`+`LINES` (под `watch`); на Windows — консоль в UTF-8.
use crate::errors::{CliError, Result};

/// Состояние терминала на момент вызова: идёт ли вывод в терминал и сколько в нём знаков в строке (0 — неизвестно).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Terminal {
    pub is_output: bool,
    pub columns: usize,
    /// «Под watch»: stdout не терминал, но экспортированы обе `COLUMNS` и `LINES` — вывод экранный.
    pub is_screen: bool,
}

impl Terminal {
    /// Нижняя граница ширины: уже не сжимаем, чтобы не получить пустой вывод.
    pub const MIN_WIDTH: usize = 20;

    pub fn new(is_output: bool, columns: usize) -> Terminal {
        Terminal {
            is_output,
            columns,
            is_screen: false,
        }
    }

    /// Терминал сейчас: размер окна читается при каждом вызове (окно могли изменить). Размер: окно, иначе `COLUMNS`; не удалось — 0.
    pub fn current() -> Terminal {
        let is_output = stdout_is_terminal();
        let window = if is_output { window_width() } else { 0 };
        Self::detect(
            is_output,
            window,
            std::env::var("COLUMNS").ok().as_deref(),
            std::env::var("LINES").ok().as_deref(),
            cfg!(windows),
        )
    }

    /// То же по заданным значениям (для тестов): ширина окна (0 — неизвестна) и переменные окружения.
    pub fn detect(is_output: bool, window_width: usize, columns: Option<&str>, lines: Option<&str>, windows: bool) -> Terminal {
        let mut width: i64 = if is_output {
            Self::usable_columns(window_width, windows) as i64
        } else {
            0
        };
        if width <= 0
            && let Some(from_environment) = columns.and_then(parse_int)
        {
            width = from_environment;
        }
        let is_screen = !is_output && width > 0 && lines.and_then(parse_int).is_some_and(|n| n > 0);
        Terminal {
            is_output,
            columns: width.max(0) as usize,
            is_screen,
        }
    }

    /// Сколько знаков строки можно занять: в классической консоли Windows строка на всю ширину уходит на следующую строку сама,
    /// поэтому там последний столбец остаётся свободным.
    pub fn usable_columns(window_width: usize, windows: bool) -> usize {
        if windows && window_width > 1 {
            window_width - 1
        } else {
            window_width
        }
    }

    /// Предельная ширина строк текстового вывода по параметрам и `TASKER_WIDTH`; None — не обрезать (см. [`Terminal::limit_with`]).
    pub fn limit(&self, truncate: bool, no_truncate: bool, width: Option<&str>) -> Result<Option<usize>> {
        self.limit_with(truncate, no_truncate, width, std::env::var("TASKER_WIDTH").ok().as_deref())
    }

    /// Явное требование — `--width N` / `TASKER_WIDTH` с числом (задаёт ширину сам) или `--truncate` / `auto` / `0` (ширина
    /// терминала): обрезают и при перенаправлении, если ширина известна. `--no-truncate` отключает всё; `TASKER_WIDTH=off` / `0`
    /// (и `--width off`) — тоже не обрезать. Ничего не задано — обрезаем в терминале с известной шириной и «под watch».
    pub fn limit_with(&self, truncate: bool, no_truncate: bool, width: Option<&str>, tasker_width: Option<&str>) -> Result<Option<usize>> {
        if no_truncate {
            if truncate {
                return Err(CliError::new("Use either --truncate or --no-truncate, not both"));
            }
            return Ok(None);
        }

        let from_variable = width.is_none();
        let requested = width.or(tasker_width);
        let mut auto = truncate;
        if let Some(requested) = requested.filter(|r| !r.trim().is_empty()) {
            let text = requested.trim();
            if text.eq_ignore_ascii_case("off") || (from_variable && text == "0") {
                if truncate {
                    return Err(CliError::new("Use either --truncate or --no-truncate, not both"));
                }
                return Ok(None);
            }
            if text.eq_ignore_ascii_case("auto") {
                auto = true;
            } else {
                match parse_int(text) {
                    Some(number) if number >= 0 => {
                        if number == 0 {
                            auto = true;
                        } else {
                            return Ok(Some((number as usize).max(Self::MIN_WIDTH)));
                        }
                    }
                    _ => {
                        return Err(CliError::new(format!(
                            "Width must be a non-negative number or 'auto', got '{requested}'"
                        )));
                    }
                }
            }
        }

        if !auto && !self.is_output && !self.is_screen {
            return Ok(None); // не терминал и ничего не просили: файл и конвейер не обрезаем
        }
        Ok(if self.columns > 0 {
            Some(self.columns.max(Self::MIN_WIDTH))
        } else {
            None
        })
    }
}

/// `int.TryParse`: пробелы по краям, знак, десятичные цифры.
pub fn parse_int(text: &str) -> Option<i64> {
    let text = text.trim();
    let digits = text.strip_prefix(['+', '-']).unwrap_or(text);
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    text.parse().ok()
}

#[cfg(not(windows))]
pub fn stdout_is_terminal() -> bool {
    rustix::termios::isatty(rustix::stdio::stdout())
}

#[cfg(not(windows))]
pub fn stdin_is_terminal() -> bool {
    rustix::termios::isatty(rustix::stdio::stdin())
}

/// Ширина окна терминала в знаках; 0 — неизвестна.
#[cfg(not(windows))]
pub fn window_width() -> usize {
    rustix::termios::tcgetwinsize(rustix::stdio::stdout())
        .map(|size| usize::from(size.ws_col))
        .unwrap_or(0)
}

#[cfg(windows)]
pub fn stdout_is_terminal() -> bool {
    windows::is_terminal(windows::STD_OUTPUT_HANDLE)
}

#[cfg(windows)]
pub fn stdin_is_terminal() -> bool {
    windows::is_terminal(windows::STD_INPUT_HANDLE)
}

#[cfg(windows)]
pub fn window_width() -> usize {
    windows::window_width()
}

/// Кодировка консоли (`ConsoleSetup` в .NET). На macOS и Linux консоль и так UTF-8. На Windows cmd.exe и старый PowerShell
/// работают в кодовой странице (866, 1251): пока хотя бы один из потоков в терминале, кодовая страница консоли переключается на
/// UTF-8 и возвращается при выходе; перенаправленные потоки Rust и так пишет в UTF-8 без BOM. На Windows не проверялось.
pub struct ConsoleSetup {
    #[cfg(windows)]
    previous_output: Option<u32>,
    #[cfg(windows)]
    previous_input: Option<u32>,
}

impl ConsoleSetup {
    #[cfg(not(windows))]
    pub fn open() -> ConsoleSetup {
        ConsoleSetup {}
    }

    #[cfg(windows)]
    pub fn open() -> ConsoleSetup {
        let (previous_output, previous_input) = windows::switch_to_utf8();
        ConsoleSetup {
            previous_output,
            previous_input,
        }
    }
}

impl Drop for ConsoleSetup {
    fn drop(&mut self) {
        #[cfg(windows)]
        windows::restore(self.previous_output, self.previous_input);
    }
}

#[cfg(windows)]
mod windows {
    use windows_sys::Win32::System::Console::{
        CONSOLE_SCREEN_BUFFER_INFO, GetConsoleCP, GetConsoleMode, GetConsoleOutputCP, GetConsoleScreenBufferInfo, GetStdHandle,
        SetConsoleCP, SetConsoleOutputCP,
    };
    pub use windows_sys::Win32::System::Console::{STD_ERROR_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE};

    const UTF8: u32 = 65001;

    pub fn is_terminal(handle: u32) -> bool {
        unsafe {
            let h = GetStdHandle(handle);
            let mut mode = 0;
            GetConsoleMode(h, &mut mode) != 0
        }
    }

    pub fn window_width() -> usize {
        unsafe {
            let h = GetStdHandle(STD_OUTPUT_HANDLE);
            let mut info: CONSOLE_SCREEN_BUFFER_INFO = std::mem::zeroed();
            if GetConsoleScreenBufferInfo(h, &mut info) != 0 {
                usize::try_from(info.srWindow.Right - info.srWindow.Left + 1).unwrap_or(0)
            } else {
                0
            }
        }
    }

    /// Кодовая страница — одна на вывод (stdout и stderr вместе): переключается, пока хотя бы один поток в терминале.
    pub fn switch_to_utf8() -> (Option<u32>, Option<u32>) {
        let mut previous_output = None;
        let mut previous_input = None;
        unsafe {
            if is_terminal(STD_OUTPUT_HANDLE) || is_terminal(STD_ERROR_HANDLE) {
                let current = GetConsoleOutputCP();
                if current != 0 && current != UTF8 && SetConsoleOutputCP(UTF8) != 0 {
                    previous_output = Some(current);
                }
            }
            if is_terminal(STD_INPUT_HANDLE) {
                let current = GetConsoleCP();
                if current != 0 && current != UTF8 && SetConsoleCP(UTF8) != 0 {
                    previous_input = Some(current);
                }
            }
        }
        (previous_output, previous_input)
    }

    pub fn restore(previous_output: Option<u32>, previous_input: Option<u32>) {
        unsafe {
            if let Some(cp) = previous_output {
                SetConsoleOutputCP(cp);
            }
            if let Some(cp) = previous_input {
                SetConsoleCP(cp);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn limit(t: Terminal, truncate: bool, no_truncate: bool, width: Option<&str>) -> Option<usize> {
        t.limit_with(truncate, no_truncate, width, None).unwrap()
    }

    fn piped(columns: Option<&str>, lines: Option<&str>) -> Terminal {
        Terminal::detect(false, 0, columns, lines, false)
    }

    #[test]
    fn a_terminal_with_a_known_width_truncates_by_default_and_the_rest_does_not() {
        assert_eq!(limit(Terminal::new(true, 80), false, false, None), Some(80));
        assert_eq!(limit(Terminal::new(true, 0), false, false, None), None);
        assert_eq!(limit(Terminal::new(false, 80), false, false, None), None);
    }

    #[test]
    fn switches_and_width_override_the_default() {
        assert_eq!(limit(Terminal::new(false, 0), false, false, Some("60")), Some(60));
        assert_eq!(limit(Terminal::new(false, 100), true, false, None), Some(100));
        assert_eq!(limit(Terminal::new(false, 100), false, false, Some("auto")), Some(100));
        assert_eq!(limit(Terminal::new(false, 90), false, false, Some("0")), Some(90));
        assert_eq!(limit(Terminal::new(false, 0), true, false, None), None);
        assert_eq!(limit(Terminal::new(true, 80), false, true, None), None);
        assert_eq!(limit(Terminal::new(true, 80), false, true, Some("50")), None);
    }

    #[test]
    fn the_width_has_a_floor_and_bad_values_are_errors() {
        assert_eq!(limit(Terminal::new(true, 8), false, false, None), Some(20));
        assert_eq!(limit(Terminal::new(false, 0), false, false, Some("5")), Some(20));
        let error = |t: Terminal, a, b, w| t.limit_with(a, b, w, None).unwrap_err().text();
        assert_eq!(
            error(Terminal::new(true, 80), false, false, Some("wide")),
            "Error: Width must be a non-negative number or 'auto', got 'wide'"
        );
        assert!(Terminal::new(true, 80).limit_with(false, false, Some("-3"), None).is_err());
        assert_eq!(
            error(Terminal::new(true, 80), true, true, None),
            "Error: Use either --truncate or --no-truncate, not both"
        );
    }

    #[test]
    fn the_variable_sets_the_width_and_the_option_wins() {
        assert_eq!(
            Terminal::new(false, 0).limit_with(false, false, None, Some("45")).unwrap(),
            Some(45)
        );
        assert_eq!(
            Terminal::new(false, 0).limit_with(false, false, Some("30"), Some("45")).unwrap(),
            Some(30)
        );
        assert_eq!(Terminal::new(true, 80).limit_with(false, true, None, Some("45")).unwrap(), None);
    }

    #[test]
    fn the_terminal_width_is_read_from_columns_when_the_window_is_unknown() {
        assert_eq!(Terminal::detect(true, 0, Some("72"), None, false).columns, 72);
        assert_eq!(Terminal::detect(true, 0, Some("junk"), None, false).columns, 0);
        assert_eq!(Terminal::detect(true, 0, Some("-5"), None, false).columns, 0);
        assert_eq!(Terminal::detect(true, 120, Some("72"), None, true).columns, 119);
    }

    #[test]
    fn both_columns_and_lines_make_a_pipe_a_screen() {
        assert_eq!(limit(piped(Some("40"), Some("24")), false, false, None), Some(40));
        assert_eq!(limit(piped(Some("8"), Some("24")), false, false, None), Some(20));
        assert_eq!(limit(piped(Some("40"), None), false, false, None), None);
        assert_eq!(limit(piped(None, Some("24")), false, false, None), None);
        assert_eq!(limit(piped(None, None), false, false, None), None);
        assert_eq!(limit(piped(Some("0"), Some("24")), false, false, None), None);
        assert_eq!(limit(piped(Some("40"), Some("0")), false, false, None), None);
        assert_eq!(limit(piped(Some("wide"), Some("24")), false, false, None), None);
        assert_eq!(limit(piped(Some("40"), Some("tall")), false, false, None), None);
        assert_eq!(limit(piped(Some("-40"), Some("24")), false, false, None), None);
    }

    #[test]
    fn under_watch_the_switches_and_the_variable_still_win() {
        let watch = piped(Some("40"), Some("24"));
        assert_eq!(limit(watch, false, true, None), None);
        assert_eq!(limit(watch, false, false, Some("60")), Some(60));
        assert_eq!(limit(watch, true, false, None), Some(40));
        assert_eq!(watch.limit_with(false, false, None, Some("off")).unwrap(), None);
        assert_eq!(watch.limit_with(false, false, Some("50"), Some("off")).unwrap(), Some(50));
        assert_eq!(watch.limit_with(false, false, None, Some("0")).unwrap(), None);
        assert_eq!(watch.limit_with(false, false, None, Some("55")).unwrap(), Some(55));
        assert_eq!(limit(watch, false, false, Some("off")), None);
        assert_eq!(limit(watch, false, false, Some("auto")), Some(40));
    }

    #[test]
    fn parse_int_is_like_dotnet() {
        assert_eq!(parse_int(" 42 "), Some(42));
        assert_eq!(parse_int("-3"), Some(-3));
        assert_eq!(parse_int("+7"), Some(7));
        assert_eq!(parse_int("4x"), None);
        assert_eq!(parse_int(""), None);
    }
}
