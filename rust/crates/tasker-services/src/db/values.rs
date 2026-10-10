//! Значения в столбцах в форматах EF Core SQLite: Guid, время, версия, списки id для `json_each`.
use rusqlite::Row;
use rusqlite::types::Type;
use tasker_core::Timestamp;
use tasker_core::ids::guid_d;
use uuid::Uuid;

/// Версия новой записи (`DbVersion.Initial`).
pub const INITIAL_VERSION: i32 = 1;

/// Guid в столбце и в параметре: форма D заглавными — так его пишет EF Core SQLite (`SqliteGuidTypeMapping`). Сравнение в SQL
/// текстовое (BINARY), поэтому регистр важен.
pub fn g(id: &Uuid) -> String {
    guid_d(id).to_ascii_uppercase()
}

/// Список id одним параметром для `IN (SELECT value FROM json_each(?))`.
pub fn json_ids(ids: &[Uuid]) -> String {
    serde_json::to_string(&ids.iter().map(g).collect::<Vec<_>>()).expect("strings serialize")
}

/// Время в столбце: UTC, `yyyy-MM-dd HH:mm:ss.FFFFFFF` — дробь без хвостовых нулей, при нулевой — без точки (`DbTime.ToDb` и
/// формат `DateTime` у Microsoft.Data.Sqlite).
pub fn db_time(time: &Timestamp) -> String {
    let t = utc(time);
    let mut text = format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
        t.year, t.month, t.day, t.hour, t.minute, t.second
    );
    if t.ticks != 0 {
        let fraction = format!("{:07}", t.ticks);
        text.push('.');
        text.push_str(fraction.trim_end_matches('0'));
    }
    text
}

/// Тот же момент со смещением 0 (`DateTimeOffset.UtcDateTime` → `DbTime.FromDb`).
pub fn utc(time: &Timestamp) -> Timestamp {
    if time.offset_minutes == 0 {
        *time
    } else {
        Timestamp::from_unix_ticks(time.unix_ticks())
    }
}

/// Время из столбца (`DateTime.Parse` у Microsoft.Data.Sqlite): `yyyy-MM-dd HH:mm:ss[.f…]`, допускается `T` и смещение или `Z`.
pub fn parse_time(text: &str) -> Option<Timestamp> {
    let text = text.trim();
    if text.len() < 19 {
        return None;
    }
    let mut normalized = String::with_capacity(text.len() + 6);
    normalized.push_str(&text[..10]);
    normalized.push('T');
    normalized.push_str(text.get(11..)?);
    let has_offset = {
        let rest = &normalized[19..];
        rest.ends_with('Z') || rest.contains('+') || rest.contains('-')
    };
    if !has_offset {
        normalized.push_str("+00:00");
    }
    Timestamp::parse(&normalized).map(|t| utc(&t))
}

/// `DbVersion.Parse`: только цифры (`NumberStyles.None`), в пределах int; иначе None — такой версии в БД точно нет.
pub fn parse_version(text: &str) -> Option<i32> {
    if text.is_empty() || !text.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    text.parse().ok()
}

pub fn conversion(column: usize, message: String) -> rusqlite::Error {
    rusqlite::Error::FromSqlConversionFailure(column, Type::Text, message.into())
}

pub fn parse_guid_text(column: usize, text: &str) -> rusqlite::Result<Uuid> {
    Uuid::try_parse(text).map_err(|e| conversion(column, format!("'{text}' is not a Guid: {e}")))
}

pub fn guid_col(row: &Row<'_>, column: usize) -> rusqlite::Result<Uuid> {
    let text: String = row.get(column)?;
    parse_guid_text(column, &text)
}

pub fn optional_guid_col(row: &Row<'_>, column: usize) -> rusqlite::Result<Option<Uuid>> {
    let text: Option<String> = row.get(column)?;
    text.map(|t| parse_guid_text(column, &t)).transpose()
}

pub fn time_col(row: &Row<'_>, column: usize) -> rusqlite::Result<Timestamp> {
    let text: String = row.get(column)?;
    parse_time(&text).ok_or_else(|| conversion(column, format!("'{text}' is not a date and time")))
}

/// Пауза между попытками секции записи: случайное число миллисекунд в `[from, to)` (`Random.Shared.Next`).
pub fn jitter(from: u32, to: u32) -> u32 {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.subsec_nanos())
        .unwrap_or(0);
    from + (nanos ^ std::process::id()) % (to - from)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn times_are_written_like_ef_core() {
        let t = Timestamp::parse("2026-10-10T20:57:29.4758920+00:00").unwrap();
        assert_eq!(db_time(&t), "2026-10-10 20:57:29.475892");
        let whole = Timestamp::parse("2026-01-01T00:00:00.0000000+00:00").unwrap();
        assert_eq!(db_time(&whole), "2026-01-01 00:00:00");
        let shifted = Timestamp::parse("2026-01-01T03:00:00.1000000+03:00").unwrap();
        assert_eq!(db_time(&shifted), "2026-01-01 00:00:00.1");
        assert_eq!(parse_time("2026-10-10 20:57:29.475892").unwrap(), t);
        assert_eq!(parse_time("2026-01-01 00:00:00").unwrap(), whole);
        assert_eq!(parse_time("2026-01-01T00:00:00Z").unwrap(), whole);
    }

    #[test]
    fn guids_are_upper_case_and_versions_are_digits() {
        let id = Uuid::parse_str("1c5289e1-1af3-4ed6-9373-7a90ea0d9673").unwrap();
        assert_eq!(g(&id), "1C5289E1-1AF3-4ED6-9373-7A90EA0D9673");
        assert_eq!(parse_version("12"), Some(12));
        assert_eq!(parse_version("+1"), None);
        assert_eq!(parse_version(" 1"), None);
        assert_eq!(parse_version("a1b2c3d4e5f60718"), None);
        assert_eq!(parse_version("99999999999"), None);
    }
}
