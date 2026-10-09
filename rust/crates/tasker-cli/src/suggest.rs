//! Директива `[suggest]` — то, что вызывают скрипты автодополнения оболочек (`CompleteDirective`, `Completer`, `Sources`,
//! `ShellWords`, `NaturalOrder` в .NET): `tasker "[suggest:ПОЗИЦИЯ]" "строка без имени программы"` печатает варианты по
//! одному в строке. Тихая и ограниченная: любая ошибка — пустой ответ без вывода в stderr, запасные ключи справки (`-?`) не
//! предлагаются, вариантов не больше [`LIMIT`]. Значения берутся из области (`-w`/текущая папка, проект — `-p`,
//! `TASKER_PROJECT` или единственный), которая открывается только если она есть.
use crate::commands::manual::{LANGUAGE_VARIABLE, ManualCatalog};
use crate::session::{Session, refs};
use crate::spec::{self, Arity, CommandSpec, GLOBAL_OPTIONS, HELP_OPTION, OptKind, OptSpec, VERSION_OPTION};
use std::cell::OnceCell;
use std::collections::HashMap;
use std::io::Write;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, FieldType, LinkType, Series, Status, StatusSet, TaskType, UserKind};
use tasker_core::tasks::{Page, TaskFilter};
use tasker_core::validate::{eq_ignore_case, to_lower_invariant};
use tasker_files::index::{IndexEntity, IndexQuery};
use uuid::Uuid;

pub const LIMIT: usize = 50;

/// Дальше просматривать задачи серии дольше, чем живёт дополнение, незачем.
const MAX_SCANNED_TASKS: usize = 5000;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Dialect {
    Posix,
    PowerShell,
}

/// Выполняет директиву: печатает варианты, всегда код 0.
pub fn run(args: &[String], out: &mut dyn Write) -> i32 {
    let directive = args.first().map(String::as_str).unwrap_or_default();
    let text = args.get(1).cloned().unwrap_or_default();
    let inner = directive.trim_start_matches("[suggest").trim_end_matches(']');
    let parts: Vec<&str> = inner.trim_start_matches(':').split(':').collect();
    let dialect = if parts.get(1) == Some(&"pwsh") {
        Dialect::PowerShell
    } else {
        Dialect::Posix
    };
    let position = parts
        .first()
        .and_then(|p| p.parse::<usize>().ok())
        .map(|p| p.min(text.chars().count()))
        .unwrap_or(text.chars().count());
    let before: String = text.chars().take(position).collect();
    let line = normalize(&before, dialect);
    for label in complete(&line) {
        let _ = writeln!(out, "{label}");
    }
    0
}

/// Варианты для разобранной строки (курсор в конце), уже отсортированные и ограниченные.
pub fn complete(line: &str) -> Vec<String> {
    let root = spec::root();
    let tokens = tokenize(line);
    let typing = !line.is_empty() && !line.ends_with(' ');
    let (preceding_tokens, word) = if typing {
        let mut t = tokens.clone();
        let last = t.pop().unwrap_or_default();
        (t, last)
    } else {
        (tokens.clone(), String::new())
    };
    let walk = walk(&root, &preceding_tokens);
    let command = *walk.path.last().expect("a command");
    let hidden: &[&str] = if matches!(command.name, "manual" | "completion") {
        &["--workspace", "-w", "--sqlite", "--project", "-p"]
    } else {
        &[]
    };
    let mut lookup = Lookup::new(&walk, &word);

    let mut all: Vec<String> = Vec::new();
    let dashed = word.starts_with('-');
    match walk.awaiting.filter(|_| !(dashed && walk.collecting)) {
        // Значение параметра: только его источник; у группы команд параметр с одним значением без значения — как без него.
        Some(option) if !command.is_group() || per_token(option) => {
            all.extend(values_of(&mut lookup, Source::of(&walk.path, Target::Option(option.id()))));
        }
        _ => {
            if command.is_group() {
                all.extend(command.subcommands.iter().flat_map(|c| c.all_names().map(str::to_string)));
            }
            if let Some(argument) = current_argument(command, walk.arguments_typed) {
                all.extend(values_of(&mut lookup, Source::of(&walk.path, Target::Argument(argument.name))));
                if !argument.accepted.is_empty() {
                    all.extend(argument.accepted.iter().map(|a| a.to_string()));
                }
            }
            for option in options_around(&walk.path, false) {
                if option.hidden {
                    continue;
                }
                all.extend(option.all_names().map(str::to_string));
            }
        }
    }
    let starts = |x: &str| x.to_lowercase().starts_with(&word.to_lowercase());
    let mut all: Vec<String> = all
        .into_iter()
        .filter(|x| !x.is_empty() && !x.contains('\n') && !is_noise(x) && !hidden.contains(&x.as_str()) && starts(x))
        .collect();
    dedup(&mut all);

    // Параметры предлагаются, когда слово начато с «-» (или предлагать больше нечего): иначе список значений тонет в --json, -w...
    let options: Vec<String> = all.iter().filter(|x| x.starts_with('-')).cloned().collect();
    let rest: Vec<String> = all.iter().filter(|x| !x.starts_with('-')).cloned().collect();
    let mut wanted = if dashed || rest.is_empty() { all } else { rest };
    let _ = options;
    if dashed && !word.contains('=') {
        for option in options_around(&walk.path, true) {
            let used = walk.used.iter().any(|u| std::ptr::eq(*u, option));
            let repeatable = matches!(option.kind, OptKind::Value { multiple: true, .. });
            if option.hidden || (used && !repeatable) {
                continue;
            }
            for name in option.all_names() {
                if name.to_lowercase().starts_with(&word.to_lowercase()) && !is_noise(name) && !hidden.contains(&name) {
                    wanted.push(name.to_string());
                }
            }
        }
        dedup(&mut wanted);
    }
    wanted.sort_by(|a, b| natural_order(a, b));
    wanted.truncate(LIMIT);
    wanted
}

fn is_noise(label: &str) -> bool {
    matches!(label, "/?" | "/h" | "-?")
}

fn dedup(items: &mut Vec<String>) {
    let mut seen: Vec<String> = Vec::new();
    items.retain(|x| {
        if seen.contains(x) {
            false
        } else {
            seen.push(x.clone());
            true
        }
    });
}

fn per_token(option: &OptSpec) -> bool {
    matches!(option.kind, OptKind::Value { per_token: true, .. })
}

fn takes_value(option: &OptSpec) -> bool {
    !matches!(option.kind, OptKind::Flag)
}

/// Параметры команды и всех родительских (общие живут в корне) плюс `--help`; `--version` — только у корня или по запросу.
fn options_around<'a>(path: &[&'a CommandSpec], with_version: bool) -> Vec<&'a OptSpec> {
    let mut result: Vec<&OptSpec> = Vec::new();
    for command in path.iter().rev() {
        result.extend(command.options.iter());
    }
    result.extend(GLOBAL_OPTIONS.iter());
    result.push(&HELP_OPTION);
    if with_version || path.len() == 1 {
        result.push(&VERSION_OPTION);
    }
    result
}

fn current_argument(command: &CommandSpec, typed: usize) -> Option<&spec::ArgSpec> {
    for (index, argument) in command.arguments.iter().enumerate() {
        if argument.arity == Arity::ZeroOrMore || index == typed {
            return Some(argument);
        }
    }
    None
}

/// Разбор строки до курсора по дереву команд: цепочка команд, набранные значения параметров и аргументов, параметр, ждущий значения.
pub struct Walk<'a> {
    pub path: Vec<&'a CommandSpec>,
    pub arguments_typed: usize,
    pub awaiting: Option<&'a OptSpec>,
    /// Параметр с несколькими значениями подряд: следующие слова без дефиса — его значения.
    pub collecting: bool,
    pub used: Vec<&'a OptSpec>,
    pub option_values: HashMap<&'static str, Vec<String>>,
    pub argument_values: HashMap<&'static str, Vec<String>>,
}

fn find_option<'a>(path: &[&'a CommandSpec], name: &str) -> Option<&'a OptSpec> {
    options_around(path, true).into_iter().find(|o| o.all_names().any(|n| n == name))
}

pub fn walk<'a>(root: &'a CommandSpec, tokens: &[String]) -> Walk<'a> {
    let mut walk = Walk {
        path: vec![root],
        arguments_typed: 0,
        awaiting: None,
        collecting: false,
        used: Vec::new(),
        option_values: HashMap::new(),
        argument_values: HashMap::new(),
    };
    for token in tokens {
        let is_option = token.starts_with('-') && token.len() > 1;
        if let Some(option) = walk.awaiting
            && !is_option
        {
            walk.option_values.entry(option.id()).or_default().push(token.clone());
            if !per_token(option) {
                walk.awaiting = None;
                walk.collecting = false;
            }
            continue;
        }
        walk.awaiting = None;
        walk.collecting = false;
        if is_option {
            let (name, inline) = match token.split_once(['=', ':']) {
                Some((name, value)) => (name, Some(value.to_string())),
                None => (token.as_str(), None),
            };
            if let Some(option) = find_option(&walk.path, name) {
                walk.used.push(option);
                if takes_value(option) {
                    match inline {
                        Some(value) => {
                            walk.option_values.entry(option.id()).or_default().push(value);
                        }
                        None => {
                            walk.awaiting = Some(option);
                            walk.collecting = per_token(option);
                        }
                    }
                }
            }
            continue;
        }
        let command = *walk.path.last().expect("a command");
        if walk.arguments_typed == 0
            && let Some(sub) = command.subcommand(token)
        {
            walk.path.push(sub);
            continue;
        }
        if let Some(argument) = current_argument(command, walk.arguments_typed) {
            walk.argument_values.entry(argument.name).or_default().push(token.clone());
        }
        walk.arguments_typed += 1;
    }
    walk
}

/// Строка оболочки → слова (двойные кавычки после [`normalize`]).
pub fn tokenize(line: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut current = String::new();
    let mut in_word = false;
    let mut quoted = false;
    for c in line.chars() {
        if c == '"' {
            quoted = !quoted;
            in_word = true;
        } else if c.is_whitespace() && !quoted {
            if in_word {
                words.push(std::mem::take(&mut current));
            }
            in_word = false;
        } else {
            current.push(c);
            in_word = true;
        }
    }
    if in_word {
        words.push(current);
    }
    words
}

fn is_single_quote(c: char) -> bool {
    matches!(c, '\'' | '\u{2018}' | '\u{2019}' | '\u{201A}' | '\u{201B}')
}

fn is_double_quote(c: char) -> bool {
    matches!(c, '"' | '\u{201C}' | '\u{201D}' | '\u{201E}')
}

/// Строка, набранная в оболочке, в виде для разбора (`ShellWords.Normalize`): каждое слово разбирается по правилам оболочки и
/// записывается заново в двойных кавычках, если в нём есть пробелы; последнее набираемое слово с пробелами остаётся с
/// незакрытой кавычкой.
pub fn normalize(text: &str, dialect: Dialect) -> String {
    let power_shell = dialect == Dialect::PowerShell;
    let escape = if power_shell { '`' } else { '\\' };
    let chars: Vec<char> = text.chars().collect();
    let mut words: Vec<(String, bool)> = Vec::new();
    let mut current = String::new();
    let mut in_word = false;
    let mut quoted = false;
    let mut quote = '\0';
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if quote == '\'' {
            if power_shell && is_single_quote(c) && i + 1 < chars.len() && is_single_quote(chars[i + 1]) {
                i += 1;
                current.push(chars[i]);
            } else if if power_shell { is_single_quote(c) } else { c == '\'' } {
                quote = '\0';
            } else {
                current.push(c);
            }
        } else if quote == '"' {
            if power_shell && is_double_quote(c) && i + 1 < chars.len() && is_double_quote(chars[i + 1]) {
                i += 1;
                current.push(chars[i]);
            } else if if power_shell { is_double_quote(c) } else { c == '"' } {
                quote = '\0';
            } else if (power_shell && c == '`' && i + 1 < chars.len())
                || (!power_shell && c == '\\' && i + 1 < chars.len() && matches!(chars[i + 1], '"' | '\\' | '$' | '`'))
            {
                i += 1;
                current.push(chars[i]);
            } else {
                current.push(c);
            }
        } else if c.is_whitespace() {
            if in_word {
                words.push((std::mem::take(&mut current), quoted));
            }
            current.clear();
            in_word = false;
            quoted = false;
        } else if c == escape {
            if i + 1 < chars.len() {
                i += 1;
                current.push(chars[i]);
            }
            in_word = true;
        } else if if power_shell {
            is_single_quote(c) || is_double_quote(c)
        } else {
            c == '\'' || c == '"'
        } {
            quote = if is_single_quote(c) { '\'' } else { '"' };
            in_word = true;
            quoted = true;
        } else {
            current.push(c);
            in_word = true;
        }
        i += 1;
    }
    let typing = in_word;
    if typing {
        words.push((current.clone(), quoted));
    }

    let mut result = String::new();
    let count = words.len();
    for (i, (word, was_quoted)) in words.iter().enumerate() {
        let needs_quotes = (word.is_empty() && *was_quoted) || word.chars().any(char::is_whitespace);
        let last = typing && i == count - 1;
        if !needs_quotes {
            result.push_str(word);
        } else if last && (quote != '\0' || !was_quoted) {
            result.push('"');
            result.push_str(word);
        } else {
            result.push('"');
            result.push_str(word);
            result.push('"');
        }
        if i < count - 1 {
            result.push(' ');
        }
    }
    if !typing && !text.is_empty() {
        result.push(' ');
    }
    result
}

/// Порядок вариантов (`NaturalOrder`): без учёта регистра, числа по значению (`TSK-2` раньше `TSK-10`).
pub fn natural_order(x: &str, y: &str) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let (a, b): (Vec<char>, Vec<char>) = (x.chars().collect(), y.chars().collect());
    let (mut i, mut j) = (0, 0);
    while i < a.len() && j < b.len() {
        if a[i].is_ascii_digit() && b[j].is_ascii_digit() {
            let (start_a, start_b) = (i, j);
            while i < a.len() && a[i].is_ascii_digit() {
                i += 1;
            }
            while j < b.len() && b[j].is_ascii_digit() {
                j += 1;
            }
            let number_a: String = a[start_a..i].iter().collect::<String>().trim_start_matches('0').to_string();
            let number_b: String = b[start_b..j].iter().collect::<String>().trim_start_matches('0').to_string();
            let by_length = number_a.len().cmp(&number_b.len());
            if by_length != Ordering::Equal {
                return by_length;
            }
            let by_digits = number_a.cmp(&number_b);
            if by_digits != Ordering::Equal {
                return by_digits;
            }
            continue;
        }
        let difference = upper(a[i]).cmp(&upper(b[j]));
        if difference != Ordering::Equal {
            return difference;
        }
        i += 1;
        j += 1;
    }
    (a.len() - i).cmp(&(b.len() - j))
}

/// `char.ToUpperInvariant`: для сравнения достаточно первого символа верхнего регистра.
fn upper(c: char) -> char {
    c.to_uppercase().next().unwrap_or(c)
}

// ---- источники значений (Sources) ----

#[derive(Clone, Copy)]
enum Target {
    Option(&'static str),
    Argument(&'static str),
}

#[derive(Clone, Copy)]
enum Source {
    None,
    Projects,
    Statuses,
    /// Статусы наборов типов, набранных в параметре `--type` (`Sources.TaskStatuses`).
    TaskStatuses,
    StatusSets,
    TaskTypes,
    Series,
    Boards,
    LinkTypes,
    LinkPhrases,
    Fields,
    Enums,
    Users,
    Agents,
    McpWorkspaces,
    FieldTypes,
    HierarchicalLinkTypes,
    SeveralChoices,
    ManualTopics,
    ManualLanguages,
    EntityKinds,
    LockedEntity,
    BoardColumns,
    EnumValues {
        pairs: bool,
    },
    TaskReferences,
    FieldValues,
    SortKeys,
    ColumnSpec,
    ColumnFilters {
        board: bool,
    },
    TypeFields,
    TrueFalse,
}

impl Source {
    /// Источник значений аргумента или параметра команды — как `Suggests(...)` в `Commands/*.cs`.
    fn of(path: &[&CommandSpec], target: Target) -> Source {
        let group = path.get(1).map(|c| c.name).unwrap_or_default();
        let leaf = path.last().map(|c| c.name).unwrap_or_default();
        match target {
            Target::Option("project") => Source::Projects,
            Target::Option(id) => match (group, leaf, id) {
                ("status-set", _, "status") => Source::Statuses,
                ("task-type", _, "status-set") => Source::StatusSets,
                ("task-type", _, "field" | "add-field") => Source::TypeFields,
                ("task-type", _, "remove-field") => Source::Fields,
                ("field", _, "type") => Source::FieldTypes,
                ("field", _, "enum") => Source::Enums,
                ("field", _, "several") => Source::SeveralChoices,
                ("enum", _, "remove-value" | "replace-with") => Source::EnumValues { pairs: false },
                ("enum", _, "rename-value") => Source::EnumValues { pairs: true },
                ("board", _, "status-set") => Source::StatusSets,
                ("board", _, "column") => Source::ColumnSpec,
                ("board", "create", "column-filter") => Source::ColumnFilters { board: false },
                ("board", _, "column-filter") => Source::ColumnFilters { board: true },
                ("board", _, "field") => Source::FieldValues,
                ("board", _, "sort") => Source::SortKeys,
                ("task", _, "type") => Source::TaskTypes,
                ("task", _, "status") => Source::TaskStatuses,
                ("task", _, "series") => Source::Series,
                ("task", _, "parent" | "add-parent" | "remove-parent") => Source::TaskReferences,
                ("task", _, "parent-type") => Source::HierarchicalLinkTypes,
                ("task", _, "field") => Source::FieldValues,
                ("task", _, "add-field" | "remove-field") => Source::Fields,
                ("task", _, "sort") => Source::SortKeys,
                ("manual", _, "lang") => Source::ManualLanguages,
                ("link-type", _, "allow-cycles" | "hierarchical") => Source::TrueFalse,
                _ => Source::None,
            },
            Target::Argument(name) => match (group, leaf, name) {
                ("project", _, "id-or-name") => Source::Projects,
                ("status", _, "id-or-name") => Source::Statuses,
                ("status-set", _, "id-or-name") => Source::StatusSets,
                ("task-type", _, "id-or-name") => Source::TaskTypes,
                ("link-type", _, "id-or-name") => Source::LinkTypes,
                ("field", _, "id-or-name") => Source::Fields,
                ("enum", _, "id-or-name") => Source::Enums,
                ("board", _, "id-or-name") => Source::Boards,
                ("board", _, "column") => Source::BoardColumns,
                ("task", _, "task" | "other-task") => Source::TaskReferences,
                ("task", _, "link") => Source::LinkPhrases,
                ("series", _, "series") => Source::Series,
                ("series", _, "task") => Source::TaskReferences,
                ("user", _, "id-or-name") => Source::Users,
                ("agent", _, "id-or-name") => Source::Agents,
                ("lock", _, "entity") => Source::EntityKinds,
                ("lock", _, "id-or-name") => Source::LockedEntity,
                ("manual", _, "topic") => Source::ManualTopics,
                ("mcp", "remove", "path") => Source::McpWorkspaces,
                _ => Source::None,
            },
        }
    }
}

/// Что известно источнику: область (лениво), разобранная строка и слово под курсором.
struct Lookup<'w, 'a> {
    walk: &'w Walk<'a>,
    word: String,
    folder: Option<String>,
    sqlite: Option<String>,
    project: Option<String>,
    session: OnceCell<Option<Session>>,
    project_id: OnceCell<Option<Uuid>>,
}

impl<'w, 'a> Lookup<'w, 'a> {
    fn new(walk: &'w Walk<'a>, word: &str) -> Lookup<'w, 'a> {
        let value = |id: &str| walk.option_values.get(id).and_then(|v| v.last().cloned());
        Lookup {
            walk,
            word: word.to_string(),
            folder: value("workspace").map(|f| tasker_core::settings::expand_user_path(&f)),
            sqlite: value("sqlite"),
            project: value("project").or_else(|| std::env::var("TASKER_PROJECT").ok()),
            session: OnceCell::new(),
            project_id: OnceCell::new(),
        }
    }

    fn session(&self) -> Option<&Session> {
        self.session
            .get_or_init(|| {
                if self.folder.is_some() && self.sqlite.is_some() {
                    return None;
                }
                Session::open(self.folder.as_deref(), self.sqlite.as_deref(), true).ok()
            })
            .as_ref()
    }

    fn project(&self) -> Option<Uuid> {
        *self
            .project_id
            .get_or_init(|| self.session()?.project_id(self.project.as_deref()).ok())
    }

    fn all<T: IndexEntity>(&self) -> Option<Vec<T>> {
        let project = self.project()?;
        self.session()?.index().all::<T>(&IndexQuery::project(&project)).ok()
    }

    fn argument(&self, name: &str) -> Option<String> {
        self.walk.argument_values.get(name).and_then(|v| v.first().cloned())
    }

    fn option_values(&self, id: &str) -> Vec<String> {
        self.walk.option_values.get(id).cloned().unwrap_or_default()
    }
}

fn values_of(lookup: &mut Lookup<'_, '_>, source: Source) -> Vec<String> {
    try_values(lookup, source).unwrap_or_default()
}

fn names<T: IndexEntity>(lookup: &Lookup<'_, '_>, name: impl Fn(&T) -> String) -> Option<Vec<String>> {
    Some(lookup.all::<T>()?.iter().map(name).collect())
}

fn try_values(lookup: &mut Lookup<'_, '_>, source: Source) -> Option<Vec<String>> {
    let word = lookup.word.clone();
    Some(match source {
        Source::None => return None,
        Source::TrueFalse => vec!["true".into(), "false".into()],
        Source::FieldTypes => vec![
            "string".into(),
            "int".into(),
            "float".into(),
            "bool".into(),
            "date".into(),
            "enum".into(),
        ],
        Source::SeveralChoices => vec!["keep-first".into(), "clear".into()],
        Source::EntityKinds => LOCK_TARGETS.iter().map(|(k, _)| k.to_string()).collect(),
        Source::ManualTopics => {
            let lang = lookup
                .option_values("lang")
                .last()
                .cloned()
                .or_else(|| std::env::var(LANGUAGE_VARIABLE).ok());
            ManualCatalog::embedded()
                .topics(lang.as_deref())
                .into_iter()
                .map(|p| p.id)
                .collect()
        }
        Source::ManualLanguages => ManualCatalog::embedded().languages(),
        Source::McpWorkspaces => tasker_core::settings::SettingsStore::new(None)
            .load()
            .ok()?
            .mcp
            .workspaces
            .iter()
            .map(|w| w.path.clone())
            .collect(),
        Source::Projects => lookup.session()?.all_projects().ok()?.into_iter().map(|p| p.name).collect(),
        Source::Statuses => names::<Status>(lookup, |s| s.name.clone())?,
        Source::StatusSets => names::<StatusSet>(lookup, |s| s.name.clone())?,
        Source::TaskTypes => names::<TaskType>(lookup, |t| t.name.clone())?,
        Source::Series => names::<Series>(lookup, |s| s.prefix.clone())?,
        Source::Boards => names::<Board>(lookup, |b| b.name.clone())?,
        Source::LinkTypes => names::<LinkType>(lookup, |t| t.name.clone())?,
        Source::LinkPhrases => lookup
            .all::<LinkType>()?
            .into_iter()
            .flat_map(|t| [t.name, t.outward_name, t.inward_name])
            .collect(),
        Source::Fields => names::<FieldDefinition>(lookup, |f| f.name.clone())?,
        Source::Enums => names::<FieldEnum>(lookup, |e| e.name.clone())?,
        Source::HierarchicalLinkTypes => lookup
            .all::<LinkType>()?
            .into_iter()
            .filter(|t| t.hierarchical)
            .map(|t| t.name)
            .collect(),
        Source::Users => user_names(lookup, UserKind::Human)?,
        Source::Agents => user_names(lookup, UserKind::Agent)?,
        Source::TaskStatuses => {
            let types = lookup.option_values("type");
            statuses_of_types(lookup, &types)?
        }
        Source::LockedEntity => {
            let kind = lookup.argument("entity")?;
            let (_, source) = LOCK_TARGETS.iter().find(|(k, _)| eq_ignore_case(k, &kind))?;
            return try_values(lookup, *source);
        }
        Source::BoardColumns => {
            let reference = lookup.argument("id-or-name")?;
            let boards = lookup.all::<Board>()?;
            refs::find_item(&boards, &reference, |b| b.id, |b| &b.name, "board", false)
                .ok()?
                .columns
                .iter()
                .map(|c| c.name.clone())
                .collect()
        }
        Source::EnumValues { pairs } => {
            let reference = lookup.argument("id-or-name")?;
            let all = lookup.all::<FieldEnum>()?;
            let values = refs::find_item(&all, &reference, |e| e.id, |e| &e.name, "enum", false).ok()?;
            values
                .values
                .iter()
                .map(|v| if pairs { format!("{}=", v.name) } else { v.name.clone() })
                .collect()
        }
        Source::TaskReferences => task_references(lookup, &word)?,
        Source::FieldValues => field_condition(lookup, &word)?,
        Source::SortKeys => {
            let comma = word.rfind(',').map(|at| at + 1).unwrap_or(0);
            let head = &word[..comma];
            let typed = &word[comma..];
            let used: Vec<String> = head
                .split(',')
                .filter(|k| !k.is_empty())
                .map(|k| to_lower_invariant(k.trim().trim_start_matches('-')))
                .collect();
            let names: Vec<String> = crate::hints::BUILTIN_SORT_NAMES
                .iter()
                .map(|n| n.to_string())
                .chain(lookup.all::<FieldDefinition>()?.into_iter().map(|f| f.name))
                .filter(|n| !used.contains(&to_lower_invariant(n)))
                .collect();
            let keys: Vec<String> = if typed.starts_with('-') {
                names.iter().map(|n| format!("-{n}")).collect()
            } else {
                names.iter().cloned().chain(names.iter().map(|n| format!("-{n}"))).collect()
            };
            keys.into_iter().map(|k| format!("{head}{k}")).collect()
        }
        Source::ColumnSpec => {
            let equals = word.find('=').filter(|at| *at > 0)?;
            let head_end = match word.rfind(',') {
                Some(comma) if comma > 0 => comma + 1,
                _ => equals + 1,
            };
            let head = &word[..head_end];
            lookup.all::<Status>()?.into_iter().map(|s| format!("{head}{}", s.name)).collect()
        }
        Source::ColumnFilters { board } => {
            let mut names: Vec<String> = lookup
                .option_values("column")
                .iter()
                .map(|c| match c.find('=') {
                    Some(eq) if eq > 0 => c[..eq].trim().to_string(),
                    _ => c.trim().to_string(),
                })
                .collect();
            if names.is_empty()
                && board
                && let Some(reference) = lookup.argument("id-or-name")
            {
                let boards = lookup.all::<Board>()?;
                let found = refs::find_item(&boards, &reference, |b| b.id, |b| &b.name, "board", false).ok()?;
                names.extend(found.columns.iter().map(|c| c.name.clone()));
            }
            // Названия колонок могут содержать «:»: условие начинается после самого длинного названия, за которым стоит «:».
            let column = names
                .iter()
                .filter(|n| {
                    let head = format!("{n}:");
                    word.len() >= head.len() && word.is_char_boundary(head.len()) && eq_ignore_case(&word[..head.len()], &head)
                })
                .max_by_key(|n| n.len())
                .cloned();
            match column {
                None => names.iter().map(|n| format!("{n}:")).collect(),
                Some(column) => {
                    let head = word[..column.len() + 1].to_string();
                    field_condition(lookup, &word[head.len()..])?
                        .into_iter()
                        .map(|v| format!("{head}{v}"))
                        .collect()
                }
            }
        }
        Source::TypeFields => match word.rfind(':') {
            None => names::<FieldDefinition>(lookup, |f| f.name.clone())?,
            Some(colon) => vec![format!("{}:required", &word[..colon])],
        },
    })
}

const LOCK_TARGETS: [(&str, Source); 10] = [
    ("project", Source::Projects),
    ("task", Source::TaskReferences),
    ("taskType", Source::TaskTypes),
    ("linkType", Source::LinkTypes),
    ("field", Source::Fields),
    ("enum", Source::Enums),
    ("status", Source::Statuses),
    ("statusSet", Source::StatusSets),
    ("board", Source::Boards),
    ("series", Source::Series),
];

fn user_names(lookup: &Lookup<'_, '_>, kind: UserKind) -> Option<Vec<String>> {
    let page = lookup.session()?.workspace().users().get_range(Some(kind), Page::first(200)).ok()?;
    Some(page.data.into_iter().map(|u| u.username).collect())
}

/// Статусы: только статусы наборов типов, названных в `--type` (не названы — все статусы проекта).
fn statuses_of_types(lookup: &Lookup<'_, '_>, type_references: &[String]) -> Option<Vec<String>> {
    let statuses = lookup.all::<Status>()?;
    if type_references.is_empty() {
        return Some(statuses.into_iter().map(|s| s.name).collect());
    }
    let types = lookup.all::<TaskType>()?;
    let sets = lookup.all::<StatusSet>()?;
    let mut in_set: Vec<Uuid> = Vec::new();
    for reference in type_references {
        let set_id = refs::find_item(&types, reference, |t| t.id, |t| &t.name, "task type", false)
            .ok()?
            .status_set_id;
        in_set.extend(sets.iter().filter(|s| s.id == set_id).flat_map(|s| s.status_ids.iter().copied()));
    }
    Some(if in_set.is_empty() {
        statuses.into_iter().map(|s| s.name).collect()
    } else {
        statuses.into_iter().filter(|s| in_set.contains(&s.id)).map(|s| s.name).collect()
    })
}

/// Ссылки задач (`TSK-7`): до дефиса — префиксы серий с дефисом, после — номера задач серии, начинающиеся с набранных цифр.
fn task_references(lookup: &Lookup<'_, '_>, word: &str) -> Option<Vec<String>> {
    let series = lookup.all::<Series>()?;
    let Some(dash) = word.find('-') else {
        return Some(series.into_iter().map(|s| format!("{}-", s.prefix)).collect());
    };
    let digits = &word[dash + 1..];
    if !digits.bytes().all(|b| b.is_ascii_digit()) {
        return Some(Vec::new());
    }
    let project = lookup.project()?;
    let ws = lookup.session()?.workspace();
    let mut references: Vec<(i32, String)> = Vec::new();
    for found in series.iter().filter(|s| s.prefix == word[..dash]) {
        let filter = TaskFilter {
            series_ids: Some(vec![found.id]),
            ..TaskFilter::default()
        };
        let mut loaded = 0;
        while loaded < MAX_SCANNED_TASKS {
            let page = ws
                .tasks()
                .list(&project, Some(&filter), None, Page::new(loaded, 200), -1, None)
                .ok()?;
            for task in &page.data {
                for number in task
                    .task
                    .series_numbers
                    .iter()
                    .filter(|n| n.series_id == found.id && n.number.to_string().starts_with(digits))
                {
                    references.push((number.number, format!("{}-{}", found.prefix, number.number)));
                }
            }
            loaded += page.data.len();
            if page.data.is_empty() || loaded >= page.total_count {
                break;
            }
        }
    }
    references.sort_by_key(|r| r.0);
    Some(references.into_iter().map(|r| r.1).collect())
}

/// Условие по полю (`--field Имя=значение`) для слова: до знака — имена полей каталога с «=»; после «=»/«!=» — значения поля
/// (перечисление, у логического — true/false); после «:» — слова наличия.
fn field_condition(lookup: &Lookup<'_, '_>, word: &str) -> Option<Vec<String>> {
    let fields = lookup.all::<FieldDefinition>()?;
    let Some(sign) = word.find(['=', '!', '<', '>', ':']) else {
        return Some(fields.into_iter().map(|f| format!("{}=", f.name)).collect());
    };
    let typed = &word[..sign];
    let field = fields.iter().find(|f| eq_ignore_case(&f.name, typed.trim()))?;
    if word[sign..].starts_with(':') {
        return Some(
            ["set", "unset", "attached", "detached"]
                .iter()
                .map(|w| format!("{typed}:{w}"))
                .collect(),
        );
    }
    let prefix = if word[sign..].starts_with("!=") {
        "!="
    } else if word[sign..].starts_with('=') {
        "="
    } else {
        return Some(Vec::new());
    };
    let values: Vec<String> = match (field.field_type, field.enum_id) {
        (FieldType::Bool, _) => vec!["true".into(), "false".into()],
        (FieldType::Enum, Some(enum_id)) => lookup
            .all::<FieldEnum>()?
            .into_iter()
            .filter(|e| e.id == enum_id)
            .flat_map(|e| e.values.into_iter().map(|v| v.name))
            .collect(),
        _ => Vec::new(),
    };
    Some(values.into_iter().map(|v| format!("{typed}{prefix}{v}")).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_words_are_normalized_to_double_quotes() {
        assert_eq!(normalize("task get 'a b'", Dialect::Posix), "task get \"a b\"");
        // Слово с экранированным пробелом набирается без кавычек: оно остаётся с открывающей кавычкой, как у .NET.
        assert_eq!(normalize("task get a\\ b", Dialect::Posix), "task get \"a b");
        // Незакрытая кавычка набираемого слова остаётся: System.CommandLine видит слово целиком.
        assert_eq!(normalize("task get \"a b", Dialect::Posix), "task get \"a b");
        assert_eq!(normalize("task list ", Dialect::Posix), "task list ");
        assert_eq!(normalize("task get 'it''s'", Dialect::PowerShell), "task get it's");
        assert_eq!(tokenize("task get \"a b\" c"), ["task", "get", "a b", "c"]);
    }

    #[test]
    fn natural_order_sorts_numbers_by_value_and_ignores_case() {
        let mut items = vec!["TSK-10", "tsk-2", "task", "--all", "-h", "--help"];
        items.sort_by(|a, b| natural_order(a, b));
        assert_eq!(items, ["--all", "--help", "-h", "task", "tsk-2", "TSK-10"]);
    }

    #[test]
    fn commands_options_and_values_are_completed_from_the_tree() {
        let root = spec::root();
        let w = walk(&root, &["task".into(), "list".into(), "--status".into(), "A".into(), "B".into()]);
        assert_eq!(w.path.last().unwrap().name, "list");
        assert_eq!(w.option_values["status"], ["A", "B"]);
        assert!(w.awaiting.is_some() && w.collecting);

        let all = complete("");
        assert!(all.contains(&"howto".to_string()) && all.iter().all(|x| !x.starts_with('-')));
        assert_eq!(complete("task ")[0], "create");
        let options = complete("task list --");
        assert!(options.contains(&"--version".to_string()) && options.contains(&"--flat".to_string()));
        assert!(!options.contains(&"-h".to_string()));
        assert!(complete("completion ").contains(&"pwsh".to_string()));
    }
}
