//! Имя файла сущности проекта (`EntityFileNames` в .NET): `<название>-<первые 8 знаков id>.yaml`, например
//! `исправить-вход-3f2a9c1e.yaml`. Название (у задачи — заголовок) делает файл узнаваемым в папке и в диффах, суффикс из id —
//! уникальным: две ветки, где создали сущности с одинаковым названием, получают разные файлы. Настоящий id — поле `id` внутри
//! файла; имя только подсказывает, где искать ([`try_parse`]), и проверяется при чтении.
//!
//! Файлы старого формата (задачи — до версии формата 2, остальные сущности — до версии 5) называются по полному Guid:
//! `<guid>.yaml`. Их читают наравне с новыми, при записи они получают новое имя, а `tasker migrate` переименовывает остальные.
use regex::Regex;
use std::sync::OnceLock;
use tasker_core::ids::{guid_n, parse_guid};
use tasker_core::validate::{rune_is_letter_or_digit, to_lower_invariant, utf16_len};
use unicode_normalization::UnicodeNormalization as _;
use uuid::Uuid;

pub const EXTENSION: &str = ".yaml";

/// Сколько знаков id идёт в имя.
pub const ID_LENGTH: usize = 8;

/// Не больше стольких единиц UTF-16 названия: имена файлов не должны упираться в ограничения файловых систем.
pub const MAX_SLUG_LENGTH: usize = 60;

fn stem_pattern() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^(?P<slug>.+)-(?P<id>[0-9a-f]{8})$").expect("regex"))
}

/// Первые знаки id в нижнем регистре, без дефисов — как в имени файла.
pub fn id_prefix(id: &Uuid) -> String {
    guid_n(id)[..ID_LENGTH].to_string()
}

/// Название для имени файла: буквы и цифры (любого алфавита, кириллица остаётся) в нижнем регистре, всё остальное — один дефис
/// между словами. Знаки, недопустимые в именах файлов (`/ \ : * ? " < > |`), в слова не входят, поэтому результат безопасен на
/// любой системе. Длина считается в единицах UTF-16, как `string.Length`; суррогатная пара не режется пополам.
///
/// `empty_slug` — что подставляется, если в названии нет ни одной буквы или цифры (у каждого вида сущности своё).
pub fn slug(name: Option<&str>, empty_slug: &str) -> String {
    let mut builder = String::new();
    let mut pending_dash = false;
    let mut length = 0;

    for rune in name.unwrap_or("").nfc() {
        if !rune_is_letter_or_digit(rune) {
            pending_dash = !builder.is_empty();
            continue;
        }

        let text = to_lower_invariant(&rune.to_string());
        let dash = usize::from(pending_dash);
        if length + utf16_len(&text) + dash > MAX_SLUG_LENGTH {
            break;
        }

        if pending_dash {
            builder.push('-');
        }
        builder.push_str(&text);
        length += utf16_len(&text) + dash;
        pending_dash = false;
    }

    // Поэлементное приведение к нижнему регистру не должно оставить текст не в форме NFC (имена сравнивают как NFC).
    if builder.is_empty() {
        empty_slug.to_string()
    } else {
        builder.nfc().collect()
    }
}

/// Имя файла с расширением: `исправить-вход-3f2a9c1e.yaml`.
pub fn file_name(name: Option<&str>, id: &Uuid, empty_slug: &str) -> String {
    format!("{}-{}{EXTENSION}", slug(name, empty_slug), id_prefix(id))
}

/// Что говорит имя файла об id: полный Guid (старое имя) или только первые знаки (новое).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParsedName {
    /// Старое имя `<guid>.yaml`.
    FullId(Uuid),
    /// Новое имя `<slug>-<id8>.yaml`: первые 8 знаков id.
    IdPrefix(String),
}

impl ParsedName {
    pub fn full_id(&self) -> Option<Uuid> {
        match self {
            Self::FullId(id) => Some(*id),
            Self::IdPrefix(_) => None,
        }
    }

    pub fn id_prefix(&self) -> Option<&str> {
        match self {
            Self::FullId(_) => None,
            Self::IdPrefix(prefix) => Some(prefix),
        }
    }
}

/// `EntityFileNames.TryParse`: None — имя не похоже на имя файла сущности.
pub fn try_parse(file_name: &str) -> Option<ParsedName> {
    let stem = file_name.strip_suffix(EXTENSION)?;
    if let Some(id) = parse_guid(stem) {
        return Some(ParsedName::FullId(id));
    }
    let m = stem_pattern().captures(stem)?;
    Some(ParsedName::IdPrefix(m["id"].to_string()))
}

/// Имя без расширения (`WorkspaceLayout.Stem`): расширения нет — имя как есть.
pub fn stem(file_name: &str) -> &str {
    file_name.strip_suffix(EXTENSION).unwrap_or(file_name)
}

/// Совпадает ли имя файла с тем, каким оно должно быть у этой сущности (без учёта формы Unicode: файловые системы её меняют).
pub fn is_current(file_name: &str, name: Option<&str>, id: &Uuid, empty_slug: &str) -> bool {
    file_name.nfc().collect::<String>() == self::file_name(name, id, empty_slug)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slugs_match_the_dotnet_tests_and_the_golden_corpus() {
        for (title, expected) in [
            ("Fix login", "fix-login"),
            ("  Fix   login!!  ", "fix-login"),
            ("Исправить вход", "исправить-вход"),
            ("Ёлка и ЁЖ", "ёлка-и-ёж"),
            ("CI/CD: pipeline (v2)", "ci-cd-pipeline-v2"),
            ("a<b>c:d\"e|f?g*h\\i/j", "a-b-c-d-e-f-g-h-i-j"),
            ("TSK-5 — миграции БД", "tsk-5-миграции-бд"),
            ("日本語のタイトル", "日本語のタイトル"),
            ("Mixed: Привет, world 42", "mixed-привет-world-42"),
            ("", "task"),
            ("   ", "task"),
            ("!!! ???", "task"),
            ("😀 only emoji 😀", "only-emoji"),
            ("CON", "con"),
            // Корпус (`rust/tests/golden/workspace`): имя файла = FileName(title, id).
            ("Ümläute, İstanbul, ß, Ærø, naïve café", "ümläute-İstanbul-ß-ærø-naïve-café"),
            (
                "Кириллица и эмодзи 🚀 ✨, ещё — тире и «ёлочки»",
                "кириллица-и-эмодзи-ещё-тире-и-ёлочки",
            ),
            ("日本語のタイトル и 中文", "日本語のタイトル-и-中文"),
            (
                "[brackets] {braces} & ampersand * star | pipe > gt ! bang % percent @ at `backtick`",
                "brackets-braces-ampersand-star-pipe-gt-bang-percent-at-backt",
            ),
            (
                "A very long title that goes on and on past sixty characters so that the slug of the file name is cut while the title itself stays whole in the file",
                "a-very-long-title-that-goes-on-and-on-past-sixty-characters",
            ),
            (
                "Percent %s and {0} braces and $var and ${env}",
                "percent-s-and-0-braces-and-var-and-env",
            ),
            ("~", "task"),
            ("-", "task"),
            ("---", "task"),
            ("...", "task"),
            ("007", "007"),
            ("0x1F", "0x1f"),
            ("1.5", "1-5"),
            ("1e3", "1e3"),
            // Комбинируемые знаки: разложенная «й» (и + U+0306) собирается в NFC и остаётся буквой.
            ("и\u{0306}", "й"),
            // Одиночный комбинируемый знак (Mn) — не буква.
            ("\u{0301}", "task"),
        ] {
            assert_eq!(slug(Some(title), "task"), expected, "{title:?}");
        }
        assert_eq!(slug(None, "status"), "status");
    }

    #[test]
    fn a_long_title_is_cut_at_a_word_boundary_character_and_never_ends_with_a_dash() {
        let words = vec!["слово"; 30].join(" ");
        let s = slug(Some(&words), "task");
        assert!(utf16_len(&s) <= MAX_SLUG_LENGTH);
        assert!(s.starts_with("слово-слово"));
        assert!(!s.ends_with('-'));
        assert_eq!(utf16_len(&slug(Some(&"x".repeat(500)), "task")), MAX_SLUG_LENGTH);
        // Суррогатные пары не режутся пополам: математическая буква вне BMP — две единицы UTF-16.
        let letters = "𝒜".repeat(100);
        let s = slug(Some(&letters), "task");
        assert!(s.chars().all(rune_is_letter_or_digit));
        assert_eq!(utf16_len(&s), MAX_SLUG_LENGTH);
        assert_eq!(s.chars().count(), 30);
    }

    #[test]
    fn the_file_name_has_the_slug_and_the_first_eight_characters_of_the_id() {
        let id = Uuid::parse_str("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f").unwrap();
        assert_eq!(file_name(Some("Fix login"), &id, "task"), "fix-login-3f2a9c1e.yaml");
        assert_eq!(id_prefix(&id), "3f2a9c1e");
        assert_eq!(file_name(None, &id, "task"), "task-3f2a9c1e.yaml");
    }

    #[test]
    fn names_are_parsed_back_into_a_full_id_or_an_id_prefix_and_other_names_are_rejected() {
        let id = Uuid::new_v4();
        assert_eq!(try_parse(&format!("{}.yaml", id.hyphenated())), Some(ParsedName::FullId(id)));
        assert_eq!(try_parse(&format!("{}.yaml", id.simple())), Some(ParsedName::FullId(id)));
        assert_eq!(
            try_parse("исправить-вход-3f2a9c1e.yaml"),
            Some(ParsedName::IdPrefix("3f2a9c1e".into()))
        );
        assert_eq!(try_parse("a-b-c-0123abcd.yaml"), Some(ParsedName::IdPrefix("0123abcd".into())));
        for name in [
            "notes.yaml",
            "fix-login.yaml",
            "fix-login-3F2A9C1E.yaml",
            "fix-login-3f2a9c1.yaml",
            "fix-login-3f2a9c1e2.yaml",
            "-3f2a9c1e.yaml",
            "fix-login-3f2a9c1e.yml",
            "fix-login-3f2a9c1e.yaml.tmp",
            "fix-login-3f2a9c1e",
            ".yaml",
        ] {
            assert_eq!(try_parse(name), None, "{name}");
        }
    }

    #[test]
    fn a_name_is_current_only_when_it_matches_the_title_and_id_whatever_the_unicode_form() {
        let id = Uuid::new_v4();
        let name = file_name(Some("Найти вход"), &id, "task");
        assert!(is_current(&name, Some("Найти вход"), &id, "task"));
        // Файловая система (macOS) могла разложить «й» на «и» + U+0306.
        let nfd: String = name.nfd().collect();
        assert_ne!(nfd, name);
        assert!(is_current(&nfd, Some("Найти вход"), &id, "task"));
        assert!(is_current(&name, Some("НАЙТИ,  вход!"), &id, "task"));
        assert!(!is_current(&name, Some("Другой заголовок"), &id, "task"));
        assert!(!is_current(&format!("{}.yaml", id.hyphenated()), Some("Найти вход"), &id, "task"));
        assert!(!is_current(&name, Some("Найти вход"), &Uuid::new_v4(), "task"));
    }
}
