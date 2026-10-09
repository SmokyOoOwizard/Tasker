//! Версия сущности — хэш файла: первые 16 hex SHA-256 от байтов без BOM и с CRLF→LF (`YamlFile.Hash`,
//! `LineEndings.ForHash`). Ходит через `--expected-version`, MCP и REST, поэтому считается одинаково в обоих движках.
use crate::error::{ConflictCode, Result, TaskerError};
use sha2::{Digest as _, Sha256};

pub const NEW: &str = "";

const BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

/// `LineEndings.ForHash`: без BOM, `\r\n` → `\n` (одиночный `\r` остаётся).
pub fn bytes_for_hash(bytes: &[u8]) -> std::borrow::Cow<'_, [u8]> {
    let body = bytes.strip_prefix(BOM).unwrap_or(bytes);
    if !body.contains(&b'\r') {
        return std::borrow::Cow::Borrowed(body);
    }
    let mut result = Vec::with_capacity(body.len());
    let mut i = 0;
    while i < body.len() {
        if body[i] == b'\r' && i + 1 < body.len() && body[i + 1] == b'\n' {
            i += 1;
            continue;
        }
        result.push(body[i]);
        i += 1;
    }
    std::borrow::Cow::Owned(result)
}

pub fn version_of(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes_for_hash(bytes));
    let mut hex = String::with_capacity(16);
    for b in &digest[..8] {
        hex.push_str(&format!("{b:02x}"));
    }
    hex
}

pub fn modified(subject: &str) -> TaskerError {
    TaskerError::conflict(
        ConflictCode::Modified,
        format!("{subject} was changed by someone else; reload it and try again"),
    )
}

/// `Versioning.Check`: ожидаемая версия обязательна и должна совпасть с текущей.
pub fn check(current: &str, expected: Option<&str>, subject: &str) -> Result<String> {
    match expected {
        None | Some("") => Err(TaskerError::validation(
            "Version is required: pass the version of the entity you are changing",
        )),
        Some(e) if e.trim().is_empty() => Err(TaskerError::validation(
            "Version is required: pass the version of the entity you are changing",
        )),
        Some(e) if e != current => Err(modified(subject)),
        Some(e) => Ok(e.to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crlf_and_bom_do_not_change_the_version() {
        let lf = b"formatVersion: 9\nid: x\n";
        let crlf = b"formatVersion: 9\r\nid: x\r\n";
        let bom = b"\xEF\xBB\xBFformatVersion: 9\nid: x\n";
        assert_eq!(version_of(lf), version_of(crlf));
        assert_eq!(version_of(lf), version_of(bom));
        assert_eq!(version_of(lf).len(), 16);
    }
}
