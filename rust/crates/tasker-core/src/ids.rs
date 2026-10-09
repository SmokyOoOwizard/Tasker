//! Идентификаторы: Guid в формах .NET (`D` — с дефисами, `N` — 32 hex), короткий id (первые 8 знаков),
//! детерминированные id типов связей по умолчанию (`DefaultLinkTypes.IdOf`).
use md5::{Digest as _, Md5};
use uuid::Uuid;

/// `Guid.ToString("D")`: 36 знаков, строчные hex.
pub fn guid_d(id: &Uuid) -> String {
    id.hyphenated().to_string()
}

/// `Guid.ToString("N")`: 32 hex без дефисов.
pub fn guid_n(id: &Uuid) -> String {
    id.simple().to_string()
}

/// `Guid.TryParse`: .NET принимает формы D, N, B (`{…}`), P (`(…)`) и X; Tasker на входе встречает D и N.
pub fn parse_guid(text: &str) -> Option<Uuid> {
    let trimmed = text.trim();
    let inner = trimmed
        .strip_prefix('{')
        .and_then(|s| s.strip_suffix('}'))
        .or_else(|| trimmed.strip_prefix('(').and_then(|s| s.strip_suffix(')')))
        .unwrap_or(trimmed);
    Uuid::try_parse(inner).ok()
}

/// Короткий id (`ShortId` в .NET): первые 8 знаков формы D; поиск по префиксу — от 8 до 32 hex-знаков без дефисов.
pub struct ShortId;

impl ShortId {
    pub const LENGTH: usize = 8;

    pub fn of(id: &Uuid) -> String {
        guid_d(id)[..Self::LENGTH].to_string()
    }

    /// Ключ поиска по префиксу: 8–32 hex без дефисов → строчные с дефисами на местах формы D (после 8, 12, 16, 20 знаков).
    /// Иначе None: это не префикс id (например, ссылка серии или имя).
    pub fn try_key(text: Option<&str>) -> Option<String> {
        let hex = text?.trim();
        if hex.len() < Self::LENGTH || hex.len() > 32 || !hex.chars().all(|c| c.is_ascii_hexdigit()) {
            return None;
        }
        let mut key = String::with_capacity(hex.len() + 4);
        for (i, c) in hex.chars().enumerate() {
            if matches!(i, 8 | 12 | 16 | 20) {
                key.push('-');
            }
            key.push(c.to_ascii_lowercase());
        }
        Some(key)
    }

    pub fn matches(id: &Uuid, key: &str) -> bool {
        guid_d(id).starts_with(key)
    }
}

/// Типы связей по умолчанию: ключ, имя, названия направлений, циклы, иерархия (`DefaultLinkTypes.All`).
pub struct DefaultLinkType {
    pub key: &'static str,
    pub name: &'static str,
    pub outward: &'static str,
    pub inward: &'static str,
    pub allow_cycles: bool,
    pub hierarchical: bool,
}

pub const DEFAULT_LINK_TYPES: [DefaultLinkType; 6] = [
    DefaultLinkType {
        key: "blocks",
        name: "Blocks",
        outward: "blocks",
        inward: "is blocked by",
        allow_cycles: false,
        hierarchical: false,
    },
    DefaultLinkType {
        key: "duplicate",
        name: "Duplicate",
        outward: "duplicates",
        inward: "is duplicated by",
        allow_cycles: true,
        hierarchical: false,
    },
    DefaultLinkType {
        key: "cloners",
        name: "Cloners",
        outward: "clones",
        inward: "is cloned by",
        allow_cycles: true,
        hierarchical: false,
    },
    DefaultLinkType {
        key: "relates",
        name: "Relates",
        outward: "relates to",
        inward: "relates to",
        allow_cycles: true,
        hierarchical: false,
    },
    DefaultLinkType {
        key: "problem",
        name: "Problem/Incident",
        outward: "causes",
        inward: "is caused by",
        allow_cycles: true,
        hierarchical: false,
    },
    DefaultLinkType {
        key: "parent",
        name: "Parent/Child",
        outward: "includes",
        inward: "is part of",
        allow_cycles: false,
        hierarchical: true,
    },
];

pub const PARENT_LINK_TYPE_KEY: &str = "parent";

/// `DefaultLinkTypes.IdOf`: MD5 от `tasker-link-type:<projectId:D>:<key>`, биты версии/варианта как у v3, и — важно —
/// конструктор .NET `Guid(byte[])` читает первые три группы little-endian: `Uuid::from_bytes_le`.
pub fn default_link_type_id(project_id: &Uuid, key: &str) -> Uuid {
    let mut hash: [u8; 16] = Md5::digest(format!("tasker-link-type:{}:{key}", guid_d(project_id)).as_bytes()).into();
    hash[6] = (hash[6] & 0x0F) | 0x30;
    hash[8] = (hash[8] & 0x3F) | 0x80;
    Uuid::from_bytes_le(hash)
}

/// `DefaultLinkTypes.AllowCyclesWhenUnset`: у файлов до формата 7 признака нет — циклы запрещены только у Blocks.
pub fn allow_cycles_when_unset(project_id: &Uuid, type_id: &Uuid) -> bool {
    *type_id != default_link_type_id(project_id, "blocks")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn short_id_is_the_first_eight_characters() {
        let id = Uuid::parse_str("70053344-2907-4f88-b86c-40a39484c33d").unwrap();
        assert_eq!(ShortId::of(&id), "70053344");
    }

    #[test]
    fn prefix_key_rules_match_dotnet() {
        assert_eq!(ShortId::try_key(Some("70053344")).as_deref(), Some("70053344"));
        assert_eq!(ShortId::try_key(Some("  70053344 ")).as_deref(), Some("70053344"));
        assert_eq!(ShortId::try_key(Some("7005334429074F88")).as_deref(), Some("70053344-2907-4f88"));
        assert_eq!(ShortId::try_key(Some("70053344-2907")), None);
        assert_eq!(ShortId::try_key(Some("7005334")), None);
        assert_eq!(ShortId::try_key(Some("7005334g")), None);
        assert_eq!(ShortId::try_key(Some("70053344290744f88b86c40a39484c33d0")), None);
        assert_eq!(ShortId::try_key(Some("")), None);
        assert_eq!(ShortId::try_key(None), None);
        let id = Uuid::parse_str("70053344-2907-4f88-b86c-40a39484c33d").unwrap();
        assert!(ShortId::matches(&id, &ShortId::try_key(Some("700533442907")).unwrap()));
        assert!(ShortId::matches(&id, &ShortId::try_key(Some("70053344290")).unwrap()));
        assert!(!ShortId::matches(&id, &ShortId::try_key(Some("70053345")).unwrap()));
    }

    #[test]
    fn guid_parsing_accepts_dotnet_forms() {
        let id = Uuid::parse_str("70053344-2907-4f88-b86c-40a39484c33d").unwrap();
        assert_eq!(parse_guid("7005334429074f88b86c40a39484c33d"), Some(id));
        assert_eq!(parse_guid("{70053344-2907-4f88-b86c-40a39484c33d}"), Some(id));
        assert_eq!(parse_guid(" 70053344-2907-4F88-B86C-40A39484C33D "), Some(id));
        assert_eq!(parse_guid("70053344"), None);
    }
}
