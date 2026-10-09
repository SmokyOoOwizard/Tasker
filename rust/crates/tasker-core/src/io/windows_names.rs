//! `WindowsNames`: правила имён файлов Windows — чистые функции без обращения к системе. Файлы `.tasker` попадают в git и
//! открываются на всех системах, так что имя, допустимое на Linux и macOS, но недопустимое на Windows, сломало бы `git clone`.

/// Знаки, которых не может быть в имени файла Windows (управляющие знаки 0–31 проверяются отдельно).
pub const INVALID_CHARS: [char; 9] = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

// Устройства DOS: имя «CON», «con.txt», «CON .txt», «COM1.yaml» зарезервировано, даже с расширением.
const RESERVED: [&str; 30] = [
    "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹",
    "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
];

/// Зарезервированное ли имя: часть до первой точки (без хвостовых пробелов) — имя устройства.
pub fn is_reserved(file_name: &str) -> bool {
    let stem = file_name.split('.').next().unwrap_or("").trim_end_matches(' ');
    RESERVED.iter().any(|r| crate::validate::eq_ignore_case(r, stem))
}

/// Чем имя файла не годится для Windows; `None` — годится на любой системе.
pub fn problem(file_name: &str) -> Option<String> {
    if file_name.is_empty() {
        return Some("the name is empty".into());
    }
    for c in file_name.chars() {
        if c < ' ' {
            return Some(format!("the name has a control character (U+{:04X})", c as u32));
        }
        if INVALID_CHARS.contains(&c) {
            return Some(format!("the name has the character '{c}', which Windows does not allow"));
        }
    }
    if file_name.ends_with(['.', ' ']) {
        return Some("Windows does not allow a name that ends with a dot or a space".into());
    }
    if is_reserved(file_name) {
        return Some("the name is reserved by Windows (CON, PRN, AUX, NUL, COM1-9, LPT1-9)".into());
    }
    None
}

pub fn is_valid(file_name: &str) -> bool {
    problem(file_name).is_none()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reserved_and_invalid_names() {
        assert!(is_reserved("con"));
        assert!(is_reserved("CON .txt"));
        assert!(is_reserved("com1.yaml"));
        assert!(!is_reserved("con1"));
        assert_eq!(
            problem("a:b").as_deref(),
            Some("the name has the character ':', which Windows does not allow")
        );
        assert_eq!(problem("a\u{1}").as_deref(), Some("the name has a control character (U+0001)"));
        assert_eq!(
            problem("a.").as_deref(),
            Some("Windows does not allow a name that ends with a dot or a space")
        );
        assert_eq!(problem(""), Some("the name is empty".into()));
        assert!(is_valid("задача-00000001.yaml"));
    }
}
