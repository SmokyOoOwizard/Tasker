//! Собственный YAML-эмиттер, повторяющий YamlDotNet 18.1 так, как его настраивает Tasker (`YamlFile.Serializer`): camelCase,
//! `OmitNull`, многострочные строки литеральным блоком (`MultilineLiteralEmitter`), `WithQuotingNecessaryStrings`, LF, отступ 2,
//! последовательности под ключом без отступа (`IndentSequences = false`), ширина строки не ограничена (переносов нет), без якорей.
//!
//! Правила выбора стиля скаляра — порт `Emitter.AnalyzeScalar`/`SelectScalarStyle` из YamlDotNet. Анализ идёт по
//! UTF-16-единицам, как в .NET: суррогатные пары (эмодзи) считаются «непечатаемыми» и дают двойные кавычки с `\UXXXXXXXX`.
//! Выбор стиля:
//! - строка с `\n` → литеральный блок: `|-` без завершающего перевода строки, `|` с одним, `|+` с двумя и более, `|2-` (явный
//!   отступ), если текст начинается с пробела или пустой строки; строка из одних пробелов внутри блока или хвостовой пробел →
//!   двойные кавычки с экранированием;
//! - строка, которую без кавычек прочли бы как null, bool или число по YAML 1.2 (`null`, `~`, `true`, `007`, `1.5`, `1e3`, `0x1F`,
//!   `.inf`) → двойные кавычки; `yes`/`no`/`off` — обычный текст;
//! - строки, недопустимые плоскими (ведущие `#`, `-`, `[`, `'`, `"`, `: ` или ` #` внутри, пробелы по краям, `---`, `...`, пустая)
//!   → одинарные кавычки, а если в них есть `'` или непечатаемые знаки — двойные;
//! - иначе — без кавычек.
//!
//! Завершающие переводы строки литерала (`|`, `|+`) служат и концом строки документа: отдельный `\n` после них не пишется.
use std::fmt::Write as _;

/// Узел документа для записи: отображение с ключами-строками, списки, скаляры. Числа и bool пишутся как есть, строки — по
/// правилам выбора стиля. Пустой список — `[]`, пустое отображение — `{}` (как у YamlDotNet).
#[derive(Debug, Clone, PartialEq)]
pub enum Node {
    Str(String),
    Int(i64),
    Bool(bool),
    Seq(Vec<Node>),
    Map(Vec<(String, Node)>),
}

impl Node {
    pub fn str(value: impl Into<String>) -> Node {
        Node::Str(value.into())
    }
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
    u == 0x09
        || u == 0x0A
        || u == 0x0D
        || (0x20..=0x7E).contains(&u)
        || u == 0x85
        || (0xA0..=0xD7FF).contains(&u)
        || (0xE000..=0xFFFD).contains(&u)
}

fn is_white_break_or_zero(u: Option<u16>) -> bool {
    matches!(u, None | Some(0) | Some(0x20) | Some(0x09)) || u.is_some_and(is_break)
}

/// Порт `Emitter.AnalyzeScalar` (блочный контекст: flowLevel == 0, значение, не ключ).
fn analyze(value: &str) -> Analysis {
    let units: Vec<u16> = value.encode_utf16().collect();
    if units.is_empty() {
        return Analysis {
            multiline: false,
            block_plain_allowed: false,
            single_quoted_allowed: true,
            block_allowed: false,
            has_single_quotes: false,
        };
    }
    let mut block_indicators = value.starts_with("---") || value.starts_with("...");
    let (mut leading_space, mut leading_break, mut trailing_space, mut trailing_break, mut leading_quote) =
        (false, false, false, false, false);
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
    let mut a = Analysis {
        multiline: line_breaks,
        block_plain_allowed: true,
        single_quoted_allowed: true,
        block_allowed: true,
        has_single_quotes: single_quotes,
    };
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

/// `TypeAssigningEventEmitter.SpecialStrings_Pattern` (YAML 1.2): строки, которые без кавычек прочлись бы как null, bool или число.
fn is_special_string(value: &str) -> bool {
    static PATTERN: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let re = PATTERN.get_or_init(|| {
        regex::Regex::new(
            r"^(null|Null|NULL|~|true|True|TRUE|false|False|FALSE|[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+|[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?(\.inf|\.Inf|\.INF)|\.nan|\.NaN|\.NAN|\s(?s:.)*)$",
        )
        .expect("regex")
    });
    re.is_match(value)
}

/// Стиль скаляра-значения, как его выберет Tasker + YamlDotNet.
pub fn style_of(value: &str) -> Style {
    let a = analyze(value);
    // MultilineLiteralEmitter: строка с '\n' → Literal; WithQuotingNecessaryStrings → DoubleQuoted; иначе Any → Plain.
    // Строки с переводом строки без '\n' (один '\r', U+2028) многострочны по анализу и уходят в двойные кавычки.
    let mut style = if value.contains('\n') {
        Style::Literal
    } else if is_special_string(value) || a.multiline {
        Style::DoubleQuoted
    } else {
        Style::Plain
    };
    if style == Style::Plain && (!a.block_plain_allowed || value.is_empty()) {
        style = if a.single_quoted_allowed && !a.has_single_quotes {
            Style::SingleQuoted
        } else {
            Style::DoubleQuoted
        };
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
                    // Строка из Rust всегда корректна: за старшим суррогатом следует младший.
                    let low = units[i + 1];
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
        out.push('2'); // явный отступ (bestIndent)
    }
    if !value.ends_with('\n') {
        out.push('-');
    } else if value[..value.len() - 1].ends_with('\n') {
        out.push('+');
    }
    out.push('\n');
    let pad = " ".repeat(indent);
    let mut at_line_start = true;
    let mut chars = value.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\r' && chars.peek() == Some(&'\n') {
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
    }
}

/// Записывает скаляр-строку после `key:` или `-` (с ведущим пробелом) так, как YamlDotNet; `indent` — отступ строк литерального
/// блока. Возвращает true, если строка документа уже завершена переводом строки (литерал `|`/`|+`).
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

/// Документ целиком: отображение верхнего уровня, LF, без `---`.
pub fn emit(root: &[(String, Node)]) -> String {
    let mut out = String::new();
    write_map(&mut out, root, 0, false);
    out
}

/// Значение после `key:` или `- ` вместе с концом строки.
fn write_value(out: &mut String, value: &Node, indent: usize) {
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
        Node::Seq(items) if items.is_empty() => out.push_str(" []\n"),
        Node::Seq(items) => {
            out.push('\n');
            for item in items {
                out.push_str(&" ".repeat(indent));
                out.push('-');
                match item {
                    Node::Map(inner) if !inner.is_empty() => {
                        out.push(' ');
                        write_map(out, inner, indent + 2, true);
                    }
                    Node::Seq(inner) if !inner.is_empty() => {
                        // Вложенный список: первый элемент на строке с дефисом, остальные — с отступом 2.
                        out.push(' ');
                        let mut nested = String::new();
                        write_value(&mut nested, item, indent + 2);
                        out.push_str(nested.trim_start_matches(['\n', ' ']));
                    }
                    other => write_value(out, other, indent),
                }
            }
        }
        Node::Map(entries) if entries.is_empty() => out.push_str(" {}\n"),
        Node::Map(entries) => {
            out.push('\n');
            write_map(out, entries, indent + 2, false);
        }
    }
}

/// Отображение; `first_on_dash_line` — первый ключ пишется на строке с дефисом элемента списка, остальные с отступом.
fn write_map(out: &mut String, entries: &[(String, Node)], indent: usize, mut first_on_dash_line: bool) {
    for (key, value) in entries {
        if first_on_dash_line {
            first_on_dash_line = false;
        } else {
            out.push_str(&" ".repeat(indent));
        }
        // Ключи файлов .tasker — имена полей и Guid; стиль ключа выбирается тем же анализом.
        match style_of(key) {
            Style::Plain => out.push_str(key),
            Style::SingleQuoted => write_single_quoted(out, key),
            _ => write_double_quoted(out, key),
        }
        out.push(':');
        write_value(out, value, indent);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn line(value: &str) -> String {
        let mut out = String::from("k:");
        write_scalar(&mut out, value, 2);
        out
    }

    #[test]
    fn scalar_styles_follow_yamldotnet() {
        assert_eq!(line("plain text"), "k: plain text");
        assert_eq!(line("007"), "k: \"007\"");
        assert_eq!(line("yes"), "k: yes");
        assert_eq!(line("~"), "k: \"~\"");
        assert_eq!(line(""), "k: ''");
        assert_eq!(line("# hash"), "k: '# hash'");
        assert_eq!(line("a: b"), "k: 'a: b'");
        assert_eq!(line("it's: here"), "k: \"it's: here\"");
        assert_eq!(line(" leading"), "k: \" leading\""); // `\s.*` в QuotingNecessaryStrings
        assert_eq!(line("trailing "), "k: 'trailing '");
        assert_eq!(line("tab\tinside"), "k: tab\tinside");
        assert_eq!(line("one\ntwo"), "k: |-\n  one\n  two");
        assert_eq!(line("one\n"), "k: |\n  one\n");
        assert_eq!(line("one\n\n"), "k: |+\n  one\n\n");
        assert_eq!(line(" lead\nx"), "k: |2-\n   lead\n  x");
        assert_eq!(line("a\n \nb"), "k: \"a\\n \\nb\"");
        assert_eq!(line("🚀"), "k: \"\\U0001F680\"");
    }

    #[test]
    fn documents_are_written_like_yamldotnet() {
        let doc = vec![
            ("formatVersion".to_string(), Node::Int(9)),
            ("flag".to_string(), Node::Bool(false)),
            ("empty".to_string(), Node::Seq(vec![])),
            ("none".to_string(), Node::Map(vec![])),
            (
                "list".to_string(),
                Node::Seq(vec![
                    Node::str("a"),
                    Node::Map(vec![("x".into(), Node::Int(1)), ("y".into(), Node::str("b"))]),
                ]),
            ),
            ("map".to_string(), Node::Map(vec![("k".into(), Node::str("v"))])),
        ];
        assert_eq!(
            emit(&doc),
            "formatVersion: 9\nflag: false\nempty: []\nnone: {}\nlist:\n- a\n- x: 1\n  y: b\nmap:\n  k: v\n"
        );
    }
}
