//! Часы сервисов (`TimeProvider` в .NET): текущее время и пауза. Системные — настоящие; [`FakeClock`] для тестов двигает
//! время сам, и ожидание блокировок в тестах не спит.
use std::sync::Mutex;
use std::time::Duration;
use tasker_core::Timestamp;

pub trait Clock: Send + Sync {
    fn now(&self) -> Timestamp;

    /// Пауза (ожидание чужой блокировки, пауза между попытками записи).
    fn sleep(&self, duration: Duration);
}

pub struct SystemClock;

impl Clock for SystemClock {
    fn now(&self) -> Timestamp {
        Timestamp::now_utc()
    }

    fn sleep(&self, duration: Duration) {
        std::thread::sleep(duration);
    }
}

/// Часы с ручным управлением: `sleep` продвигает время вместо ожидания.
pub struct FakeClock {
    now: Mutex<Timestamp>,
}

impl FakeClock {
    pub fn new(now: Timestamp) -> FakeClock {
        FakeClock { now: Mutex::new(now) }
    }

    pub fn at(text: &str) -> FakeClock {
        FakeClock::new(Timestamp::parse(text).expect("timestamp"))
    }

    pub fn set(&self, now: Timestamp) {
        *self.now.lock().unwrap_or_else(|e| e.into_inner()) = now;
    }

    pub fn advance(&self, duration: Duration) {
        let mut now = self.now.lock().unwrap_or_else(|e| e.into_inner());
        *now = add(*now, duration);
    }
}

impl Clock for FakeClock {
    fn now(&self) -> Timestamp {
        *self.now.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn sleep(&self, duration: Duration) {
        self.advance(duration);
    }
}

/// `timestamp + duration` в тиках (100 нс); смещение сохраняется.
pub fn add(now: Timestamp, duration: Duration) -> Timestamp {
    let ticks = now.unix_ticks() + (duration.as_nanos() / 100) as i64;
    let utc = Timestamp::from_unix_ticks(ticks);
    if now.offset_minutes == 0 {
        utc
    } else {
        // В локальном смещении: сдвигаем поля на смещение, оставляя его в метке.
        let shifted = Timestamp::from_unix_ticks(ticks + now.offset_minutes as i64 * 60 * 10_000_000);
        Timestamp {
            offset_minutes: now.offset_minutes,
            ..shifted
        }
    }
}

/// Остаток до `deadline` от `now`; None — срок прошёл.
pub fn until(now: &Timestamp, deadline: &Timestamp) -> Option<Duration> {
    let left = deadline.unix_ticks() - now.unix_ticks();
    (left > 0).then(|| Duration::from_nanos(left as u64 * 100))
}
