//! `LineEndings`: файлы пишутся с LF, но git на Windows при `core.autocrlf=true` отдаёт их с CRLF, а редакторы добавляют BOM.
//! Версия файла считается по тексту без BOM и с CRLF→LF, читатели терпят и то и другое.

pub use crate::versioning::bytes_for_hash;

/// Текст без BOM (U+FEFF в начале), если он есть.
pub fn strip_bom(text: &str) -> &str {
    text.strip_prefix('\u{FEFF}').unwrap_or(text)
}

/// CRLF → LF (одиночный CR остаётся).
pub fn to_lf(text: &str) -> std::borrow::Cow<'_, str> {
    if text.contains('\r') {
        std::borrow::Cow::Owned(text.replace("\r\n", "\n"))
    } else {
        std::borrow::Cow::Borrowed(text)
    }
}

/// Окончание строки, которым написан текст: CRLF, если оно в тексте встречается, иначе LF.
pub fn line_ending_of(text: &str) -> &'static str {
    if text.contains("\r\n") { "\r\n" } else { "\n" }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bom_and_line_endings() {
        assert_eq!(strip_bom("\u{FEFF}a"), "a");
        assert_eq!(strip_bom("a\u{FEFF}"), "a\u{FEFF}");
        assert_eq!(to_lf("a\r\nb\rc"), "a\nb\rc");
        assert_eq!(line_ending_of("a\r\nb"), "\r\n");
        assert_eq!(line_ending_of("a\rb\n"), "\n");
        assert_eq!(bytes_for_hash(b"\xEF\xBB\xBFa\r\nb\r").as_ref(), b"a\nb\r");
    }
}
