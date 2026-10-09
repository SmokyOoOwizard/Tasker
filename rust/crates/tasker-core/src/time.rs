//! Временные метки в формате .NET `DateTimeOffset`: в файлах — формат `O` (`2026-01-01T00:35:35.1234850+00:00`,
//! всегда 7 знаков дробной части), в JSON (System.Text.Json) — то же без хвостовых нулей дробной части
//! (`2026-01-01T00:35:35.123485+00:00`, при нулевой дроби без точки), в консоли — `2026-01-01 00:35:35 +00:00`.
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Timestamp {
    pub year: i32,
    pub month: u8,
    pub day: u8,
    pub hour: u8,
    pub minute: u8,
    pub second: u8,
    /// Доли секунды в тиках по 100 нс (0..10_000_000).
    pub ticks: u32,
    /// Смещение от UTC в минутах (знак включён).
    pub offset_minutes: i32,
}

/// В JSON индекса и настроек — строка формата `O` (как `DateTimeOffset` в System.Text.Json, с полными тиками).
impl serde::Serialize for Timestamp {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.format_o())
    }
}

impl<'de> serde::Deserialize<'de> for Timestamp {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let text = <String as serde::Deserialize>::deserialize(deserializer)?;
        Timestamp::parse(&text).ok_or_else(|| serde::de::Error::custom(format!("'{text}' is not a timestamp")))
    }
}

impl Timestamp {
    pub const UNIX_EPOCH: Timestamp = Timestamp {
        year: 1970,
        month: 1,
        day: 1,
        hour: 0,
        minute: 0,
        second: 0,
        ticks: 0,
        offset_minutes: 0,
    };

    /// Текущее время UTC, как `DateTimeOffset.UtcNow` (точность — системные часы, 100 нс).
    pub fn now_utc() -> Timestamp {
        let since = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default();
        Self::from_unix_ticks(since.as_secs() as i64 * 10_000_000 + (since.subsec_nanos() / 100) as i64)
    }

    /// Из числа тиков от 1970-01-01T00:00:00Z (смещение 0).
    pub fn from_unix_ticks(ticks: i64) -> Timestamp {
        let secs = ticks.div_euclid(10_000_000);
        let frac = ticks.rem_euclid(10_000_000) as u32;
        let days = secs.div_euclid(86_400);
        let day_secs = secs.rem_euclid(86_400);
        let (year, month, day) = civil_from_days(days);
        Timestamp {
            year,
            month,
            day,
            hour: (day_secs / 3600) as u8,
            minute: (day_secs % 3600 / 60) as u8,
            second: (day_secs % 60) as u8,
            ticks: frac,
            offset_minutes: 0,
        }
    }

    /// Тики от 1970-01-01T00:00:00Z с учётом смещения (для сравнения моментов).
    pub fn unix_ticks(&self) -> i64 {
        let days = days_from_civil(self.year, self.month, self.day);
        let secs = days * 86_400 + self.hour as i64 * 3600 + self.minute as i64 * 60 + self.second as i64 - self.offset_minutes as i64 * 60;
        secs * 10_000_000 + self.ticks as i64
    }

    /// Разбор формата `O` (и его укороченных дробей, как пишет JSON .NET; `Z` принимается как +00:00).
    pub fn parse(text: &str) -> Option<Timestamp> {
        let b = text.as_bytes();
        if b.len() < 19 || b[4] != b'-' || b[7] != b'-' || b[10] != b'T' || b[13] != b':' || b[16] != b':' {
            return None;
        }
        let num = |range: std::ops::Range<usize>| text.get(range).and_then(|s| s.parse::<u32>().ok());
        let year = num(0..4)? as i32;
        let month = num(5..7)? as u8;
        let day = num(8..10)? as u8;
        let hour = num(11..13)? as u8;
        let minute = num(14..16)? as u8;
        let second = num(17..19)? as u8;
        let mut i = 19;
        let mut ticks = 0u32;
        if b.get(i) == Some(&b'.') {
            i += 1;
            let start = i;
            while i < b.len() && b[i].is_ascii_digit() {
                i += 1;
            }
            let digits = &text[start..i];
            if digits.is_empty() || digits.len() > 7 {
                return None;
            }
            ticks = format!("{digits:0<7}").parse().ok()?;
        }
        let offset_minutes = match b.get(i) {
            Some(b'Z') if i + 1 == b.len() => 0,
            Some(sign @ (b'+' | b'-')) if i + 6 == b.len() && b[i + 3] == b':' => {
                let h = num(i + 1..i + 3)? as i32;
                let m = num(i + 4..i + 6)? as i32;
                if h > 14 || m > 59 {
                    return None;
                }
                let total = h * 60 + m;
                if *sign == b'-' { -total } else { total }
            }
            _ => return None,
        };
        if !(1..=12).contains(&month) || day == 0 || day > days_in_month(year, month) || hour > 23 || minute > 59 || second > 59 {
            return None;
        }
        Some(Timestamp {
            year,
            month,
            day,
            hour,
            minute,
            second,
            ticks,
            offset_minutes,
        })
    }

    fn offset_text(&self) -> String {
        let sign = if self.offset_minutes < 0 { '-' } else { '+' };
        let abs = self.offset_minutes.abs();
        format!("{sign}{:02}:{:02}", abs / 60, abs % 60)
    }

    /// Формат `O`: 7 знаков дробной части всегда.
    pub fn format_o(&self) -> String {
        format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:07}{}",
            self.year,
            self.month,
            self.day,
            self.hour,
            self.minute,
            self.second,
            self.ticks,
            self.offset_text()
        )
    }

    /// Как System.Text.Json: дробная часть без хвостовых нулей, при нуле — без точки.
    pub fn format_json(&self) -> String {
        let mut s = format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
            self.year, self.month, self.day, self.hour, self.minute, self.second
        );
        if self.ticks != 0 {
            let frac = format!("{:07}", self.ticks);
            s.push('.');
            s.push_str(frac.trim_end_matches('0'));
        }
        s.push_str(&self.offset_text());
        s
    }

    /// Как консоль: `yyyy-MM-dd HH:mm:ss zzz`.
    pub fn format_console(&self) -> String {
        format!(
            "{:04}-{:02}-{:02} {:02}:{:02}:{:02} {}",
            self.year,
            self.month,
            self.day,
            self.hour,
            self.minute,
            self.second,
            self.offset_text()
        )
    }
}

impl fmt::Display for Timestamp {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.format_o())
    }
}

pub fn is_leap_year(year: i32) -> bool {
    year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
}

pub fn days_in_month(year: i32, month: u8) -> u8 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if is_leap_year(year) => 29,
        2 => 28,
        _ => 0,
    }
}

// Алгоритмы Говарда Хиннанта (days_from_civil / civil_from_days).
fn days_from_civil(y: i32, m: u8, d: u8) -> i64 {
    let y = if m <= 2 { y - 1 } else { y } as i64;
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = (m as i64 + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

fn civil_from_days(z: i64) -> (i32, u8, u8) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u8;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u8;
    ((if m <= 2 { y + 1 } else { y }) as i32, m, d)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn o_format_round_trips_and_json_trims_zeros() {
        let t = Timestamp::parse("2026-01-01T00:35:35.1234850+00:00").unwrap();
        assert_eq!(t.format_o(), "2026-01-01T00:35:35.1234850+00:00");
        assert_eq!(t.format_json(), "2026-01-01T00:35:35.123485+00:00");
        assert_eq!(t.format_console(), "2026-01-01 00:35:35 +00:00");
        let whole = Timestamp::parse("2026-10-02T12:00:25+03:00").unwrap();
        assert_eq!(whole.format_o(), "2026-10-02T12:00:25.0000000+03:00");
        assert_eq!(whole.format_json(), "2026-10-02T12:00:25+03:00");
        assert_eq!(Timestamp::parse("2026-02-29T00:00:00Z"), None);
        assert!(Timestamp::parse("2028-02-29T00:00:00Z").is_some());
        assert_eq!(Timestamp::parse("2026-01-01T00:00:00.12345+00:00").unwrap().ticks, 1_234_500);
    }

    #[test]
    fn unix_ticks_round_trip() {
        for text in [
            "1970-01-01T00:00:00.0000000+00:00",
            "2026-10-09T11:21:47.1234567+00:00",
            "1969-12-31T23:59:59.9999999+00:00",
        ] {
            let t = Timestamp::parse(text).unwrap();
            assert_eq!(Timestamp::from_unix_ticks(t.unix_ticks()), t, "{text}");
        }
        let plus = Timestamp::parse("2026-01-01T03:00:00+03:00").unwrap();
        let utc = Timestamp::parse("2026-01-01T00:00:00+00:00").unwrap();
        assert_eq!(plus.unix_ticks(), utc.unix_ticks());
    }
}
