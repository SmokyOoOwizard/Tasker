//! `FieldValues`: значения полей хранятся строками в каноническом виде — `007` → `7`, `1.50` → `1.5` (формат `R`),
//! `TRUE` → `true`, даты `yyyy-MM-dd`, значения перечислений — id в форме D.
use crate::error::{Result, TaskerError};
use crate::ids::{guid_d, parse_guid};
use crate::model::{FieldEnum, FieldType};
use crate::time::days_in_month;
use crate::validate::{eq_ignore_case, utf16_len};
use uuid::Uuid;

pub const MAX_STRING_LENGTH: usize = 2000;
pub const DATE_FORMAT: &str = "yyyy-MM-dd";

/// Проверяет и канонизирует список значений поля: пустые отвергаются, повторы отвергаются, несколько значений — только у
/// множественного поля.
pub fn normalize(
    name: &str,
    field_type: FieldType,
    multiple: bool,
    enumeration: Option<&FieldEnum>,
    raw: &[Option<String>],
) -> Result<Vec<String>> {
    let mut result: Vec<String> = Vec::new();
    for value in raw {
        let canonical = one(name, field_type, enumeration, value.as_deref())?;
        if result.contains(&canonical) {
            return Err(TaskerError::validation(format!(
                "Field '{name}': value '{}' is repeated",
                value.as_deref().unwrap_or("")
            )));
        }
        result.push(canonical);
    }
    if result.len() > 1 && !multiple {
        return Err(TaskerError::validation(format!(
            "Field '{name}' holds a single value, but {} were given",
            result.len()
        )));
    }
    Ok(result)
}

/// Канонический вид одного значения без имени поля и перечисления; None — не разбирается.
pub fn try_parse(field_type: FieldType, text: &str) -> Option<String> {
    one("", field_type, None, Some(text)).ok()
}

fn one(name: &str, field_type: FieldType, enumeration: Option<&FieldEnum>, raw: Option<&str>) -> Result<String> {
    let text = raw.map(str::trim).unwrap_or("");
    if text.is_empty() {
        return Err(TaskerError::validation(format!(
            "Field '{name}': a value must not be empty (pass no values to clear the field)"
        )));
    }
    match field_type {
        FieldType::String => {
            if utf16_len(text) > MAX_STRING_LENGTH {
                return Err(TaskerError::validation(format!(
                    "Field '{name}': a value must be at most {MAX_STRING_LENGTH} characters"
                )));
            }
            Ok(text.to_string())
        }
        FieldType::Int => parse_long(text)
            .map(|n| n.to_string())
            .ok_or_else(|| invalid(name, text, "an integer")),
        FieldType::Float => parse_double(text)
            .filter(|f| f.is_finite())
            .map(format_double)
            .ok_or_else(|| invalid(name, text, "a number (use '.' as the decimal separator)")),
        FieldType::Bool => match text.to_lowercase().as_str() {
            "true" => Ok("true".into()),
            "false" => Ok("false".into()),
            _ => Err(invalid(name, text, "true or false")),
        },
        FieldType::Date => parse_date(text).ok_or_else(|| invalid(name, text, &format!("a date in the format {DATE_FORMAT}"))),
        FieldType::Enum => enum_value(name, enumeration, text).map(|id| guid_d(&id)),
    }
}

/// `long.TryParse(NumberStyles.AllowLeadingSign)`: только цифры ASCII и ведущий знак, без пробелов внутри.
fn parse_long(text: &str) -> Option<i64> {
    let body = text.strip_prefix(['+', '-']).unwrap_or(text);
    if body.is_empty() || !body.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    text.parse::<i64>().ok()
}

/// `double.TryParse(NumberStyles.Float)`: знак, цифры, точка, экспонента, пробелы по краям уже обрезаны.
fn parse_double(text: &str) -> Option<f64> {
    let lowered = text.to_ascii_lowercase();
    let body = lowered.strip_prefix(['+', '-']).unwrap_or(&lowered);
    let (mantissa, exponent) = match body.split_once('e') {
        Some((m, e)) => (m, Some(e)),
        None => (body, None),
    };
    let (int_part, frac_part) = match mantissa.split_once('.') {
        Some((i, f)) => (i, f),
        None => (mantissa, ""),
    };
    if int_part.is_empty() && frac_part.is_empty() {
        return None;
    }
    if !int_part.bytes().all(|b| b.is_ascii_digit()) || !frac_part.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    if let Some(e) = exponent {
        let digits = e.strip_prefix(['+', '-']).unwrap_or(e);
        if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
    }
    lowered.parse::<f64>().ok()
}

/// `double.ToString("R", InvariantCulture)` в .NET Core 3.0+: кратчайшее представление, обратимое без потерь;
/// экспоненциальная форма (`1E+15`, `1E-05`) при показателе ≥ 15 или < −5, с двумя знаками показателя минимум.
pub fn format_double(value: f64) -> String {
    if value == 0.0 {
        return if value.is_sign_negative() { "-0".into() } else { "0".into() };
    }
    if value.is_nan() {
        return "NaN".into();
    }
    if value.is_infinite() {
        return if value > 0.0 { "Infinity".into() } else { "-Infinity".into() };
    }
    // Кратчайшие цифры: Rust печатает их в научной записи как d.ddde±x.
    let sci = format!("{value:e}");
    let (mantissa, exp) = sci.split_once('e').unwrap();
    let exponent: i32 = exp.parse().unwrap();
    let negative = mantissa.starts_with('-');
    let digits: String = mantissa.trim_start_matches('-').chars().filter(|c| *c != '.').collect();
    let sign = if negative { "-" } else { "" };
    if (-4..15).contains(&exponent) {
        let point = exponent + 1; // позиция десятичной точки относительно начала цифр
        let text = if point <= 0 {
            format!("0.{}{}", "0".repeat((-point) as usize), digits)
        } else if (point as usize) >= digits.len() {
            format!("{}{}", digits, "0".repeat(point as usize - digits.len()))
        } else {
            format!("{}.{}", &digits[..point as usize], &digits[point as usize..])
        };
        format!("{sign}{text}")
    } else {
        let mantissa_text = if digits.len() == 1 {
            digits.clone()
        } else {
            format!("{}.{}", &digits[..1], &digits[1..])
        };
        format!(
            "{sign}{mantissa_text}E{}{:02}",
            if exponent < 0 { '-' } else { '+' },
            exponent.abs()
        )
    }
}

/// `DateOnly.TryParseExact("yyyy-MM-dd")`.
fn parse_date(text: &str) -> Option<String> {
    let b = text.as_bytes();
    if b.len() != 10 || b[4] != b'-' || b[7] != b'-' || !b.iter().enumerate().all(|(i, c)| matches!(i, 4 | 7) || c.is_ascii_digit()) {
        return None;
    }
    let year: i32 = text[..4].parse().ok()?;
    let month: u8 = text[5..7].parse().ok()?;
    let day: u8 = text[8..].parse().ok()?;
    if year < 1 || !(1..=12).contains(&month) || day == 0 || day > days_in_month(year, month) {
        return None;
    }
    Some(text.to_string())
}

fn enum_value(name: &str, enumeration: Option<&FieldEnum>, text: &str) -> Result<Uuid> {
    let Some(e) = enumeration else {
        return Err(TaskerError::validation(format!("Field '{name}': its enum was not found")));
    };
    if let Some(id) = parse_guid(text)
        && e.values.iter().any(|v| v.id == id)
    {
        return Ok(id);
    }
    let by_name: Vec<&Uuid> = e.values.iter().filter(|v| eq_ignore_case(&v.name, text)).map(|v| &v.id).collect();
    if by_name.len() == 1 {
        Ok(*by_name[0])
    } else {
        let names: Vec<&str> = e.values.iter().map(|v| v.name.as_str()).collect();
        Err(TaskerError::validation(format!(
            "Field '{name}': '{text}' is not a value of enum '{}' (values: {})",
            e.name,
            names.join(", ")
        )))
    }
}

/// Значения для показа: у перечисления — имена значений вместо id (неизвестный id остаётся как есть).
pub fn texts(field_type: FieldType, enumeration: Option<&FieldEnum>, values: &[String]) -> Vec<String> {
    if field_type != FieldType::Enum {
        return values.to_vec();
    }
    values
        .iter()
        .map(|v| match (parse_guid(v), enumeration) {
            (Some(id), Some(e)) => e
                .values
                .iter()
                .find(|x| x.id == id)
                .map(|x| x.name.clone())
                .unwrap_or_else(|| v.clone()),
            _ => v.clone(),
        })
        .collect()
}

fn invalid(name: &str, text: &str, expected: &str) -> TaskerError {
    TaskerError::validation(format!("Field '{name}': '{text}' is not {expected}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn integers_are_canonical() {
        assert_eq!(try_parse(FieldType::Int, "007").as_deref(), Some("7"));
        assert_eq!(try_parse(FieldType::Int, "-3").as_deref(), Some("-3"));
        assert_eq!(try_parse(FieldType::Int, "+13").as_deref(), Some("13"));
        assert_eq!(try_parse(FieldType::Int, "1.0"), None);
        assert_eq!(try_parse(FieldType::Int, "abc"), None);
        assert_eq!(try_parse(FieldType::Int, "99999999999999999999"), None);
    }

    #[test]
    fn floats_use_dotnet_r_format() {
        assert_eq!(try_parse(FieldType::Float, "1.50").as_deref(), Some("1.5"));
        assert_eq!(try_parse(FieldType::Float, "0.50").as_deref(), Some("0.5"));
        assert_eq!(try_parse(FieldType::Float, "-0.25").as_deref(), Some("-0.25"));
        assert_eq!(try_parse(FieldType::Float, "100").as_deref(), Some("100"));
        assert_eq!(try_parse(FieldType::Float, "1e3").as_deref(), Some("1000"));
        assert_eq!(try_parse(FieldType::Float, ".5").as_deref(), Some("0.5"));
        assert_eq!(try_parse(FieldType::Float, "1,5"), None);
        assert_eq!(format_double(1e15), "1E+15");
        assert_eq!(format_double(123456789012345.0), "123456789012345");
        assert_eq!(format_double(0.0001), "0.0001");
        assert_eq!(format_double(0.00001), "1E-05");
        assert_eq!(format_double(1.5e-7), "1.5E-07");
        assert_eq!(format_double(0.1 + 0.2), "0.30000000000000004");
        assert_eq!(format_double(-0.0), "-0");
    }

    #[test]
    fn bools_and_dates() {
        assert_eq!(try_parse(FieldType::Bool, "TRUE").as_deref(), Some("true"));
        assert_eq!(try_parse(FieldType::Bool, "yes"), None);
        assert_eq!(try_parse(FieldType::Date, "2028-02-29").as_deref(), Some("2028-02-29"));
        assert_eq!(try_parse(FieldType::Date, "2026-02-29"), None);
        assert_eq!(try_parse(FieldType::Date, "2026-1-1"), None);
    }

    #[test]
    fn enum_values_by_name_or_id() {
        let id = Uuid::parse_str("0000000e-0000-4000-8000-00000000000e").unwrap();
        let e = FieldEnum {
            id: Uuid::nil(),
            project_id: Uuid::nil(),
            name: "Priority".into(),
            values: vec![crate::model::FieldEnumValue { id, name: "High".into() }],
            version: String::new(),
        };
        assert_eq!(
            normalize("Priority", FieldType::Enum, false, Some(&e), &[Some("high".into())]).unwrap(),
            vec![guid_d(&id)]
        );
        assert_eq!(
            normalize("Priority", FieldType::Enum, false, Some(&e), &[Some("Low".into())])
                .unwrap_err()
                .message(),
            "Field 'Priority': 'Low' is not a value of enum 'Priority' (values: High)"
        );
        assert_eq!(texts(FieldType::Enum, Some(&e), &[guid_d(&id)]), vec!["High".to_string()]);
    }

    #[test]
    fn repeats_and_multiplicity() {
        let err = normalize("Tags", FieldType::String, true, None, &[Some("a".into()), Some("a".into())]).unwrap_err();
        assert_eq!(err.message(), "Field 'Tags': value 'a' is repeated");
        let err = normalize("Notes", FieldType::String, false, None, &[Some("a".into()), Some("b".into())]).unwrap_err();
        assert_eq!(err.message(), "Field 'Notes' holds a single value, but 2 were given");
        let err = normalize("Notes", FieldType::String, false, None, &[Some("  ".into())]).unwrap_err();
        assert_eq!(
            err.message(),
            "Field 'Notes': a value must not be empty (pass no values to clear the field)"
        );
    }
}
