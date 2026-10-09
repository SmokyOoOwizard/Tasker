//! Справка в формате System.CommandLine (`HelpBuilder` 2.0) — слово в слово и пробел в пробел как у .NET-консоли:
//! разделы `Description:`, `Usage:`, `Arguments:`, `Options:`, `Commands:`, две колонки с выравниванием по самой длинной строке
//! раздела, `[default: …]`, `(REQUIRED)`, подсказки значений `<a|b>` из области; перенос строк по ширине окна, когда вывод идёт
//! в терминал (при перенаправлении — без переноса). Эталоны — `rust/tests/golden/expected/cli/*help-*`.
//!
//! Здесь же — подсказки при опечатке (`TypoCorrection`: расстояние Левенштейна не больше трёх) и тексты ошибок разбора.
use crate::spec::{ArgSpec, Arity, CommandSpec, GLOBAL_OPTIONS, HELP_OPTION, Hint, OptKind, OptSpec, VERSION_OPTION};

const INDENT: &str = "  ";

/// Источник подсказок значений для справки: имена сущностей текущей области ([`crate::hints`]).
pub type Resolve<'a> = dyn FnMut(Hint) -> Vec<String> + 'a;

/// Текст справки команды по пути от корня (`path[0]` — корень).
pub fn render(path: &[&CommandSpec], resolve: &mut Resolve<'_>, max_width: usize) -> String {
    let command = path.last().expect("a command");
    let mut out = String::new();
    write_heading(&mut out, "Description:", Some(command.description), max_width);
    write_heading(&mut out, "Usage:", Some(&usage(path)), max_width);

    let arguments: Vec<(String, String)> = command
        .arguments
        .iter()
        .map(|a| (argument_label(a), a.description.to_string()))
        .collect();
    if !arguments.is_empty() {
        write_section(&mut out, "Arguments:", &arguments, max_width);
    }

    let options: Vec<(String, String)> = options_of(path)
        .into_iter()
        .map(|o| (option_label(o, resolve), option_description(o)))
        .collect();
    if !options.is_empty() {
        write_section(&mut out, "Options:", &options, max_width);
    }

    let commands: Vec<(String, String)> = command
        .subcommands
        .iter()
        .map(|c| (command_label(c), c.description.to_string()))
        .collect();
    if !commands.is_empty() {
        write_section(&mut out, "Commands:", &commands, max_width);
    }
    out
}

/// Параметры в разделе `Options:`: у корня — общие, затем `--help` и `--version` (так их объявляет `RootCommand`); у команды —
/// свои (без скрытых), затем `--help` и общие параметры корня (рекурсивные).
pub fn options_of<'a>(path: &[&'a CommandSpec]) -> Vec<&'a OptSpec> {
    let command = path.last().expect("a command");
    let mut options: Vec<&OptSpec> = Vec::new();
    if path.len() == 1 {
        options.extend(GLOBAL_OPTIONS.iter());
        options.push(&HELP_OPTION);
        options.push(&VERSION_OPTION);
    } else {
        options.extend(command.options.iter().filter(|o| !o.hidden));
        options.push(&HELP_OPTION);
        options.extend(GLOBAL_OPTIONS.iter());
    }
    options
}

/// Строка `Usage:`: имена команд по пути, их аргументы, `[command]` у групп, `[options]` — всегда (общие параметры есть у всех).
pub fn usage(path: &[&CommandSpec]) -> String {
    let mut parts: Vec<String> = Vec::new();
    for command in path {
        parts.push(command.name.to_string());
        for argument in &command.arguments {
            let name = format!("<{}>", argument.name);
            parts.push(match argument.arity {
                Arity::ExactlyOne => name,
                Arity::ZeroOrOne => format!("[{name}]"),
                Arity::ZeroOrMore => format!("[{name}...]"),
            });
        }
    }
    let command = path.last().expect("a command");
    if command.is_group() {
        parts.push("[command]".into());
    }
    parts.push("[options]".into());
    parts.join(" ")
}

/// Первая колонка аргумента: допустимые значения (`<bash|pwsh|zsh>`), иначе имя.
pub fn argument_label(argument: &ArgSpec) -> String {
    if argument.accepted.is_empty() {
        format!("<{}>", argument.name)
    } else {
        let mut values: Vec<&str> = argument.accepted.to_vec();
        values.sort_unstable();
        format!("<{}>", values.join("|"))
    }
}

/// Первая колонка параметра: имена в порядке System.CommandLine (`-d, --description`), подсказка значения, `(REQUIRED)`.
pub fn option_label(option: &OptSpec, resolve: &mut Resolve<'_>) -> String {
    let mut label = joined_names(option.all_names());
    if let OptKind::Value { hint, required, .. } = option.kind {
        let value = match hint {
            Hint::Name => format!("<{}>", option.id()),
            Hint::None => String::new(),
            Hint::Fixed(values) => {
                let mut values: Vec<String> = values.iter().map(|v| v.to_string()).collect();
                values.sort_unstable();
                format!("<{}>", values.join("|"))
            }
            dynamic => {
                let mut values = resolve(dynamic);
                values.sort_unstable();
                if values.is_empty() {
                    String::new()
                } else {
                    format!("<{}>", values.join("|"))
                }
            }
        };
        if !value.is_empty() {
            label.push(' ');
            label.push_str(&value);
        }
        if required {
            label.push_str(" (REQUIRED)");
        }
    }
    label
}

fn option_description(option: &OptSpec) -> String {
    match option.kind {
        OptKind::Value {
            default: Some(default), ..
        } => format!("{} [default: {default}]", option.description),
        _ => option.description.to_string(),
    }
}

/// Строка подкоманды в разделе `Commands:`: имена и аргументы (`howto, manual <topic>`).
pub fn command_label(command: &CommandSpec) -> String {
    let mut label = joined_names(command.all_names());
    for argument in &command.arguments {
        label.push(' ');
        label.push_str(&argument_label(argument));
    }
    label
}

/// Имена через запятую в порядке System.CommandLine: по префиксу (`-` раньше `--`), затем по имени без префикса, без учёта регистра.
fn joined_names(names: impl Iterator<Item = &'static str>) -> String {
    let mut parts: Vec<(String, String, &'static str)> = names
        .map(|name| {
            let prefix_len = name.len() - name.trim_start_matches(['-', '/']).len();
            let (prefix, alias) = name.split_at(prefix_len);
            (prefix.to_lowercase(), alias.to_lowercase(), name)
        })
        .collect();
    parts.sort_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.cmp(&b.1)));
    parts.dedup_by(|a, b| a.1 == b.1);
    parts.into_iter().map(|(_, _, name)| name).collect::<Vec<_>>().join(", ")
}

fn write_heading(out: &mut String, heading: &str, text: Option<&str>, max_width: usize) {
    out.push_str(heading);
    out.push('\n');
    if let Some(text) = text {
        for part in wrap_text(text, max_width.saturating_sub(INDENT.len()).max(1)) {
            out.push_str(INDENT);
            out.push_str(&part);
            out.push('\n');
        }
    }
    out.push('\n');
}

/// Раздел из двух колонок (`HelpBuilder.WriteColumns`): первая выровнена по самой длинной строке; если обе не помещаются в окно,
/// первая занимает не больше половины, остальное переносится.
fn write_section(out: &mut String, heading: &str, rows: &[(String, String)], max_width: usize) {
    out.push_str(heading);
    out.push('\n');
    let mut first_width = rows.iter().map(|(first, _)| text_length(first)).max().unwrap_or(0);
    let second_max = rows.iter().map(|(_, second)| text_length(second)).max().unwrap_or(0);
    let mut second_width = second_max;
    if first_width + second_max + INDENT.len() * 2 > max_width {
        let first_max = (max_width / 2).saturating_sub(INDENT.len()).max(1);
        if first_width > first_max {
            first_width = rows
                .iter()
                .flat_map(|(first, _)| wrap_text(first, first_max))
                .map(|part| text_length(&part))
                .max()
                .unwrap_or(0);
        }
        second_width = max_width.saturating_sub(first_width + INDENT.len() * 2).max(1);
    }
    for (first, second) in rows {
        let first_parts = wrap_text(first, first_width.max(1));
        let second_parts = wrap_text(second, second_width.max(1));
        let lines = first_parts.len().max(second_parts.len());
        for i in 0..lines {
            let first_part = first_parts.get(i).cloned().unwrap_or_default();
            let second_part = second_parts.get(i).cloned().unwrap_or_default();
            out.push_str(INDENT);
            out.push_str(&first_part);
            if !second_part.trim().is_empty() {
                let padding = first_width.saturating_sub(text_length(&first_part));
                out.extend(std::iter::repeat_n(' ', padding));
                out.push_str(INDENT);
                out.push_str(&second_part);
            }
            out.push('\n');
        }
    }
    out.push('\n');
}

/// Длина в знаках UTF-16, как `string.Length` в .NET.
fn text_length(text: &str) -> usize {
    text.chars().map(char::len_utf16).sum()
}

/// `HelpBuilder.WrapText`: существующие переводы строк сохраняются, длинные строки режутся по последнему пробелу в пределах ширины.
fn wrap_text(text: &str, max_width: usize) -> Vec<String> {
    if text.trim().is_empty() {
        return Vec::new();
    }
    let mut lines = Vec::new();
    for part in text.split('\n') {
        let part = part.strip_suffix('\r').unwrap_or(part);
        let chars: Vec<char> = part.chars().collect();
        if chars.len() <= max_width {
            lines.push(part.to_string());
            continue;
        }
        let mut i = 0;
        while i < chars.len() {
            if chars.len() - i < max_width {
                lines.push(chars[i..].iter().collect());
                break;
            }
            let mut length = None;
            let mut j = 0;
            while j + i < chars.len() && j < max_width {
                if chars[i + j].is_whitespace() {
                    length = Some(j + 1);
                }
                j += 1;
            }
            let length = length.unwrap_or(max_width);
            lines.push(chars[i..i + length].iter().collect());
            i += length;
        }
    }
    lines
}

// ---- опечатки (TypoCorrection) ----

const MAX_LEVENSHTEIN_DISTANCE: usize = 3;

/// Варианты для неизвестного слова: имена и псевдонимы подкоманд, затем параметров команды, с расстоянием Левенштейна не больше
/// трёх, ближайшие первыми (при равном расстоянии — с более длинным общим началом).
pub fn typo_suggestions(path: &[&CommandSpec], token: &str) -> Vec<String> {
    let command = path.last().expect("a command");
    let mut candidates: Vec<&'static str> = Vec::new();
    for sub in &command.subcommands {
        candidates.extend(sub.all_names());
    }
    for option in options_of(path) {
        candidates.extend(option.all_names());
    }
    let mut scored: Vec<(usize, usize, usize, &str)> = candidates
        .into_iter()
        .enumerate()
        .map(|(index, name)| (levenshtein(token, name), starts_with_distance(token, name), index, name))
        .filter(|(distance, ..)| *distance <= MAX_LEVENSHTEIN_DISTANCE)
        .collect();
    scored.sort_by(|a, b| a.0.cmp(&b.0).then_with(|| b.1.cmp(&a.1)).then_with(|| a.2.cmp(&b.2)));
    scored.into_iter().map(|(.., name)| name.to_string()).collect()
}

/// Текст блока подсказок (`'nope' was not matched. Did you mean one of the following?`), пустой — подсказать нечего.
pub fn typo_block(path: &[&CommandSpec], token: &str) -> String {
    let suggestions = typo_suggestions(path, token);
    if suggestions.is_empty() {
        return String::new();
    }
    let mut out = format!("'{token}' was not matched. Did you mean one of the following?\n");
    for suggestion in suggestions {
        out.push_str(&suggestion);
        out.push('\n');
    }
    out.push('\n');
    out
}

fn starts_with_distance(first: &str, second: &str) -> usize {
    first.chars().zip(second.chars()).take_while(|(a, b)| a == b).count()
}

pub fn levenshtein(a: &str, b: &str) -> usize {
    let a: Vec<char> = a.chars().collect();
    let b: Vec<char> = b.chars().collect();
    let mut previous: Vec<usize> = (0..=b.len()).collect();
    let mut current = vec![0; b.len() + 1];
    for (i, ca) in a.iter().enumerate() {
        current[0] = i + 1;
        for (j, cb) in b.iter().enumerate() {
            let cost = usize::from(ca != cb);
            current[j + 1] = (previous[j + 1] + 1).min(current[j] + 1).min(previous[j] + cost);
        }
        std::mem::swap(&mut previous, &mut current);
    }
    previous[b.len()]
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::spec::root;

    fn no_hints(_: Hint) -> Vec<String> {
        Vec::new()
    }

    #[test]
    fn names_are_ordered_by_prefix_then_name() {
        assert_eq!(joined_names(["--help", "-?", "-h"].into_iter()), "-?, -h, --help");
        assert_eq!(joined_names(["--truncate", "--no-wrap"].into_iter()), "--no-wrap, --truncate");
        assert_eq!(joined_names(["manual", "howto"].into_iter()), "howto, manual");
    }

    #[test]
    fn labels_follow_system_commandline() {
        let root = root();
        let create = root.find(&["task", "create"]).unwrap();
        let mut resolve = |hint: Hint| match hint {
            Hint::TaskTypes => vec!["Фича".to_string(), "Баг".to_string(), "R&D".to_string()],
            _ => Vec::new(),
        };
        let labels: Vec<String> = create.options.iter().map(|o| option_label(o, &mut resolve)).collect();
        assert_eq!(labels[0], "--type <R&D|Баг|Фича> (REQUIRED)");
        assert_eq!(labels[1], "--status");
        assert_eq!(labels[2], "-d, --description <description>");
        assert_eq!(labels[6], "--field");
        assert_eq!(labels[7], "--custom-field <custom-field>");
        assert_eq!(
            usage(&[&root, root.find(&["task"]).unwrap(), create]),
            "tasker task create <title> [options]"
        );
        assert_eq!(
            usage(&[&root, root.find(&["manual"]).unwrap()]),
            "tasker manual [<topic>...] [options]"
        );
        assert_eq!(usage(&[&root, root.find(&["whoami"]).unwrap()]), "tasker whoami [<name>] [options]");
        assert_eq!(usage(&[&root]), "tasker [command] [options]");
        assert_eq!(
            command_label(root.find(&["completion"]).unwrap()),
            "completion <bash|powershell|pwsh|zsh>"
        );
        assert_eq!(command_label(root.find(&["manual"]).unwrap()), "howto, manual <topic>");
    }

    #[test]
    fn long_lines_wrap_in_a_terminal_like_help_builder() {
        let root = root();
        let text = render(&[&root, root.find(&["sync"]).unwrap()], &mut no_hints, 60);
        for line in text.lines() {
            assert!(line.chars().count() <= 60, "{line:?}");
        }
        assert!(text.starts_with("Description:\n  Brings the cache of a workspace up to date with its files \n  (after git pull, checkout, merge)\n\nUsage:\n"), "{text}");
        assert_eq!(wrap_text("aaaa bbbb", 4), ["aaaa", " ", "bbbb"]); // как HelpBuilder.WrapText: пробел уходит отдельной строкой
        assert_eq!(wrap_text("aaaaaaaa", 3), ["aaa", "aaa", "aa"]);
        assert_eq!(wrap_text("x\ny", 10), ["x", "y"]);
    }

    #[test]
    fn typo_suggestions_match_system_commandline() {
        let root = root();
        assert_eq!(typo_suggestions(&[&root], "nope"), ["lock", "mcp", "-p"]);
        assert_eq!(levenshtein("kitten", "sitting"), 3);
        assert_eq!(levenshtein("", "abc"), 3);
    }
}
