//! Spike TSK-127: собственный YAML-эмиттер, повторяющий YamlDotNet 18.1 так, как его настраивает Tasker
//! (`YamlFile.Serializer`: camelCase, OmitNull, многострочные строки литеральным блоком, `WithQuotingNecessaryStrings`,
//! LF, отступ 2, последовательности без отступа, ширина строки не ограничена).
//!
//! Правила выбора стиля скаляра — порт `Emitter.AnalyzeScalar`/`SelectScalarStyle` из YamlDotNet; анализ идёт по
//! UTF-16-единицам, как в .NET: суррогатные пары (эмодзи) считаются «непечатаемыми» и дают двойные кавычки с `\UXXXXXXXX`.
use std::fmt::Write as _;

/// Узел документа: плоская схема файлов .tasker — отображение с ключами-строками, списки, скаляры.
#[derive(Debug, Clone, PartialEq)]
pub enum Node {
    Str(String),
    Int(i64),
    Bool(bool),
    Seq(Vec<Node>),
    Map(Vec<(String, Node)>),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Style {
    Plain,
    SingleQuoted,
    DoubleQuoted,
    Literal,
}

struct Analysis {
    multiline: bool,
    block_plain_allowed: bool,
    single_quoted_allowed: bool,
    block_allowed: bool,
    has_single_quotes: bool,
}

fn is_break(u: u16) -> bool {
    matches!(u, 0x0A | 0x0D | 0x85 | 0x2028 | 0x2029)
}

fn is_printable(u: u16) -> bool {
    u == 0x09 || u == 0x0A || u == 0x0D || (0x20..=0x7E).contains(&u) || u == 0x85 || (0xA0..=0xD7FF).contains(&u) || (0xE000..=0xFFFD).contains(&u)
}

fn is_white_break_or_zero(u: Option<u16>) -> bool {
    matches!(u, None | Some(0) | Some(0x20) | Some(0x09)) || u.is_some_and(is_break)
}

/// Порт `Emitter.AnalyzeScalar` (контекст блочный: flowLevel == 0, не ключ).
fn analyze(value: &str) -> Analysis {
    let units: Vec<u16> = value.encode_utf16().collect();
    if units.is_empty() {
        return Analysis { multiline: false, block_plain_allowed: false, single_quoted_allowed: true, block_allowed: false, has_single_quotes: false };
    }
    let mut block_indicators = value.starts_with("---") || value.starts_with("...");
    let (mut leading_space, mut leading_break, mut trailing_space, mut trailing_break, mut leading_quote) = (false, false, false, false, false);
    let (mut break_space, mut space_break, mut previous_space, mut previous_break) = (false, false, false, false);
    let (mut line_of_spaces, mut lines_of_spaces, mut line_breaks, mut special, mut single_quotes) = (false, false, false, false, false);
    let mut preceded_by_whitespace = true;
    let mut followed_by_whitespace = is_white_break_or_zero(units.get(1).copied());
    for (i, &u) in units.iter().enumerate() {
        let c = char::from_u32(u as u32).unwrap_or('\u{FFFD}');
        if i == 0 {
            if "#,[]{}&*!|>\"%@`'".contains(c) {
                block_indicators = true;
                leading_quote = c == '\'';
                single_quotes |= c == '\'';
            }
            if (c == '?' || c == ':') && followed_by_whitespace {
                block_indicators = true;
            }
            if c == '-' && followed_by_whitespace {
                block_indicators = true;
            }
        } else {
            if c == ':' && followed_by_whitespace {
                block_indicators = true;
            }
            if c == '#' && preceded_by_whitespace {
                block_indicators = true;
            }
            single_quotes |= c == '\'';
        }
        if !special && !is_printable(u) {
            special = true;
        }
        if is_break(u) {
            line_breaks = true;
        }
        let last = i + 1 == units.len();
        if u == 0x20 {
            if i == 0 {
                leading_space = true;
            }
            if last {
                trailing_space = true;
            }
            if previous_break {
                break_space = true;
                line_of_spaces = true;
            }
            previous_space = true;
            previous_break = false;
        } else if is_break(u) {
            if i == 0 {
                leading_break = true;
            }
            if last {
                trailing_break = true;
            }
            if previous_space {
                space_break = true;
            }
            if line_of_spaces {
                lines_of_spaces = true;
            }
            previous_space = false;
            previous_break = true;
        } else {
            previous_space = false;
            previous_break = false;
            line_of_spaces = false;
        }
        preceded_by_whitespace = is_white_break_or_zero(Some(u));
        if !last {
            followed_by_whitespace = is_white_break_or_zero(units.get(i + 2).copied());
        }
    }
    let mut a = Analysis { multiline: line_breaks, block_plain_allowed: true, single_quoted_allowed: true, block_allowed: true, has_single_quotes: single_quotes };
    if leading_space || leading_break || trailing_space || trailing_break || leading_quote {
        a.block_plain_allowed = false;
    }
    if trailing_space {
        a.block_allowed = false;
    }
    if break_space {
        a.block_plain_allowed = false;
        a.single_quoted_allowed = false;
    }
    if space_break || special {
        a.block_plain_allowed = false;
        a.single_quoted_allowed = false;
    }
    if lines_of_spaces {
        a.block_allowed = false;
    }
    if line_breaks {
        a.block_plain_allowed = false;
    }
    if block_indicators {
        a.block_plain_allowed = false;
    }
    a
}

/// `TypeAssigningEventEmitter.SpecialStrings_Pattern` (YAML 1.2): строки, которые без кавычек прочлись бы как null/bool/число.
fn is_special_string(value: &str) -> bool {
    static PATTERN: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let re = PATTERN.get_or_init(|| {
        regex::Regex::new(
            r"^(null|Null|NULL|~|true|True|TRUE|false|False|FALSE|[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+|[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?(\.inf|\.Inf|\.INF)|\.nan|\.NaN|\.NAN|\s(?s:.)*)$",
        )
        .expect("regex")
    });
    // .NET: `$` совпадает и перед завершающим `\n`; `\s.*` без RegexOptions.Singleline не проходит через перевод строки —
    // но такие строки многострочные и всё равно уходят в литеральный блок, так что различие не влияет.
    re.is_match(value)
}

/// Стиль скаляра-значения, как его выберет Tasker + YamlDotNet.
pub fn style_of(value: &str) -> Style {
    let a = analyze(value);
    // MultilineLiteralEmitter: строка с '\n' → Literal; WithQuotingNecessaryStrings → DoubleQuoted; иначе Any → Plain
    // (Any при multiline давал бы Folded, но литерал назначается раньше по '\n'; строки с '\r'/U+2028 без '\n' — редкость).
    let mut style = if value.contains('\n') {
        Style::Literal
    } else if is_special_string(value) {
        Style::DoubleQuoted
    } else if a.multiline {
        Style::DoubleQuoted // Folded без block_allowed — не встречается в наших данных; упрощение spike
    } else {
        Style::Plain
    };
    if style == Style::Plain && !a.block_plain_allowed {
        style = if a.single_quoted_allowed && !a.has_single_quotes { Style::SingleQuoted } else { Style::DoubleQuoted };
    }
    if style == Style::Plain && value.is_empty() {
        style = Style::SingleQuoted; // пустая строка: block_plain_allowed = false → одинарные кавычки ('')
    }
    if style == Style::Literal && !a.block_allowed {
        style = Style::DoubleQuoted;
    }
    style
}

fn write_double_quoted(out: &mut String, value: &str) {
    out.push('"');
    let units: Vec<u16> = value.encode_utf16().collect();
    let mut i = 0;
    while i < units.len() {
        let u = units[i];
        if !is_printable(u) || is_break(u) || u == 0x22 || u == 0x5C {
            out.push('\\');
            match u {
                0x00 => out.push('0'),
                0x07 => out.push('a'),
                0x08 => out.push('b'),
                0x09 => out.push('t'),
                0x0A => out.push('n'),
                0x0B => out.push('v'),
                0x0C => out.push('f'),
                0x0D => out.push('r'),
                0x1B => out.push('e'),
                0x22 => out.push('"'),
                0x5C => out.push('\\'),
                0x85 => out.push('N'),
                0xA0 => out.push('_'),
                0x2028 => out.push('L'),
                0x2029 => out.push('P'),
                _ if u <= 0xFF => write!(out, "x{u:02X}").unwrap(),
                _ if (0xD800..=0xDBFF).contains(&u) => {
                    let low = units.get(i + 1).copied().filter(|l| (0xDC00..=0xDFFF).contains(l)).expect("orphaned high surrogate");
                    let code = 0x10000 + (((u as u32) - 0xD800) << 10) + ((low as u32) - 0xDC00);
                    write!(out, "U{code:08X}").unwrap();
                    i += 1;
                }
                _ => write!(out, "u{u:04X}").unwrap(),
            }
        } else {
            out.push(char::from_u32(u as u32).unwrap());
        }
        i += 1;
    }
    out.push('"');
}

fn write_single_quoted(out: &mut String, value: &str) {
    out.push('\'');
    for c in value.chars() {
        if c == '\'' {
            out.push('\'');
        }
        out.push(c);
    }
    out.push('\'');
}

fn write_literal(out: &mut String, value: &str, indent: usize) {
    out.push('|');
    if value.starts_with(' ') || value.starts_with('\n') {
        out.push('2'); // bestIndent
    }
    if !value.ends_with('\n') {
        out.push('-');
    } else if value.len() >= 2 && value[..value.len() - 1].ends_with('\n') {
        out.push('+');
    }
    out.push('\n');
    let pad = " ".repeat(indent);
    let mut at_line_start = true;
    let chars: Vec<char> = value.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if c == '\r' && chars.get(i + 1) == Some(&'\n') {
            i += 1;
            continue;
        }
        if c == '\n' || c == '\r' || c == '\u{85}' {
            out.push('\n');
            at_line_start = true;
        } else {
            if at_line_start {
                out.push_str(&pad);
            }
            out.push(c);
            at_line_start = false;
        }
        i += 1;
    }
}

/// Записывает скаляр-значение после `key:` (с ведущим пробелом) — так, как YamlDotNet. `indent` — отступ блока литерала.
/// Возвращает true, если строка уже завершена переводом строки: у литерального блока завершающие переводы строки значения
/// (`|`, `|+`) служат и концом строки документа, отдельный `\n` после них не пишется.
pub fn write_scalar(out: &mut String, value: &str, indent: usize) -> bool {
    out.push(' ');
    match style_of(value) {
        Style::Plain => out.push_str(value),
        Style::SingleQuoted => write_single_quoted(out, value),
        Style::DoubleQuoted => write_double_quoted(out, value),
        Style::Literal => {
            write_literal(out, value, indent);
            return value.ends_with('\n');
        }
    }
    false
}

/// Документ целиком: отображение верхнего уровня. Списки под ключом без отступа (как `IndentSequences = false`),
/// элемент-отображение начинается на строке с дефисом, остальные его ключи — с отступом 2.
pub fn emit(root: &Node) -> String {
    let mut out = String::new();
    match root {
        Node::Map(entries) => write_map(&mut out, entries, 0, false),
        other => panic!("root must be a mapping, got {other:?}"),
    }
    out
}

fn write_map(out: &mut String, entries: &[(String, Node)], indent: usize, mut first_on_dash_line: bool) {
    for (key, value) in entries {
        if first_on_dash_line {
            first_on_dash_line = false;
        } else {
            out.push_str(&" ".repeat(indent));
        }
        out.push_str(key);
        out.push(':');
        match value {
            Node::Str(s) => {
                if !write_scalar(out, s, indent + 2) {
                    out.push('\n');
                }
            }
            Node::Int(n) => {
                let _ = writeln!(out, " {n}");
            }
            Node::Bool(b) => {
                let _ = writeln!(out, " {b}");
            }
            Node::Seq(items) => {
                out.push('\n');
                for item in items {
                    out.push_str(&" ".repeat(indent));
                    out.push('-');
                    match item {
                        Node::Map(inner) => {
                            out.push(' ');
                            write_map(out, inner, indent + 2, true);
                        }
                        Node::Str(s) => {
                            if !write_scalar(out, s, indent + 2) {
                                out.push('\n');
                            }
                        }
                        Node::Int(n) => {
                            let _ = writeln!(out, " {n}");
                        }
                        Node::Bool(b) => {
                            let _ = writeln!(out, " {b}");
                        }
                        Node::Seq(_) => panic!("nested sequences are not used in .tasker files"),
                    }
                }
            }
            Node::Map(inner) => {
                out.push('\n');
                write_map(out, inner, indent + 2, false);
            }
        }
    }
}

/// Преобразование дерева saphyr в Node (для теста parse → emit).
pub fn from_saphyr(yaml: &saphyr::Yaml<'_>) -> Node {
    use saphyr::{Scalar, Yaml};
    match yaml {
        Yaml::Value(Scalar::String(s)) => Node::Str(s.to_string()),
        Yaml::Value(Scalar::Integer(n)) => Node::Int(*n),
        Yaml::Value(Scalar::Boolean(b)) => Node::Bool(*b),
        Yaml::Value(Scalar::Null) => Node::Str(String::new()),
        Yaml::Value(Scalar::FloatingPoint(f)) => Node::Str(f.to_string()),
        Yaml::Representation(s, _, _) => Node::Str(s.to_string()),
        Yaml::Sequence(items) => Node::Seq(items.iter().map(from_saphyr).collect()),
        Yaml::Mapping(map) => Node::Map(
            map.iter()
                .map(|(k, v)| {
                    let key = match k {
                        Yaml::Value(Scalar::String(s)) => s.to_string(),
                        Yaml::Representation(s, _, _) => s.to_string(),
                        other => panic!("non-string key {other:?}"),
                    };
                    (key, from_saphyr(v))
                })
                .collect(),
        ),
        other => panic!("unsupported node {other:?}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use saphyr::LoadableYamlNode;
    use std::path::{Path, PathBuf};

    fn golden() -> PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
    }

    #[derive(serde::Deserialize)]
    struct Probe {
        value: String,
        yaml: String,
        file: String,
    }

    fn probes(name: &str) -> Vec<Probe> {
        serde_json::from_str(&std::fs::read_to_string(golden().join("yaml-style").join(name)).unwrap()).unwrap()
    }

    /// Каждая строка-проба из корпуса: `key:` + скаляр = строка файла, записанная YamlDotNet.
    #[test]
    fn scalars_match_yamldotnet() {
        let mut failures = Vec::new();
        for (name, key) in [("titles.json", "title"), ("descriptions.json", "description")] {
            for probe in probes(name) {
                let mut out = String::from(key);
                out.push(':');
                write_scalar(&mut out, &probe.value, 2);
                // Эталон снят без завершающих переводов строки (rstrip в generate.py).
                let out = out.trim_end_matches('\n').to_string();
                if out != probe.yaml {
                    failures.push(format!("{} {:?}\n  expected: {:?}\n  actual:   {:?}", probe.file, probe.value, probe.yaml, out));
                }
            }
        }
        assert!(failures.is_empty(), "{} mismatches:\n{}", failures.len(), failures.join("\n"));
    }

    /// Все файлы канонической области: parse (saphyr) → emit даёт те же байты. Файлы с CRLF, BOM, конфликтом
    /// и «из будущего» пропускаются: их правил эмиттер не касается (сравнение после нормализации или вовсе не читаются).
    #[test]
    fn workspace_files_round_trip() {
        let root = golden().join("workspace/.tasker");
        let mut checked = 0;
        let mut failures = Vec::new();
        for entry in walkdir(&root) {
            let bytes = std::fs::read(&entry).unwrap();
            if bytes.starts_with(&[0xEF, 0xBB, 0xBF]) || bytes.contains(&b'\r') || bytes.windows(7).any(|w| w == b"<<<<<<<") {
                continue;
            }
            let text = String::from_utf8(bytes).unwrap();
            if text.starts_with("formatVersion: 99") {
                continue;
            }
            let docs = saphyr::Yaml::load_from_str(&text).unwrap();
            let node = from_saphyr(&docs[0]);
            let emitted = emit(&node);
            checked += 1;
            if emitted != text {
                failures.push(format!("{}\n--- expected\n{}--- actual\n{}", entry.display(), text, emitted));
            }
        }
        assert!(checked > 100, "only {checked} files checked");
        assert!(failures.is_empty(), "{} of {} files differ:\n{}", failures.len(), checked, failures.join("\n"));
    }

    fn walkdir(dir: &Path) -> Vec<PathBuf> {
        let mut result = Vec::new();
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                if path.file_name().is_some_and(|n| n == ".cache") {
                    continue;
                }
                result.extend(walkdir(&path));
            } else if path.extension().is_some_and(|e| e == "yaml") {
                result.push(path);
            }
        }
        result.sort();
        result
    }
}
