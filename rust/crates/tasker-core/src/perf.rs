//! Фазы одного вызова для профилирования (`PerfTrace` в .NET, стенд `scripts/perf/bench.py`). Включается переменной
//! `TASKER_PROFILE=1`; без неё [`mark`] и [`count`] — одна проверка флага. Метки печатаются в stderr после команды строками
//! `[profile] имя миллисекунды_от_старта_процесса jit`, счётчики — `[count] имя число миллисекунды`; JIT в Rust нет — счётчик
//! `jit.methods` и третье поле меток всегда 0. Модуль в `tasker-core`, чтобы метки ставили и консоль, и индекс области.
use std::sync::{Mutex, OnceLock};
use std::time::Instant;

/// Потолок числа меток: долгоживущий процесс (демон) с `TASKER_PROFILE` не копит их без конца.
const MAX_MARKS: usize = 10_000;

struct State {
    origin: Instant,
    /// Сколько процесс жил до первого обращения к профилю (запуск процесса и загрузка бинарника) по часам ОС; 0 — не узнать.
    process_offset_ms: f64,
    marks: Vec<(String, f64)>,
    counters: Vec<(String, usize, f64)>,
}

static ENABLED: OnceLock<bool> = OnceLock::new();
static STATE: OnceLock<Mutex<State>> = OnceLock::new();

pub fn enabled() -> bool {
    *ENABLED.get_or_init(|| std::env::var("TASKER_PROFILE").is_ok_and(|v| !v.is_empty() && v != "0"))
}

fn state() -> &'static Mutex<State> {
    STATE.get_or_init(|| {
        let origin = Instant::now();
        Mutex::new(State {
            origin,
            process_offset_ms: process_age_ms().unwrap_or(0.0).max(0.0),
            marks: Vec::new(),
            counters: Vec::new(),
        })
    })
}

/// Возраст процесса сейчас, мс, по времени его запуска у ядра (macOS — `proc_pidinfo(PROC_PIDTBSDINFO)`, микросекунды; Linux —
/// `/proc/self/stat`, тики). Без внешних процессов: метка не должна сама стоить миллисекунды.
#[cfg(target_os = "macos")]
fn process_age_ms() -> Option<f64> {
    let mut info: libc::proc_bsdinfo = unsafe { std::mem::zeroed() };
    let size = std::mem::size_of::<libc::proc_bsdinfo>() as libc::c_int;
    // SAFETY: буфер — одна структура proc_bsdinfo, её размер передаётся; ядро пишет не больше size байт.
    let written = unsafe {
        libc::proc_pidinfo(
            std::process::id() as libc::c_int,
            libc::PROC_PIDTBSDINFO,
            0,
            (&mut info as *mut libc::proc_bsdinfo).cast(),
            size,
        )
    };
    if written != size {
        return None;
    }
    let started = std::time::UNIX_EPOCH
        + std::time::Duration::from_secs(info.pbi_start_tvsec)
        + std::time::Duration::from_micros(info.pbi_start_tvusec);
    std::time::SystemTime::now()
        .duration_since(started)
        .ok()
        .map(|d| d.as_secs_f64() * 1000.0)
}

#[cfg(target_os = "linux")]
fn process_age_ms() -> Option<f64> {
    // Поле 22 `starttime` — тики от загрузки системы; сейчас — CLOCK_BOOTTIME. Точность — тик (обычно 10 мс).
    let stat = std::fs::read_to_string("/proc/self/stat").ok()?;
    let rest = &stat[stat.rfind(')')? + 2..];
    let ticks: f64 = rest.split_whitespace().nth(19)?.parse().ok()?;
    let hz = unsafe { libc::sysconf(libc::_SC_CLK_TCK) } as f64;
    let mut now = libc::timespec { tv_sec: 0, tv_nsec: 0 };
    // SAFETY: timespec на стеке, clock_gettime только пишет в него.
    if hz <= 0.0 || unsafe { libc::clock_gettime(libc::CLOCK_BOOTTIME, &mut now) } != 0 {
        return None;
    }
    let now_ms = now.tv_sec as f64 * 1000.0 + now.tv_nsec as f64 / 1e6;
    Some(now_ms - ticks * 1000.0 / hz)
}

#[cfg(not(any(target_os = "macos", target_os = "linux")))]
fn process_age_ms() -> Option<f64> {
    None
}

/// Фиксирует момент: прошедшее с запуска процесса время (запуск до первой метки входит в первую — у консоли это `main`).
pub fn mark(name: &str) {
    if !enabled() {
        return;
    }
    let mut state = state().lock().unwrap_or_else(|e| e.into_inner());
    if state.marks.len() >= MAX_MARKS {
        return;
    }
    let ms = state.process_offset_ms + state.origin.elapsed().as_secs_f64() * 1000.0;
    state.marks.push((name.to_string(), ms));
}

/// Начало замера (None, когда профилирование выключено); конец — [`count`].
pub fn start() -> Option<Instant> {
    enabled().then(Instant::now)
}

/// Складывает время от `started` в счётчик `name` (число вызовов и сумма) — для частых мелких операций.
pub fn count(name: &str, started: Option<Instant>) {
    let Some(started) = started else { return };
    let ms = started.elapsed().as_secs_f64() * 1000.0;
    let mut state = state().lock().unwrap_or_else(|e| e.into_inner());
    match state.counters.iter_mut().find(|(n, _, _)| n == name) {
        Some(entry) => {
            entry.1 += 1;
            entry.2 += ms;
        }
        None => state.counters.push((name.to_string(), 1, ms)),
    }
}

/// Печатает метки и счётчики (`PerfTrace.Dump`): JIT-методов в Rust нет, их число и время компиляции — 0.
pub fn dump(writer: &mut dyn std::io::Write) {
    if !enabled() {
        return;
    }
    let state = state().lock().unwrap_or_else(|e| e.into_inner());
    for (name, ms) in &state.marks {
        let _ = writeln!(writer, "[profile] {name} {ms:.1} 0");
    }
    let _ = writeln!(writer, "[count] jit.methods 0 0.0");
    for (name, count, ms) in &state.counters {
        let _ = writeln!(writer, "[count] {name} {count} {ms:.1}");
    }
}

#[cfg(test)]
mod tests {
    #[test]
    #[cfg(any(target_os = "macos", target_os = "linux"))]
    fn the_process_age_is_known_and_plausible() {
        let age = super::process_age_ms().expect("process start time");
        // Тестовый процесс живёт не меньше нуля и не дольше суток (тик Linux — 10 мс, отсюда допуск).
        assert!(age > -20.0 && age < 86_400_000.0, "{age}");
    }
}
