//! Фазы одного вызова `tasker` для профилирования (`PerfTrace` в .NET, стенд `scripts/perf/bench.py`). Включается переменной
//! `TASKER_PROFILE=1`; без неё [`mark`] — одна проверка флага. Метки печатаются в stderr после команды строками
//! `[profile] имя миллисекунды_от_старта_процесса jit`; JIT в Rust нет — счётчик всегда 0.
use std::sync::{Mutex, OnceLock};
use std::time::Instant;

struct State {
    origin: Instant,
    /// Сколько процесс жил до первой метки (старт рантайма): здесь — время от запуска процесса по часам ОС, если известно.
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
        Mutex::new(State {
            origin: Instant::now(),
            process_offset_ms: process_offset_ms(),
            marks: Vec::new(),
            counters: Vec::new(),
        })
    })
}

/// Время от запуска процесса до первой метки по часам ОС (не точнее секунд на большинстве систем), 0 — не узнать.
fn process_offset_ms() -> f64 {
    #[cfg(unix)]
    {
        if let Ok(output) = std::process::Command::new("ps")
            .args(["-o", "etime=", "-p", &std::process::id().to_string()])
            .output()
            && output.status.success()
        {
            let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
            // Формат etime: [[dd-]hh:]mm:ss.
            let (days, rest) = match text.split_once('-') {
                Some((d, r)) => (d.parse::<f64>().unwrap_or(0.0), r.to_string()),
                None => (0.0, text.clone()),
            };
            let parts: Vec<f64> = rest.split(':').map(|p| p.parse::<f64>().unwrap_or(0.0)).collect();
            let seconds = match parts.as_slice() {
                [h, m, s] => h * 3600.0 + m * 60.0 + s,
                [m, s] => m * 60.0 + s,
                [s] => *s,
                _ => 0.0,
            };
            // Шаг etime — секунда: это оценка сверху, как и у .NET (Process.StartTime тоже грубое); берём не больше секунды.
            return ((days * 86400.0 + seconds) * 1000.0).min(1000.0);
        }
    }
    0.0
}

/// Фиксирует момент: прошедшее с запуска процесса время.
pub fn mark(name: &str) {
    if !enabled() {
        return;
    }
    let mut state = state().lock().unwrap_or_else(|e| e.into_inner());
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
