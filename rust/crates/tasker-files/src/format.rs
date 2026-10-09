//! Версия формата файлов `.tasker` (`FormatVersions` в .NET): число в первой строке `formatVersion: N`. Растёт только когда
//! меняется формат файлов. Tasker читает все версии до [`CURRENT`] (текст приводится к текущему формату в памяти, файл на диске
//! не меняется), пишет всегда [`CURRENT`], файл более новой версии не читает и не пишет.
//!
//! История версий (все шаги — только замена номера, содержимое прежних файлов не меняется):
//! 0 — файлы до введения версии; 1 — `formatVersion: 1`; 2 — имена файлов задач по заголовку; 3 — каталог полей и перечислений;
//! 4 — поля у типов и задач; 5 — имена файлов остальных сущностей по названию; 6 — `fieldFilters` у колонок досок;
//! 7 — `allowCycles` у типов связей; 8 — `description` у статусов и типов задач; 9 — `hierarchical` у типов связей.
use crate::error::{Error, Result};
use regex::Regex;
use std::path::Path;
use std::sync::OnceLock;

pub const CURRENT: u32 = 9;

/// Строка версии на верхнем уровне (без отступа; строки внутри блоков с отступом не считаются). `\r?` — у файлов с окончаниями
/// строк Windows перед концом строки стоит `\r`.
fn version_line() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?m)^formatVersion:[ \t]*(?P<value>[^\r\n]*?)[ \t]*\r?$").expect("regex"))
}

/// Маркеры конфликта git в начале строки (регулярное выражение индекса и `tasker migrate` в .NET).
fn conflict_marker() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?m)^(<{7}|>{7})( |$)|^={7}$").expect("regex"))
}

/// `Path.GetFileName`: последний компонент пути.
pub fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

/// Версия формата из текста файла: 0 — поля нет.
pub fn version_of(text: &str, path: &Path) -> Result<u32> {
    let Some(m) = version_line().captures(text) else {
        return Ok(0);
    };
    let value = &m["value"];
    // int.TryParse: пробелы по краям и знак допускаются; отрицательная версия не принимается.
    match value.trim().parse::<i64>() {
        Ok(v) if (0..=i32::MAX as i64).contains(&v) => Ok(v as u32),
        _ => Err(Error::UnsupportedFormat(format!(
            "{}: formatVersion '{value}' is not a number",
            file_name(path)
        ))),
    }
}

/// Ошибка «файл более нового формата».
pub fn newer(version: u32, path: &Path) -> Error {
    Error::UnsupportedFormat(format!(
        "{}: format version {version} is newer than this Tasker supports ({CURRENT}): update Tasker",
        file_name(path)
    ))
}

/// Приводит текст файла к текущему формату в памяти. Файл на диске не меняется.
pub fn upgrade(text: &str, path: &Path) -> Result<String> {
    let version = version_of(text, path)?;
    if version > CURRENT {
        return Err(newer(version, path));
    }
    // Шаги 0→1 … 8→9 меняют только номер версии; применяются все шаги с номером больше версии файла.
    Ok(if version < CURRENT {
        set_version(text, CURRENT)
    } else {
        text.to_string()
    })
}

/// Выставляет `formatVersion` первой строкой: заменяет имеющуюся строку (сохраняя её `\r`) или добавляет (с окончанием строки
/// самого текста). Остальной текст не трогает.
pub fn set_version(text: &str, version: u32) -> String {
    let line = format!("formatVersion: {version}");
    if version_line().is_match(text) {
        version_line()
            .replacen(text, 1, |m: &regex::Captures<'_>| {
                let cr = if m[0].ends_with('\r') { "\r" } else { "" };
                format!("{line}{cr}")
            })
            .into_owned()
    } else {
        let ending = if text.contains("\r\n") { "\r\n" } else { "\n" };
        format!("{line}{ending}{text}")
    }
}

/// Номер строки (с 1) первого маркера конфликта git, если он есть.
pub fn merge_conflict_line(text: &str) -> Option<usize> {
    conflict_marker().find(text).map(|m| text[..m.start()].matches('\n').count() + 1)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(name: &str) -> &Path {
        Path::new(name)
    }

    #[test]
    fn the_version_is_read_from_the_top_level_field_only() {
        for (text, expected) in [
            ("id: 1\nname: x\n", 0),
            ("formatVersion: 1\nid: 1\n", 1),
            ("formatVersion: 0\nid: 1\n", 0),
            ("formatVersion:   7  \nid: 1\n", 7),
            ("id: 1\r\nformatVersion: 3\r\nname: x\r\n", 3),
            // Строка внутри блока (с отступом) — это текст описания, а не поле файла.
            ("id: 1\ndescription: |\n  formatVersion: 9\n  text\n", 0),
        ] {
            assert_eq!(version_of(text, p("x.yaml")).unwrap(), expected, "{text:?}");
        }
    }

    #[test]
    fn an_unreadable_version_is_reported_as_an_unsupported_format() {
        for text in [
            "formatVersion: abc\nid: 1\n",
            "formatVersion: -1\nid: 1\n",
            "formatVersion:\nid: 1\n",
        ] {
            let error = version_of(text, p("tasks/x.yaml")).unwrap_err();
            assert!(
                matches!(&error, Error::UnsupportedFormat(m) if m.starts_with("x.yaml: formatVersion '")),
                "{error}"
            );
            assert!(error.message().ends_with("' is not a number"), "{error}");
        }
        assert_eq!(
            version_of("formatVersion: abc\n", p("x.yaml")).unwrap_err().message(),
            "x.yaml: formatVersion 'abc' is not a number"
        );
    }

    #[test]
    fn an_old_file_is_upgraded_in_memory_by_adding_the_version_and_nothing_else() {
        let old = "id: 1\nname: Тест\ndescription: |\n  Строка один.\n  Строка два.\n";
        let upgraded = upgrade(old, p("x.yaml")).unwrap();
        assert_eq!(upgraded, format!("formatVersion: {CURRENT}\n{old}"));
        assert_eq!(upgrade(&upgraded, p("x.yaml")).unwrap(), upgraded);
    }

    #[test]
    fn upgrading_keeps_windows_line_endings_and_replaces_an_existing_old_version() {
        assert_eq!(
            upgrade("id: 1\r\n", p("x.yaml")).unwrap(),
            format!("formatVersion: {CURRENT}\r\nid: 1\r\n")
        );
        assert_eq!(
            upgrade("formatVersion: 0\nid: 1\n", p("x.yaml")).unwrap(),
            format!("formatVersion: {CURRENT}\nid: 1\n")
        );
        assert_eq!(
            upgrade("formatVersion: 3\r\nid: 1\r\n", p("x.yaml")).unwrap(),
            format!("formatVersion: {CURRENT}\r\nid: 1\r\n")
        );
        assert_eq!(set_version("formatVersion: 1\nid: 1\n", 5), "formatVersion: 5\nid: 1\n");
        // Текущий формат, записанный иначе, не трогается.
        assert_eq!(
            upgrade("formatVersion:  9 \nid: 1\n", p("x.yaml")).unwrap(),
            "formatVersion:  9 \nid: 1\n"
        );
    }

    #[test]
    fn a_newer_file_is_not_upgraded_but_refused_with_a_hint_to_update() {
        let error = upgrade(&format!("formatVersion: {}\nid: 1\n", CURRENT + 1), p("tasks/abc.yaml")).unwrap_err();
        assert_eq!(
            error.message(),
            format!(
                "abc.yaml: format version {} is newer than this Tasker supports ({CURRENT}): update Tasker",
                CURRENT + 1
            )
        );
    }

    #[test]
    fn merge_conflict_markers_are_found_at_line_start_only() {
        assert_eq!(
            merge_conflict_line("id: 1\n<<<<<<< HEAD\ntitle: a\n=======\ntitle: b\n>>>>>>> x\n"),
            Some(2)
        );
        assert_eq!(merge_conflict_line("id: 1\n=======\n"), Some(2));
        assert_eq!(merge_conflict_line("description: |\n  <<<<<<< not a marker\n"), None);
        assert_eq!(merge_conflict_line("title: <<<<<<< inside\n"), None);
        assert_eq!(merge_conflict_line("<<<<<<<\n"), Some(1));
        assert_eq!(merge_conflict_line("<<<<<<<x\n"), None);
    }
}
