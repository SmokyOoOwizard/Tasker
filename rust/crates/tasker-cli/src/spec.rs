//! Декларативное дерево команд `tasker` — та же структура, что `CliApp.BuildRoot` и `Commands/*.cs` в .NET: имена, псевдонимы,
//! описания, аргументы и параметры в порядке объявления. По нему строится дерево clap ([`CommandSpec::to_clap`]) и печатается
//! справка в формате System.CommandLine ([`crate::help`]); подсказки значений в справке (`<R&D|Баг|Фича>`) — [`Hint`], они
//! берутся из текущей области ([`crate::hints`]).
//!
//! Глобальные параметры (`GlobalOptions`) лежат у корня и принимаются в любом месте строки; у каждой команды в справке они идут
//! после её собственных параметров.
use clap::{Arg, ArgAction, Command};

/// Арность аргумента (`ArgumentArity`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Arity {
    ExactlyOne,
    ZeroOrOne,
    ZeroOrMore,
}

/// Тип значения: число проверяется после разбора с текстом ошибки System.CommandLine.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValueKind {
    Text,
    Int,
}

/// Откуда System.CommandLine берёт подсказку значения параметра в справке (`<a|b>`): источники автодополнения .NET (`Sources`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Hint {
    /// Источника нет: печатается имя параметра — `<name>`.
    Name,
    /// Источник есть, но подсказывать нечего (`NoSuggestions`): подсказки в справке нет вовсе.
    None,
    /// Фиксированный набор значений (печатается отсортированным).
    Fixed(&'static [&'static str]),
    /// Имена проектов области.
    Projects,
    /// Статусы проекта; если в строке набран `--type`, — только статусы наборов этих типов (`Sources.TaskStatuses`).
    Statuses,
    StatusSets,
    TaskTypes,
    /// Префиксы серий (`Sources.Series`).
    SeriesPrefixes,
    /// Ссылки на задачи: без набранного слова — префиксы серий с дефисом (`TSK-`).
    TaskRefs,
    HierarchicalLinkTypes,
    /// Имена полей каталога с `=` (`Sources.FieldValues`).
    FieldEquals,
    /// Имена полей каталога (`Sources.Fields`, `Sources.TypeFields`).
    FieldNames,
    Enums,
    /// Ключи `--sort`: встроенные и имена полей, каждый также с `-`.
    SortKeys,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ArgSpec {
    pub name: &'static str,
    pub description: &'static str,
    pub arity: Arity,
    pub value: ValueKind,
    /// Допустимые значения (`AcceptOnlyFromAmong`): пусто — любые. Единственный случай, когда у аргумента в справке печатаются значения.
    pub accepted: &'static [&'static str],
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OptKind {
    /// `Option<bool>`: флаг без значения.
    Flag,
    /// `Option<bool?>`: флаг со необязательным значением `true`/`false`.
    OptionalBool,
    Value {
        /// Параметр можно повторять (`Option<string[]>`).
        multiple: bool,
        /// Несколько значений после одного параметра (`AllowMultipleArgumentsPerToken`).
        per_token: bool,
        value: ValueKind,
        hint: Hint,
        /// Значение по умолчанию — печатается в справке как `[default: …]`.
        default: Option<&'static str>,
        required: bool,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OptSpec {
    /// Полное имя (`--type`); псевдонимы — в `aliases` (`-d`).
    pub name: &'static str,
    pub aliases: &'static [&'static str],
    pub description: &'static str,
    pub kind: OptKind,
    pub hidden: bool,
}

#[derive(Debug, Clone)]
pub struct CommandSpec {
    pub name: &'static str,
    pub aliases: &'static [&'static str],
    pub description: &'static str,
    pub arguments: Vec<ArgSpec>,
    pub options: Vec<OptSpec>,
    pub subcommands: Vec<CommandSpec>,
}

impl ArgSpec {
    pub const fn new(name: &'static str, description: &'static str) -> ArgSpec {
        ArgSpec {
            name,
            description,
            arity: Arity::ExactlyOne,
            value: ValueKind::Text,
            accepted: &[],
        }
    }

    pub const fn optional(mut self) -> ArgSpec {
        self.arity = Arity::ZeroOrOne;
        self
    }

    pub const fn zero_or_more(mut self) -> ArgSpec {
        self.arity = Arity::ZeroOrMore;
        self
    }

    pub const fn int(mut self) -> ArgSpec {
        self.value = ValueKind::Int;
        self
    }

    pub const fn accept_only(mut self, values: &'static [&'static str]) -> ArgSpec {
        self.accepted = values;
        self
    }

    /// Clap-идентификатор аргумента: его имя.
    pub fn id(&self) -> &'static str {
        self.name
    }
}

impl OptSpec {
    pub const fn flag(name: &'static str, description: &'static str) -> OptSpec {
        OptSpec {
            name,
            aliases: &[],
            description,
            kind: OptKind::Flag,
            hidden: false,
        }
    }

    pub const fn optional_bool(name: &'static str, description: &'static str) -> OptSpec {
        OptSpec {
            name,
            aliases: &[],
            description,
            kind: OptKind::OptionalBool,
            hidden: false,
        }
    }

    /// Параметр с одним значением (`Option<string?>`, `Option<int>`).
    pub const fn value(name: &'static str, description: &'static str) -> OptSpec {
        OptSpec {
            name,
            aliases: &[],
            description,
            kind: OptKind::Value {
                multiple: false,
                per_token: false,
                value: ValueKind::Text,
                hint: Hint::Name,
                default: None,
                required: false,
            },
            hidden: false,
        }
    }

    /// Повторяемый параметр (`Option<string[]>`), по одному значению за раз.
    pub const fn repeated(name: &'static str, description: &'static str) -> OptSpec {
        let mut spec = Self::value(name, description);
        if let OptKind::Value { multiple, .. } = &mut spec.kind {
            *multiple = true;
        }
        spec
    }

    /// `Kit.Many`: повторяемый параметр, принимающий несколько значений подряд.
    pub const fn many(name: &'static str, description: &'static str) -> OptSpec {
        let mut spec = Self::repeated(name, description);
        if let OptKind::Value { per_token, .. } = &mut spec.kind {
            *per_token = true;
        }
        spec
    }

    pub const fn aliases(mut self, aliases: &'static [&'static str]) -> OptSpec {
        self.aliases = aliases;
        self
    }

    pub const fn hint(mut self, new_hint: Hint) -> OptSpec {
        if let OptKind::Value { hint, .. } = &mut self.kind {
            *hint = new_hint;
        }
        self
    }

    pub const fn int(mut self) -> OptSpec {
        if let OptKind::Value { value, .. } = &mut self.kind {
            *value = ValueKind::Int;
        }
        self
    }

    pub const fn default(mut self, text: &'static str) -> OptSpec {
        if let OptKind::Value { default, .. } = &mut self.kind {
            *default = Some(text);
        }
        self
    }

    pub const fn required(mut self) -> OptSpec {
        if let OptKind::Value { required, .. } = &mut self.kind {
            *required = true;
        }
        self
    }

    pub const fn hidden(mut self) -> OptSpec {
        self.hidden = true;
        self
    }

    /// Clap-идентификатор параметра: полное имя без дефисов (`type`).
    pub fn id(&self) -> &'static str {
        self.name.trim_start_matches('-')
    }

    pub fn is_required(&self) -> bool {
        matches!(self.kind, OptKind::Value { required: true, .. })
    }

    /// Все имена: полное и псевдонимы — для подсказок опечаток.
    pub fn all_names(&self) -> impl Iterator<Item = &'static str> {
        std::iter::once(self.name).chain(self.aliases.iter().copied())
    }
}

impl CommandSpec {
    pub fn new(name: &'static str, description: &'static str) -> CommandSpec {
        CommandSpec {
            name,
            aliases: &[],
            description,
            arguments: Vec::new(),
            options: Vec::new(),
            subcommands: Vec::new(),
        }
    }

    fn args(mut self, arguments: impl IntoIterator<Item = ArgSpec>) -> CommandSpec {
        self.arguments.extend(arguments);
        self
    }

    fn opts(mut self, options: impl IntoIterator<Item = OptSpec>) -> CommandSpec {
        self.options.extend(options);
        self
    }

    fn subs(mut self, subcommands: impl IntoIterator<Item = CommandSpec>) -> CommandSpec {
        self.subcommands.extend(subcommands);
        self
    }

    fn with_aliases(mut self, aliases: &'static [&'static str]) -> CommandSpec {
        self.aliases = aliases;
        self
    }

    pub fn is_group(&self) -> bool {
        !self.subcommands.is_empty()
    }

    pub fn all_names(&self) -> impl Iterator<Item = &'static str> {
        std::iter::once(self.name).chain(self.aliases.iter().copied())
    }

    /// Подкоманда по имени или псевдониму.
    pub fn subcommand(&self, name: &str) -> Option<&CommandSpec> {
        self.subcommands.iter().find(|c| c.all_names().any(|n| n == name))
    }

    /// Команда по пути (`["task", "list"]`); пустой путь — сама команда.
    pub fn find(&self, path: &[&str]) -> Option<&CommandSpec> {
        path.iter().try_fold(self, |command, name| command.subcommand(name))
    }

    /// Дерево clap: глобальные параметры корня принимаются у всех команд, справка и `--version` отключены (их рисуем сами),
    /// обязательность параметров и типы значений проверяются после разбора — так же, как System.CommandLine, который при
    /// `--help` их не проверяет.
    pub fn to_clap(&self) -> Command {
        let mut command = Command::new(self.name)
            .disable_help_flag(true)
            .disable_help_subcommand(true)
            .disable_version_flag(true)
            .subcommand_required(false)
            .arg_required_else_help(false)
            .args_conflicts_with_subcommands(false);
        for alias in self.aliases {
            command = command.visible_alias(*alias);
        }
        for (index, argument) in self.arguments.iter().enumerate() {
            let mut arg = Arg::new(argument.id()).index(index + 1).value_name(argument.name);
            arg = match argument.arity {
                Arity::ExactlyOne => arg.required(true).num_args(1),
                Arity::ZeroOrOne => arg.required(false).num_args(0..=1),
                Arity::ZeroOrMore => arg.required(false).num_args(0..).action(ArgAction::Append),
            };
            command = command.arg(arg);
        }
        for option in &self.options {
            command = command.arg(option.clap_arg());
        }
        for subcommand in &self.subcommands {
            command = command.subcommand(subcommand.to_clap());
        }
        command
    }
}

impl OptSpec {
    fn clap_arg(&self) -> Arg {
        let long = self.name.trim_start_matches('-');
        let mut arg = Arg::new(self.id()).long(long).hide(self.hidden);
        for alias in self.aliases {
            arg = match alias.strip_prefix("--") {
                Some(long_alias) => arg.alias(long_alias),
                None => arg.short(alias.chars().nth(1).expect("short alias")),
            };
        }
        match self.kind {
            OptKind::Flag => arg.action(ArgAction::SetTrue),
            OptKind::OptionalBool => arg
                .action(ArgAction::Set)
                .num_args(0..=1)
                .default_missing_value("true")
                .value_name(long),
            OptKind::Value { multiple, per_token, .. } => {
                let arg = arg.value_name(long);
                // Значение с дефисом (`--sort -series`) System.CommandLine принимает: у параметра с одним значением — тоже.
                match (multiple, per_token) {
                    (false, _) => arg.action(ArgAction::Set).num_args(1).allow_hyphen_values(true),
                    (true, false) => arg.action(ArgAction::Append).num_args(1),
                    (true, true) => arg.action(ArgAction::Append).num_args(1..),
                }
            }
        }
    }
}

// ---- глобальные параметры (GlobalOptions) ----

pub const HELP_OPTION: OptSpec = OptSpec {
    name: "--help",
    aliases: &["-?", "-h"],
    description: "Show help and usage information",
    kind: OptKind::Flag,
    hidden: false,
};

pub const VERSION_OPTION: OptSpec = OptSpec {
    name: "--version",
    aliases: &[],
    description: "Show version information",
    kind: OptKind::Flag,
    hidden: false,
};

/// Общие параметры всех команд в порядке `GlobalOptions.AddTo`.
pub const GLOBAL_OPTIONS: [OptSpec; 8] = [
    OptSpec::value(
        "--workspace",
        "Folder with the workspace (data lives in <folder>/.tasker). Default: the current folder",
    )
    .aliases(&["-w"]),
    OptSpec::value("--sqlite", "SQLite file with the workspace instead of a folder"),
    OptSpec::value(
        "--project",
        "Project (id or name) for commands inside a project. Default: the TASKER_PROJECT variable, or the only project of the workspace",
    )
    .aliases(&["-p"])
    .hint(Hint::Projects),
    OptSpec::flag("--json", "Print the result as JSON"),
    OptSpec::flag(
        "--quiet",
        "Print less: lists skip the first line with the number of found items ('Found N'), sync prints nothing unless something needs attention",
    )
    .aliases(&["-q"]),
    OptSpec::flag(
        "--truncate",
        "Cut long lines of list output to the window width with an ellipsis instead of wrapping (default: on in a terminal and under watch - COLUMNS and LINES both set; off when redirected)",
    )
    .aliases(&["--no-wrap"]),
    OptSpec::flag("--no-truncate", "Never cut lines of list output, even in a terminal"),
    OptSpec::value(
        "--width",
        "Line width for --truncate in characters (minimum 20); 0 or 'auto' - the terminal width, 'off' - never cut. Default: the TASKER_WIDTH variable (off or 0 - never cut), else the terminal",
    )
    .hint(Hint::None),
];

pub const ROOT_DESCRIPTION: &str = "Tasker command line: works with a workspace folder (data in <folder>/.tasker) or a SQLite file";

/// Дерево clap всего `tasker` с глобальными параметрами, справкой и `--version` у корня.
pub fn clap_root(spec: &CommandSpec) -> Command {
    let mut command = spec.to_clap().no_binary_name(false);
    command = command.arg(HELP_OPTION.clap_arg().global(true));
    command = command.arg(VERSION_OPTION.clap_arg());
    for option in GLOBAL_OPTIONS {
        command = command.arg(option.clap_arg().global(true));
    }
    command
}

// ---- дерево команд (Commands/*.cs) ----

const ID_OR_NAME: &str = "id-or-name";

fn reference(what: &'static str) -> ArgSpec {
    const DESCRIPTIONS: &[(&str, &str)] = &[
        ("Project", "Project (id or name)"),
        ("Status", "Status (id or name)"),
        ("Status set", "Status set (id or name)"),
        ("Task type", "Task type (id or name)"),
        ("Link type", "Link type (id or name)"),
        ("Field", "Field (id or name)"),
        ("Enum", "Enum (id or name)"),
        ("Board", "Board (id or name)"),
        ("User", "User (id or name)"),
        ("Agent", "Agent (id or name)"),
    ];
    let description = DESCRIPTIONS
        .iter()
        .find(|(kind, _)| *kind == what)
        .map(|(_, d)| *d)
        .unwrap_or_else(|| panic!("no reference description for {what}"));
    ArgSpec::new(ID_OR_NAME, description)
}

const EXPECTED_VERSION: OptSpec = OptSpec::value(
    "--expected-version",
    "Fail if the entity was changed after this version (as shown by get). Default: the current version",
);

const NEW_NAME: OptSpec = OptSpec::value("--name", "New name");

const SORT: OptSpec = OptSpec::value(
    "--sort",
    "Order of the tasks: keys separated by commas, '-' before a key for descending order, e.g. status,-updated,Estimate. Keys: status (position in the task type's status set), type, title, created, updated, series (TSK-9 before TSK-12), or the name of a field (catalog or own field of tasks: numbers by value, dates by date, enum by the order of its values, bool false before true, strings ignoring case; a multiple field - by its first value). Tasks without the value go last in either direction; equal keys keep the default order (creation time). Default: creation order",
)
.hint(Hint::SortKeys);

const TASK_DESCRIPTION_LENGTH: OptSpec = OptSpec::value(
    "--description-length",
    "For --json: how many characters of each task's description to include - 0 none, N the first N characters, -1 the full text (default). The record also has descriptionTruncated, descriptionLength (the full length), linksCount, parentIds and childCount",
)
.int()
.default("-1")
.hint(Hint::None);

fn entity_description_length(what: &'static str) -> OptSpec {
    let description = match what {
        "status" => {
            "For --json: how many characters of each status's description to include - 0 none, N the first N characters, -1 the full text (default). The record also has descriptionTruncated and descriptionLength (the full length)"
        }
        "task type" => {
            "For --json: how many characters of each task type's description to include - 0 none, N the first N characters, -1 the full text (default). The record also has descriptionTruncated and descriptionLength (the full length)"
        }
        _ => panic!("no description-length text for {what}"),
    };
    OptSpec::value("--description-length", description)
        .int()
        .default("-1")
        .hint(Hint::None)
}

/// `Kit.Paging`: `--offset`, `--limit`, `--all`.
fn paging() -> [OptSpec; 3] {
    [
        OptSpec::value("--offset", "How many to skip").int().default("0"),
        OptSpec::value("--limit", "How many to show, 1-200").int().default("50"),
        OptSpec::flag(
            "--all",
            "Show everything, not just a page (cannot be combined with --offset/--limit)",
        ),
    ]
}

fn list(description: &'static str) -> CommandSpec {
    CommandSpec::new("list", description).opts(paging())
}

fn new_description(what: &'static str) -> OptSpec {
    let description = match what {
        "status" => "What the status means and when to use it (free text, may have line breaks)",
        "task type" => "What the task type means and when to use it (free text, may have line breaks)",
        _ => panic!("no description text for {what}"),
    };
    OptSpec::value("--description", description).aliases(&["-d"]).hint(Hint::None)
}

const CHANGED_DESCRIPTION: OptSpec = OptSpec::value("--description", "New description; an empty one clears it")
    .aliases(&["-d"])
    .hint(Hint::None);

const FIELD_TYPES: &[&str] = &["bool", "date", "enum", "float", "int", "string"];

const TASK_REFERENCE: ArgSpec = ArgSpec::new("task", "Task: id or reference like TSK-5");

const TASK_FIELD_VALUES: OptSpec = OptSpec::repeated(
    "--field",
    "Value of a field: Name=value (the field of the task's type, an additional or own field of the task, or a field of the catalog: it becomes an additional field). Repeat the option for several values of a field with several values; Name= clears the values. int, float (with a dot), bool (true/false), date (yyyy-MM-dd); enum: the value name or id",
)
.hint(Hint::FieldEquals);

const TASK_CUSTOM_FIELD: OptSpec = OptSpec::repeated(
    "--custom-field",
    "A field of this task alone: Name:type[:required][:multiple][:enum=Enum][=value], e.g. 'Estimate:int=5', 'Tags:string:multiple=a', 'Risk:enum:required:enum=Priority=High'. Types: string, int, float, bool, date, enum (the enum is a name or id). Repeat the same definition to give several values. Names cannot contain ':' or '='",
);

const TASK_ADD_FIELD: OptSpec = OptSpec::repeated(
    "--add-field",
    "Add a field of the catalog to the task without a value: Name (id or name; repeat for several)",
)
.hint(Hint::FieldNames);

const TASK_REMOVE_FIELD: OptSpec = OptSpec::repeated(
    "--remove-field",
    "Remove an additional or own field of the task with its values: Name (repeat for several); fields of the task's type cannot be removed",
)
.hint(Hint::FieldNames);

const PARENT_TYPE: OptSpec = OptSpec::value(
    "--parent-type",
    "Hierarchical link type for --parent/--add-parent/--remove-parent (id or name); needed only when the project has several",
)
.hint(Hint::HierarchicalLinkTypes);

const COLUMN_HELP: &str = "Column, left to right: \"Name=status,status\" (statuses by id or name). A task moved into the column gets the first of its statuses that belongs to the task type's status set";

const FILTER_HELP: &str = "Field condition of a column, in addition to its statuses (AND): \"Column:Field=value\", \"Column:Field>=3\", \"Column:Field:set\" and so on - the same conditions as 'task list --field' (=, !=, >, >=, <, <=, :set, :unset, :attached, :detached), by the field's name in the catalog (stored by id). Repeat for several conditions; the column is named as in --column. A task is in the column only if its status and all conditions fit";

const BOARD_VIEW_FIELD: OptSpec = OptSpec::many(
    "--field",
    "Show only tasks by a field (catalog or own field of tasks): \"Name=value\", \"Name>=value\", Name:set and so on (as in 'task list --field'); a view filter, the column does not store it",
)
.hint(Hint::FieldEquals);

fn project() -> CommandSpec {
    CommandSpec::new("project", "Projects").subs([
        CommandSpec::new("create", "Creates a project").args([ArgSpec::new("name", "Project name")]),
        list("Lists projects"),
        CommandSpec::new("get", "Shows a project").args([reference("Project")]),
        CommandSpec::new("update", "Changes a project")
            .args([reference("Project")])
            .opts([NEW_NAME, EXPECTED_VERSION]),
        CommandSpec::new("delete", "Deletes a project with everything in it")
            .args([reference("Project")])
            .opts([
                OptSpec::flag("--yes", "Confirm: deleting a project deletes everything in it").aliases(&["-y"]),
                EXPECTED_VERSION,
            ]),
    ])
}

fn status() -> CommandSpec {
    CommandSpec::new("status", "Statuses of a project (Todo, Done, ...)").subs([
        CommandSpec::new("create", "Creates a status")
            .args([ArgSpec::new("name", "Status name")])
            .opts([
                OptSpec::value("--color", "Color in #RRGGBB format").default("#808080"),
                new_description("status"),
            ]),
        list("Lists statuses").opts([entity_description_length("status")]),
        CommandSpec::new("get", "Shows a status").args([reference("Status")]),
        CommandSpec::new("update", "Changes a status").args([reference("Status")]).opts([
            NEW_NAME,
            OptSpec::value("--color", "New color in #RRGGBB format"),
            CHANGED_DESCRIPTION,
            EXPECTED_VERSION,
        ]),
        CommandSpec::new("delete", "Deletes a status (not used by sets, boards or tasks)")
            .args([reference("Status")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn status_set() -> CommandSpec {
    CommandSpec::new("status-set", "Status sets: the ordered statuses a task type goes through").subs([
        CommandSpec::new("create", "Creates a status set")
            .args([ArgSpec::new("name", "Status set name")])
            .opts([
                OptSpec::many("--status", "Statuses of the set in order (id or name); repeat or list several")
                    .hint(Hint::Statuses)
                    .required(),
            ]),
        list("Lists status sets"),
        CommandSpec::new("get", "Shows a status set").args([reference("Status set")]),
        CommandSpec::new("update", "Changes a status set")
            .args([reference("Status set")])
            .opts([
                NEW_NAME,
                OptSpec::many(
                    "--status",
                    "New statuses of the set in order (id or name); replaces the current ones",
                )
                .hint(Hint::Statuses),
                EXPECTED_VERSION,
            ]),
        CommandSpec::new("delete", "Deletes a status set (not used by task types or boards)")
            .args([reference("Status set")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn task_type() -> CommandSpec {
    CommandSpec::new("task-type", "Task types").subs([
        CommandSpec::new("create", "Creates a task type")
            .args([ArgSpec::new("name", "Task type name")])
            .opts([
                OptSpec::value("--status-set", "Status set of the type (id or name)").hint(Hint::StatusSets).required(),
                new_description("task type"),
                OptSpec::repeated(
                    "--field",
                    "Attach a field of the catalog: Name or Name:required (a required field must have a value in every task of the type). Repeat for several; see 'field list'",
                )
                .hint(Hint::FieldNames),
            ]),
        list("Lists task types").opts([entity_description_length("task type")]),
        CommandSpec::new("get", "Shows a task type").args([reference("Task type")]),
        CommandSpec::new("update", "Changes a task type").args([reference("Task type")]).opts([
            NEW_NAME,
            CHANGED_DESCRIPTION,
            OptSpec::value(
                "--status-set",
                "New status set (id or name); its statuses must cover the statuses of the type's tasks",
            )
            .hint(Hint::StatusSets),
            OptSpec::repeated(
                "--field",
                "Replace the whole list of the type's fields: Name or Name:required (repeat for several; the order is kept)",
            )
            .hint(Hint::FieldNames),
            OptSpec::repeated(
                "--add-field",
                "Attach a field, or change whether it is required: Name or Name:required (repeat for several)",
            )
            .hint(Hint::FieldNames),
            OptSpec::repeated(
                "--remove-field",
                "Detach a field (name or id; repeat for several). If tasks have its values, choose --drop-values or --keep-values",
            )
            .hint(Hint::FieldNames),
            OptSpec::flag("--drop-values", "With a detached field: clear its values from the tasks of the type"),
            OptSpec::flag(
                "--keep-values",
                "With a detached field: keep the values, the field becomes an additional field of the tasks that have values",
            ),
            EXPECTED_VERSION,
        ]),
        CommandSpec::new("delete", "Deletes a task type (without tasks)")
            .args([reference("Task type")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn link_type() -> CommandSpec {
    CommandSpec::new(
        "link-type",
        "Link types between tasks (Blocks, Duplicate, Cloners, Relates, Problem/Incident, Parent/Child by default; add your own)",
    )
    .subs([
        CommandSpec::new("create", "Creates a link type")
            .args([ArgSpec::new("name", "Link type name")])
            .opts([
                OptSpec::value("--outward", "How the link reads for the task it starts from, e.g. 'depends on'").required(),
                OptSpec::value(
                    "--inward",
                    "How it reads for the task it points to, e.g. 'is a dependency of'. Omit it for a link without direction ('relates to')",
                ),
                OptSpec::optional_bool(
                    "--allow-cycles",
                    "true or false: whether a chain of links of this type may close a cycle (A blocks B, B blocks A). false rejects such a link; the default is true for your own types (Blocks forbids cycles). Ignored for links without direction",
                ),
                OptSpec::optional_bool(
                    "--hierarchical",
                    "true or false: a parent/child type ('includes' / 'is part of'): the task the link starts from is the parent (an epic), the target its child; 'task list' shows children under the parent, a task may have several parents. Cycles are always rejected, the two names must differ. Default false (Parent/Child is hierarchical)",
                ),
            ]),
        list("Lists link types"),
        CommandSpec::new("get", "Shows a link type").args([reference("Link type")]),
        CommandSpec::new("update", "Changes a link type").args([reference("Link type")]).opts([
            NEW_NAME,
            OptSpec::value("--outward", "New outward name"),
            OptSpec::value("--inward", "New inward name"),
            OptSpec::optional_bool(
                "--allow-cycles",
                "true or false: whether a chain of links of this type may close a cycle. Changing it does not touch existing links (cycles that exist are reported by 'tasker sync' and 'tasker cleanup --check')",
            ),
            OptSpec::optional_bool(
                "--hierarchical",
                "true or false: make the type parent/child (cycles become forbidden, the two names must differ) or an ordinary one. Existing links are not touched",
            ),
            EXPECTED_VERSION,
        ]),
        CommandSpec::new("delete", "Deletes a link type (without links)")
            .args([reference("Link type")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn field() -> CommandSpec {
    CommandSpec::new(
        "field",
        "Fields of the project's catalog: a task type attaches them, tasks hold their values (see 'task-type' and 'task')",
    )
    .subs([
        CommandSpec::new("create", "Creates a field")
            .args([ArgSpec::new("name", "Field name (unique in the project, case-insensitive)")])
            .opts([
                OptSpec::value("--type", "Type of the value: string, int, float, bool, date, enum")
                    .hint(Hint::Fixed(FIELD_TYPES))
                    .required(),
                OptSpec::flag("--multiple", "The field holds several values (a list)"),
                OptSpec::value(
                    "--enum",
                    "Enum of the values (id or name): required for the type enum, not allowed for the others",
                )
                .hint(Hint::Enums),
            ]),
        list("Lists fields"),
        CommandSpec::new("get", "Shows a field").args([reference("Field")]),
        CommandSpec::new(
            "update",
            "Changes a field: name, type, 'multiple' and enum (the values in tasks are converted)",
        )
        .args([reference("Field")])
        .opts([
            NEW_NAME,
            OptSpec::value(
                "--type",
                "New type of the values: string, int, float, bool, date, enum. Allowed: any to string, int to float, string to int/float/bool/date/enum; the values in tasks are converted",
            )
            .hint(Hint::Fixed(FIELD_TYPES)),
            OptSpec::flag("--multiple", "The field holds several values from now on"),
            OptSpec::flag(
                "--single",
                "The field holds one value from now on (tasks with several values: choose --several)",
            ),
            OptSpec::value(
                "--enum",
                "New enum of the values (id or name); values of the tasks are matched by name, see --map",
            )
            .hint(Hint::Enums),
            OptSpec::flag(
                "--clear-unconvertible",
                "Clear the values of tasks that do not fit the new type/enum (the others are converted)",
            ),
            OptSpec::value(
                "--several",
                "Tasks with several values when the field becomes single: keep-first or clear",
            )
            .hint(Hint::Fixed(&["clear", "keep-first"])),
            OptSpec::many(
                "--map",
                "With a new enum: From=To pairs matching an old value to a value of the new enum (repeat for several); the rest are matched by name",
            ),
            EXPECTED_VERSION,
        ]),
        CommandSpec::new("delete", "Deletes a field (not used by task types and tasks)")
            .args([reference("Field")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn enumeration() -> CommandSpec {
    CommandSpec::new("enum", "Enums of the project: lists of values for fields of the type enum").subs([
        CommandSpec::new("create", "Creates an enum")
            .args([ArgSpec::new("name", "Enum name (unique in the project, case-insensitive)")])
            .opts([OptSpec::many("--value", "Values in display order (several after one --value, or repeat it)").required()]),
        list("Lists enums"),
        CommandSpec::new("get", "Shows an enum with its values").args([reference("Enum")]),
        CommandSpec::new("update", "Changes an enum: its name and values")
            .args([reference("Enum")])
            .opts([
                NEW_NAME,
                OptSpec::many(
                    "--values",
                    "Replace the whole list of values and their order; a value with the same name stays the same value (selected in tasks)",
                ),
                OptSpec::many("--add-value", "Add values at the end of the list"),
                OptSpec::many(
                    "--remove-value",
                    "Remove values (id or name). If tasks have them selected, choose --drop or --replace-with",
                )
                .hint(Hint::None),
                OptSpec::repeated(
                    "--rename-value",
                    "Rename a value: Old=New (repeat for several); the value stays the same in tasks",
                )
                .hint(Hint::None),
                OptSpec::flag(
                    "--drop",
                    "With --remove-value: clear the removed values from the tasks that have them selected",
                ),
                OptSpec::value(
                    "--replace-with",
                    "With --remove-value: give the tasks this value instead (a value the enum keeps: id or name)",
                )
                .hint(Hint::None),
                EXPECTED_VERSION,
            ]),
        CommandSpec::new("delete", "Deletes an enum (not used by fields)")
            .args([reference("Enum")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn board() -> CommandSpec {
    CommandSpec::new("board", "Boards").subs([
        CommandSpec::new("create", "Creates a board")
            .args([ArgSpec::new("name", "Board name")])
            .opts([
                OptSpec::many("--status-set", "Status sets whose tasks are on the board (id or name)")
                    .hint(Hint::StatusSets)
                    .required(),
                OptSpec::many("--column", COLUMN_HELP).hint(Hint::None).required(),
                OptSpec::many("--column-filter", FILTER_HELP).hint(Hint::None),
            ]),
        list("Lists boards"),
        CommandSpec::new("get", "Shows a board with its columns").args([reference("Board")]),
        CommandSpec::new("tasks", "Lists the tasks of a board column (the same lines as 'task list')")
            .args([reference("Board"), ArgSpec::new("column", "Column of the board (id or name)")])
            .opts([BOARD_VIEW_FIELD, SORT, TASK_DESCRIPTION_LENGTH])
            .opts(paging()),
        CommandSpec::new("show", "Shows the whole board: every column in order with its tasks")
            .args([reference("Board")])
            .opts([
                OptSpec::value("--limit", "How many tasks to show in each column, 1-200").int().default("50"),
                OptSpec::flag("--all", "Show all tasks of every column (cannot be combined with --limit)"),
                BOARD_VIEW_FIELD,
                SORT,
                TASK_DESCRIPTION_LENGTH,
            ]),
        CommandSpec::new("update", "Changes a board").args([reference("Board")]).opts([
            NEW_NAME,
            OptSpec::many("--status-set", "New status sets of the board (id or name); replaces the current ones").hint(Hint::StatusSets),
            OptSpec::many(
                "--column",
                "Column, left to right: \"Name=status,status\" (statuses by id or name). A task moved into the column gets the first of its statuses that belongs to the task type's status set. Replaces all columns; a column with the name of an existing one stays the same column and keeps its field conditions",
            )
            .hint(Hint::None),
            OptSpec::many(
                "--column-filter",
                "Field condition of a column, in addition to its statuses (AND): \"Column:Field=value\", \"Column:Field>=3\", \"Column:Field:set\" and so on - the same conditions as 'task list --field' (=, !=, >, >=, <, <=, :set, :unset, :attached, :detached), by the field's name in the catalog (stored by id). Repeat for several conditions; the column is named as in --column. A task is in the column only if its status and all conditions fit. Replaces the conditions of the named column (without --column - of that column of the current board; other columns keep theirs); \"Column:\" removes them",
            )
            .hint(Hint::None),
            EXPECTED_VERSION,
        ]),
        CommandSpec::new("delete", "Deletes a board (tasks stay)")
            .args([reference("Board")])
            .opts([EXPECTED_VERSION]),
    ])
}

fn task() -> CommandSpec {
    let link_arguments = [
        TASK_REFERENCE,
        ArgSpec::new(
            "link",
            "Link type or its side name: 'blocks', 'is blocked by', 'Duplicate', 'relates to'... (see 'link-type list')",
        ),
        ArgSpec::new("other-task", "The other task: id or reference like TSK-7"),
    ];
    CommandSpec::new("task", "Tasks").subs([
        CommandSpec::new("create", "Creates a task")
            .args([ArgSpec::new("title", "Task title")])
            .opts([
                OptSpec::value("--type", "Task type (id or name)").hint(Hint::TaskTypes).required(),
                OptSpec::value("--status", "Status (id or name). Default: the first status of the type's set").hint(Hint::Statuses),
                OptSpec::value("--description", "Task description").aliases(&["-d"]),
                OptSpec::repeated(
                    "--series",
                    "Series to put the task into (id or exact prefix); repeat for several series",
                )
                .hint(Hint::SeriesPrefixes),
                OptSpec::many(
                    "--parent",
                    "Make the new task a child of this task (id or reference like TSK-5; repeat or list several: the task gets several parents). Uses the project's hierarchical link type, see --parent-type",
                )
                .hint(Hint::TaskRefs),
                PARENT_TYPE,
                TASK_FIELD_VALUES,
                TASK_CUSTOM_FIELD,
                TASK_ADD_FIELD,
            ]),
        CommandSpec::new("list", "Lists tasks")
            .opts([
                OptSpec::many("--type", "Only tasks of this type (id or name); repeat the option or list several values: a task fits if it matches any of them (different filters must all hold)")
                    .hint(Hint::TaskTypes),
                OptSpec::many("--status", "Only tasks with this status (id or name); repeat the option or list several values: a task fits if it matches any of them (different filters must all hold)")
                    .hint(Hint::Statuses),
                OptSpec::many("--series", "Only tasks in this series (id or exact prefix); repeat the option or list several values: a task fits if it matches any of them (different filters must all hold)")
                    .hint(Hint::SeriesPrefixes),
                OptSpec::many(
                    "--field",
                    "Only tasks by a field - a catalog field or an own field of tasks with that name (a catalog field: own fields of the same name and type match too; no catalog field: the type of the own fields, an error if they differ) (repeat or list several: all must hold): \"Name=value\" or \"Name!=value\" (any type; != also takes tasks without a value; a multiple field - contains the value; enum value by name), \"Name>=value\" with > >= < <= (int, float, date; quote them in the shell), Name:set, Name:unset (has a value or not), Name:attached, Name:detached (the field is connected to the task or not)",
                )
                .hint(Hint::FieldEquals),
                SORT,
                OptSpec::flag(
                    "--flat",
                    "A flat list in the order of the sort, as without hierarchy. By default a task with children (an epic: it has links of a hierarchical type such as Parent/Child) is followed by its child tasks indented by four spaces per level; a task with several parents appears under each (the repeats are marked '(+)'), 'Found N' counts distinct tasks, --offset/--limit count top-level tasks (the ones without a parent in the result) with their subtrees. --json is always flat (parentIds, childCount)",
                ),
                TASK_DESCRIPTION_LENGTH,
            ])
            .opts(paging()),
        CommandSpec::new(
            "get",
            "Shows a task (a reference used by several tasks after a merge shows all of them)",
        )
        .args([TASK_REFERENCE]),
        CommandSpec::new("update", "Changes a task").args([TASK_REFERENCE]).opts([
            OptSpec::value("--title", "New title"),
            OptSpec::value("--description", "New description; an empty one clears it").aliases(&["-d"]),
            OptSpec::value("--type", "New task type (id or name)").hint(Hint::TaskTypes),
            OptSpec::value("--status", "New status (id or name); must be in the status set of the (new) type").hint(Hint::Statuses),
            OptSpec::many(
                "--add-parent",
                "Make the task a child of this task too (id or reference; repeat or list several). Uses the project's hierarchical link type, see --parent-type",
            )
            .hint(Hint::TaskRefs),
            OptSpec::many(
                "--remove-parent",
                "Take the task out of this parent (id or reference; repeat or list several; no such link is not an error)",
            )
            .hint(Hint::TaskRefs),
            PARENT_TYPE,
            TASK_FIELD_VALUES,
            TASK_CUSTOM_FIELD,
            TASK_ADD_FIELD,
            TASK_REMOVE_FIELD,
            EXPECTED_VERSION,
        ]),
        CommandSpec::new("delete", "Deletes a task").args([TASK_REFERENCE]).opts([EXPECTED_VERSION]),
        CommandSpec::new(
            "link",
            "Links two tasks: 'task link TSK-5 blocks TSK-7' (or 'TSK-7 is blocked by TSK-5'); types: see 'link-type list'",
        )
        .args(link_arguments),
        CommandSpec::new(
            "unlink",
            "Removes a link between two tasks (the same arguments as 'task link'; a missing link is not an error)",
        )
        .args(link_arguments),
        CommandSpec::new("links", "Shows the links of a task: the ones it starts and the ones pointing at it").args([TASK_REFERENCE]),
    ])
}

fn series() -> CommandSpec {
    let series_reference = ArgSpec::new("series", "Series (id or exact prefix)");
    let task_reference = ArgSpec::new("task", "Task (id or reference like TSK-5)");
    CommandSpec::new("series", "Task series: numbered references like TSK-5").subs([
        CommandSpec::new("create", "Creates a series")
            .args([ArgSpec::new("name", "Series name")])
            .opts([OptSpec::value(
                "--prefix",
                "Prefix of task references: Latin letters and digits, up to 20, case matters (TSK-5)",
            )
            .required()]),
        list("Lists series"),
        CommandSpec::new("get", "Shows a series").args([series_reference]),
        CommandSpec::new("update", "Changes a series (a new prefix renames it)")
            .args([series_reference])
            .opts([
                NEW_NAME,
                OptSpec::value("--prefix", "New prefix: references like TSK-5 written elsewhere stop working"),
                EXPECTED_VERSION,
            ]),
        CommandSpec::new(
            "delete",
            "Deletes a series; its tasks stay and lose the series (and their numbers in it)",
        )
        .args([series_reference])
        .opts([EXPECTED_VERSION]),
        CommandSpec::new("add-task", "Puts a task into a series: it gets the next number").args([series_reference, task_reference]),
        CommandSpec::new(
            "remove-task",
            "Takes the task with this number out of a series (the number becomes free)",
        )
        .args([series_reference, ArgSpec::new("number", "Number of the task in the series").int()]),
        CommandSpec::new("renumber-task", "Changes the number of a task in a series")
            .args([series_reference, task_reference])
            .opts([OptSpec::value(
                "--to",
                "The new number (must be free). Default: the next free number (the maximum + 1)",
            )
            .int()]),
    ])
}

fn cleanup() -> CommandSpec {
    CommandSpec::new(
        "cleanup",
        "Removes references to missing series and links to missing tasks or link types after a git merge (with --resolve-conflicts also renumbers duplicate numbers); link cycles are only reported, remove one link with 'task unlink'",
    )
    .opts([
        OptSpec::flag(
            "--resolve-conflicts",
            "Also give duplicate numbers new ones: the earliest task keeps its number, the others get the next numbers",
        ),
        OptSpec::flag("--dry-run", "Write nothing, only show what would change"),
        OptSpec::flag(
            "--check",
            "Write nothing; exit with code 2 if the cleanup would change something or something needs attention (for scripts and CI)",
        ),
    ])
}

pub const MIGRATE_DESCRIPTION: &str = "Brings the files of the workspace to the current file format version (formatVersion) and names the files of project entities after their names (titles of tasks)";

fn migrate() -> CommandSpec {
    CommandSpec::new("migrate", MIGRATE_DESCRIPTION).opts([
        OptSpec::flag("--dry-run", "Write nothing, only show which files would be migrated"),
        OptSpec::flag(
            "--check",
            "Write nothing; exit with code 2 if any file is not in the current format or needs attention (for scripts and CI)",
        ),
    ])
}

fn users(name: &'static str, description: &'static str, what: &'static str) -> CommandSpec {
    let (create, name_description, list_description, get, update, delete) = match what {
        "User" => (
            "Creates a user",
            "User name",
            "Lists users",
            "Shows a user",
            "Renames a user",
            "Deletes a user",
        ),
        "Agent" => (
            "Creates an agent",
            "Agent name",
            "Lists agents",
            "Shows an agent",
            "Renames an agent",
            "Deletes an agent",
        ),
        _ => panic!("no user texts for {what}"),
    };
    CommandSpec::new(name, description).subs([
        CommandSpec::new("create", create).args([ArgSpec::new("name", name_description)]),
        list(list_description),
        CommandSpec::new("get", get).args([reference(what)]),
        CommandSpec::new("update", update)
            .args([reference(what)])
            .opts([NEW_NAME, EXPECTED_VERSION]),
        CommandSpec::new("delete", delete).args([reference(what)]).opts([EXPECTED_VERSION]),
    ])
}

fn lock() -> CommandSpec {
    let arguments = [
        ArgSpec::new(
            "entity",
            "Kind of the entity: project, task, taskType, linkType, field, enum, status, statusSet, board, series",
        ),
        ArgSpec::new(ID_OR_NAME, "The entity (id or name; tasks also by PREFIX-number)"),
    ];
    CommandSpec::new(
        "lock",
        "Locks an entity while you edit it, so nobody else changes or deletes it (expires after 2 minutes)",
    )
    .subs([
        CommandSpec::new("acquire", "Locks an entity, or renews your lock").args(arguments),
        CommandSpec::new("release", "Releases your lock (nothing to release is not an error)").args(arguments),
        CommandSpec::new("show", "Shows who is editing an entity").args(arguments),
    ])
}

fn whoami() -> CommandSpec {
    CommandSpec::new(
        "whoami",
        "Shows or sets your name that others see in edit locks (shared by the desktop app and the console)",
    )
    .args([ArgSpec::new("name", "New name; without it the current one is shown").optional()])
    .opts([OptSpec::flag("--clear", "Use the operating system user name again")])
}

fn sync() -> CommandSpec {
    CommandSpec::new(
        "sync",
        "Brings the cache of a workspace up to date with its files (after git pull, checkout, merge)",
    )
}

fn hooks() -> CommandSpec {
    CommandSpec::new(
        "hooks",
        "Git hooks that keep the cache up to date after pull, checkout, merge and rebase",
    )
    .subs([
        CommandSpec::new(
            "install",
            "Installs the hooks (post-merge, post-checkout, post-rewrite) that run 'tasker sync'",
        ),
        CommandSpec::new("uninstall", "Removes the Tasker part of the hooks; other commands in them stay"),
        CommandSpec::new("status", "Shows which hooks are installed"),
    ])
}

/// Языки справочника: пока только `ru` (каталог страниц переносится вместе с командой `manual`, TSK-135).
pub const MANUAL_LANGUAGES: &[&str] = &["ru"];

fn manual() -> CommandSpec {
    CommandSpec::new(
        "manual",
        "Short how-to recipes with ready commands: the list of topics, 'manual <topic>' for a recipe, 'manual <word>' to search",
    )
    .with_aliases(&["howto"])
    .args([ArgSpec::new(
        "topic",
        "Topic name from the list, or words to search for (in names, titles and descriptions, then in the text). Without it: the list of topics",
    )
    .zero_or_more()])
    .opts([OptSpec::value(
        "--lang",
        "Language of the pages. Default: the TASKER_LANG variable, else ru. A topic without a page in this language is shown in ru, with a note",
    )
    .hint(Hint::Fixed(MANUAL_LANGUAGES))])
}

/// Оболочки `completion` (`Shells.AcceptedNames`).
/// Порядок `Shells.AcceptedNames`: так их перечисляет ошибка «not recognized» (справка сортирует сама).
pub const SHELLS: &[&str] = &["zsh", "bash", "pwsh", "powershell"];

fn completion() -> CommandSpec {
    CommandSpec::new(
        "completion",
        "Prints the Tab completion script of a shell: commands, options and values (projects, statuses, tasks...) of the current workspace",
    )
    .args([ArgSpec::new("shell", "Shell: zsh, bash, pwsh").accept_only(SHELLS)])
    .opts([
        OptSpec::flag(
            "--install",
            "pwsh only: save the script and add one marked block to your PowerShell profile ($PROFILE) that loads it (a copy <profile>.tasker-backup is made before the first change; repeating is safe)",
        ),
        OptSpec::flag(
            "--uninstall",
            "pwsh only: remove the block from your PowerShell profile (the saved script is deleted too)",
        ),
        OptSpec::repeated(
            "--profile",
            "pwsh only, with --install or --uninstall: profile file to change (repeatable). Default: the profiles of PowerShell 7 and Windows PowerShell 5.1 (Windows) or of pwsh (macOS, Linux)",
        ),
        OptSpec::value(
            "--script",
            "pwsh only, with --install: where to save the script. Default: <Tasker data folder>/completions/tasker.ps1",
        ),
    ])
}

fn mcp() -> CommandSpec {
    CommandSpec::new("mcp", "MCP server: global settings and the background service").subs([
        CommandSpec::new(
            "run",
            "Runs the MCP server in this terminal until Ctrl+C (the background service runs the same)",
        )
        .opts([OptSpec::flag("--detached", "Started in the background (used by start and by the system service)").hidden()]),
        CommandSpec::new("start", "Starts the MCP server in the background"),
        CommandSpec::new("stop", "Stops the MCP server"),
        CommandSpec::new("restart", "Restarts the MCP server (applies a changed port)"),
        CommandSpec::new(
            "upgrade",
            "Switches the running MCP server to the installed build without downtime (--restart: restart instead)",
        )
        .opts([
            OptSpec::flag(
                "--restart",
                "Hard path: stop the MCP server and start it again (calls in progress are cut off)",
            ),
            OptSpec::value(
                "--timeout",
                "Seconds to wait for the new server process to open the workspaces and be ready (default 60)",
            )
            .int()
            .default("60"),
            OptSpec::value(
                "--daemon",
                "Path of the tasker-mcpd program of the new build (default: the one next to this tasker)",
            )
            .hidden(),
        ]),
        CommandSpec::new("status", "Shows whether the MCP server is running (exit code 3 if not)"),
        CommandSpec::new(
            "autostart",
            "Runs the MCP server at login and keeps it running (launchd on macOS, systemd on Linux, Task Scheduler on Windows)",
        )
        .subs([
            CommandSpec::new("enable", "Enables autostart and starts the MCP server now"),
            CommandSpec::new("disable", "Disables autostart; a running MCP server keeps running"),
            CommandSpec::new("status", "Shows whether autostart is enabled"),
        ]),
        CommandSpec::new("workspace", "Workspaces available through MCP").subs([
            CommandSpec::new("add", "Allows a workspace (folder or existing SQLite file) in MCP").args([ArgSpec::new(
                "path",
                "Workspace folder or SQLite file. Default: the current folder",
            )
            .optional()]),
            CommandSpec::new("remove", "Removes a workspace from MCP").args([ArgSpec::new(
                "path",
                "Workspace folder or SQLite file. Default: the current folder",
            )
            .optional()]),
            CommandSpec::new("list", "Lists workspaces available through MCP"),
        ]),
        CommandSpec::new("config", "Shows the global MCP settings and where they are stored"),
        CommandSpec::new("port", "Shows or sets the MCP port on 127.0.0.1").args([ArgSpec::new(
            "port",
            "New port; without it the current one is shown",
        )
        .optional()
        .int()]),
    ])
}

/// Корень `tasker` со всеми командами в порядке `CliApp.BuildRoot`.
pub fn root() -> CommandSpec {
    CommandSpec::new("tasker", ROOT_DESCRIPTION).subs([
        project(),
        status(),
        status_set(),
        task_type(),
        link_type(),
        field(),
        enumeration(),
        board(),
        task(),
        series(),
        cleanup(),
        migrate(),
        users("user", "Users (only a name, as in the desktop app)", "User"),
        users("agent", "Agents (users for LLMs working through MCP)", "Agent"),
        lock(),
        whoami(),
        sync(),
        hooks(),
        manual(),
        completion(),
        mcp(),
    ])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_clap_tree_is_consistent() {
        clap_root(&root()).debug_assert();
    }

    #[test]
    fn there_are_112_commands_with_descriptions_everywhere() {
        fn walk(command: &CommandSpec, count: &mut usize) {
            *count += 1;
            assert!(!command.description.is_empty(), "{}", command.name);
            for option in &command.options {
                assert!(!option.description.is_empty(), "{} {}", command.name, option.name);
            }
            for argument in &command.arguments {
                assert!(!argument.description.is_empty(), "{} {}", command.name, argument.name);
            }
            for sub in &command.subcommands {
                walk(sub, count);
            }
        }
        let mut count = 0;
        walk(&root(), &mut count);
        assert_eq!(count, 112);
    }

    #[test]
    fn global_options_are_accepted_anywhere() {
        let m = clap_root(&root())
            .try_get_matches_from(["tasker", "-w", "x", "task", "list", "--status", "A", "B", "--json", "-q", "--all"])
            .unwrap();
        assert_eq!(m.get_one::<String>("workspace").map(String::as_str), Some("x"));
        let (name, task) = m.subcommand().unwrap();
        assert_eq!(name, "task");
        let (name, list) = task.subcommand().unwrap();
        assert_eq!(name, "list");
        assert!(list.get_flag("json") && list.get_flag("quiet") && list.get_flag("all"));
        let statuses: Vec<&String> = list.get_many::<String>("status").unwrap().collect();
        assert_eq!(statuses, ["A", "B"]);
    }

    #[test]
    fn optional_bool_takes_an_optional_value_and_howto_is_an_alias_of_manual() {
        let m = clap_root(&root())
            .try_get_matches_from(["tasker", "link-type", "create", "X", "--outward", "o", "--allow-cycles"])
            .unwrap();
        let create = m.subcommand().unwrap().1.subcommand().unwrap().1;
        assert_eq!(create.get_one::<String>("allow-cycles").map(String::as_str), Some("true"));
        let m = clap_root(&root())
            .try_get_matches_from(["tasker", "link-type", "create", "X", "--outward", "o", "--allow-cycles", "false"])
            .unwrap();
        let create = m.subcommand().unwrap().1.subcommand().unwrap().1;
        assert_eq!(create.get_one::<String>("allow-cycles").map(String::as_str), Some("false"));

        let m = clap_root(&root()).try_get_matches_from(["tasker", "howto", "tasks"]).unwrap();
        assert_eq!(m.subcommand().unwrap().0, "manual");
    }
}
