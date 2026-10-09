//! Проверки входных значений с теми же текстами ошибок, что в .NET (`Validate`).
use crate::error::{Result, TaskerError};
use unicode_general_category::{GeneralCategory, get_general_category};
use uuid::Uuid;

pub const MAX_NAME_LENGTH: usize = 100;
pub const MIN_PASSWORD_LENGTH: usize = 8;

/// Длина строки в единицах UTF-16 — так .NET считает `Length`.
pub fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

/// `string.Equals(a, b, OrdinalIgnoreCase)`: посимвольное сравнение после простого приведения к верхнему регистру
/// (без спец-правил вроде ß → SS).
pub fn eq_ignore_case(a: &str, b: &str) -> bool {
    let mut x = a.encode_utf16().map(upper_unit);
    let mut y = b.encode_utf16().map(upper_unit);
    loop {
        match (x.next(), y.next()) {
            (None, None) => return true,
            (Some(p), Some(q)) if p == q => continue,
            _ => return false,
        }
    }
}

fn upper_unit(u: u16) -> u16 {
    match char::from_u32(u as u32) {
        Some(c) => {
            let mut upper = c.to_uppercase();
            match (upper.next(), upper.next()) {
                (Some(single), None) if (single as u32) <= 0xFFFF => single as u16,
                _ => u,
            }
        }
        None => u,
    }
}

/// `ToLowerInvariant` по единицам UTF-16: только простые однозначные соответствия (как в .NET).
pub fn to_lower_invariant(text: &str) -> String {
    let units: Vec<u16> = text
        .encode_utf16()
        .map(|u| match char::from_u32(u as u32) {
            // Простое соответствие UnicodeData: единственное многознаковое — İ (U+0130) → i + U+0307, простое даёт i.
            Some(c) => match c.to_lowercase().next() {
                Some(single) if (single as u32) <= 0xFFFF => single as u16,
                _ => u,
            },
            None => u,
        })
        .collect();
    String::from_utf16_lossy(&units)
}

/// `ToUpperInvariant` по единицам UTF-16.
pub fn to_upper_invariant(text: &str) -> String {
    let units: Vec<u16> = text.encode_utf16().map(upper_unit).collect();
    String::from_utf16_lossy(&units)
}

/// Название сущности: обрезка пробелов, непустое, не длиннее `max_length` единиц UTF-16.
pub fn name(value: Option<&str>, field: &str, max_length: usize) -> Result<String> {
    let trimmed = value.map(str::trim).unwrap_or("");
    if trimmed.is_empty() {
        return Err(TaskerError::validation(format!("{field} is required")));
    }
    if utf16_len(trimmed) > max_length {
        return Err(TaskerError::validation(format!("{field} must be at most {max_length} characters")));
    }
    Ok(trimmed.to_string())
}

pub fn entity_name(value: Option<&str>, field: &str) -> Result<String> {
    name(value, field, MAX_NAME_LENGTH)
}

/// Имя поля: только буквы и цифры (категории L*, Nd как `char.IsLetterOrDigit`) и несоединённые знаки (Mn).
pub fn field_name(value: Option<&str>, field: &str) -> Result<String> {
    let name = entity_name(value, field)?;
    if !is_field_name(&name) {
        return Err(TaskerError::validation(format!(
            "{field} must consist of letters and digits only (no spaces or symbols such as _ - = ! < > :): '{name}'"
        )));
    }
    Ok(name)
}

/// `char.IsLetterOrDigit(c) || category == NonSpacingMark` по единицам UTF-16: знак вне BMP — две суррогатные
/// единицы, у которых категории Cs, то есть «не буква».
pub fn is_field_name(name: &str) -> bool {
    !name.is_empty()
        && name.chars().all(|c| {
            (c as u32) <= 0xFFFF
                && matches!(
                    get_general_category(c),
                    GeneralCategory::UppercaseLetter
                        | GeneralCategory::LowercaseLetter
                        | GeneralCategory::TitlecaseLetter
                        | GeneralCategory::ModifierLetter
                        | GeneralCategory::OtherLetter
                        | GeneralCategory::DecimalNumber
                        | GeneralCategory::NonspacingMark
                )
        })
}

/// `char.IsLetterOrDigit` для одного знака Unicode (категории L* и Nd); знаки вне BMP — false, как в .NET для суррогатов.
pub fn is_letter_or_digit(c: char) -> bool {
    (c as u32) <= 0xFFFF
        && matches!(
            get_general_category(c),
            GeneralCategory::UppercaseLetter
                | GeneralCategory::LowercaseLetter
                | GeneralCategory::TitlecaseLetter
                | GeneralCategory::ModifierLetter
                | GeneralCategory::OtherLetter
                | GeneralCategory::DecimalNumber
        )
}

/// Описание: пустое или из одних пробелов — None.
pub fn description(value: Option<&str>) -> Option<String> {
    value.filter(|v| !v.trim().is_empty()).map(str::to_string)
}

/// Цвет `#RRGGBB` → заглавные hex.
pub fn color(value: Option<&str>) -> Result<String> {
    match value {
        Some(v) if v.len() == 7 && v.starts_with('#') && v[1..].bytes().all(|b| b.is_ascii_hexdigit()) => Ok(v.to_ascii_uppercase()),
        _ => Err(TaskerError::validation("Color must be in #RRGGBB format")),
    }
}

pub fn distinct(ids: &[Uuid], field: &str) -> Result<Vec<Uuid>> {
    let mut seen = std::collections::HashSet::new();
    if ids.iter().any(|id| !seen.insert(*id)) {
        return Err(TaskerError::validation(format!("{field} contains duplicates")));
    }
    Ok(ids.to_vec())
}

pub fn all_known<'a>(ids: impl IntoIterator<Item = &'a Uuid>, known: &[Uuid], field: &str) -> Result<()> {
    let mut missing: Vec<String> = Vec::new();
    for id in ids {
        if !known.contains(id) && !missing.contains(&crate::ids::guid_d(id)) {
            missing.push(crate::ids::guid_d(id));
        }
    }
    if missing.is_empty() {
        Ok(())
    } else {
        Err(TaskerError::validation(format!(
            "{field}: not found in the project: {}",
            missing.join(", ")
        )))
    }
}

/// Имя пользователя: 3–50 знаков `[A-Za-z0-9._-]`.
pub fn username(value: Option<&str>) -> Result<String> {
    let trimmed = value.map(str::trim).unwrap_or("");
    let ok = (3..=50).contains(&trimmed.len())
        && trimmed
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'));
    if ok {
        Ok(trimmed.to_string())
    } else {
        Err(TaskerError::validation(
            "Username must be 3-50 characters: latin letters, digits, '.', '_' or '-'",
        ))
    }
}

pub fn email(value: Option<&str>) -> Result<String> {
    let trimmed = value.map(str::trim).unwrap_or("");
    let at = trimmed.find('@');
    let ok = utf16_len(trimmed) <= 254
        && at.is_some_and(|i| i > 0)
        && !trimmed.chars().any(char::is_whitespace)
        && trimmed.matches('@').count() == 1
        && trimmed[at.unwrap() + 1..].contains('.')
        && !trimmed.ends_with('.')
        && !trimmed[at.unwrap() + 1..].starts_with('.');
    if ok {
        Ok(trimmed.to_string())
    } else {
        Err(TaskerError::validation("Email is not valid"))
    }
}

pub fn password(value: Option<&str>) -> Result<String> {
    match value {
        Some(v) if utf16_len(v) >= MIN_PASSWORD_LENGTH => Ok(v.to_string()),
        _ => Err(TaskerError::validation(format!(
            "Password must be at least {MIN_PASSWORD_LENGTH} characters"
        ))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_trimmed_and_limited_in_utf16_units() {
        assert_eq!(entity_name(Some("  Alpha  "), "Name").unwrap(), "Alpha");
        assert_eq!(entity_name(Some("   "), "Name").unwrap_err().message(), "Name is required");
        let emoji = "🚀".repeat(51); // 102 единицы UTF-16, 51 знак
        assert_eq!(
            entity_name(Some(&emoji), "Name").unwrap_err().message(),
            "Name must be at most 100 characters"
        );
        assert!(entity_name(Some(&"🚀".repeat(50)), "Name").is_ok());
    }

    #[test]
    fn field_names_allow_letters_digits_and_combining_marks_only() {
        assert!(is_field_name("StoryPoints"));
        assert!(is_field_name("Ожидание"));
        assert!(is_field_name("e\u{0301}"));
        assert!(!is_field_name("Story Points"));
        assert!(!is_field_name("a_b"));
        assert!(!is_field_name("x²")); // No — не Nd, как у .NET
        assert!(!is_field_name("🚀")); // суррогатная пара
        assert_eq!(
            field_name(Some("Story Points"), "Field name").unwrap_err().message(),
            "Field name must consist of letters and digits only (no spaces or symbols such as _ - = ! < > :): 'Story Points'"
        );
    }

    #[test]
    fn invariant_case_mapping_is_per_unit() {
        assert_eq!(to_lower_invariant("İSTANBUL ß Ü"), "istanbul ß ü");
        assert_eq!(to_upper_invariant("straße"), "STRAßE");
        assert!(eq_ignore_case("Relates To", "relates to"));
        assert!(!eq_ignore_case("ß", "SS"));
    }

    #[test]
    fn colors_usernames_emails() {
        assert_eq!(color(Some("#1e90ff")).unwrap(), "#1E90FF");
        assert!(color(Some("1E90FF")).is_err());
        assert_eq!(username(Some(" bob.the-2nd_user ")).unwrap(), "bob.the-2nd_user");
        assert!(username(Some("Боб")).is_err());
        assert!(email(Some("a@b.c")).is_ok());
        assert!(email(Some("a b@c.d")).is_err());
    }
}
