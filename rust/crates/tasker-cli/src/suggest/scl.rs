//! Разбор строки и варианты дополнения ровно так, как их строит System.CommandLine 2.0 у .NET-консоли — директива `[suggest]`
//! (`CompleteDirective`) опирается на `ParseResult.GetCompletions`, и все его особенности видны пользователю: после флага
//! предлагаются `True`/`False`, имена команд и параметров сравниваются по вхождению, а не по началу, слово под курсором —
//! кусок строки после последнего пробела, параметр, у которого набрано максимум значений, больше не предлагается.
//!
//! Перенесено по исходникам dotnet/command-line-api: `CommandLineParser.SplitCommandLine`, `StringExtensions.Tokenize`
//! (известные токены, `--x=значение`, склейка `-abc`, файлы ответов `@файл`, имя программы первым словом), `ParseOperation`
//! (результаты команд, параметров и аргументов, жадные параметры, `AllowMultipleArgumentsPerToken`, `bool.TryParse` у флагов),
//! `ParseResult.SymbolToComplete`/`WillAcceptAnArgument`/`OptionsWithArgumentLimitReached`, `Command.GetCompletions`,
//! `Option.GetCompletions`, `CompletionContext.GetWordToComplete`. Проверки и значения по умолчанию разбору дополнения не нужны.
use crate::spec::{ArgSpec, Arity, CommandSpec, GLOBAL_OPTIONS, HELP_OPTION, OptKind, OptSpec, VERSION_OPTION};
use std::collections::HashMap;
use tasker_core::validate::upper_char;

/// Справка корня (`HelpOption`): все её имена — и те, что консоль не показывает (`/h`, `/?`), — известны разбору.
static HELP: OptSpec = OptSpec {
    aliases: &["-h", "/h", "-?", "/?"],
    ..HELP_OPTION
};
static VERSION: OptSpec = VERSION_OPTION;
static GLOBALS: [OptSpec; 8] = GLOBAL_OPTIONS;

/// `ArgumentArity.MaximumArity`: «сколько угодно».
const MAXIMUM_ARITY: usize = 100_000;

/// Имя корневой команды (`RootCommand.ExecutableName`).
const ROOT_NAME: &str = "tasker";

/// Символ, к которому относится токен или результат (`Symbol`): сравнение — по адресу описания, как ссылки в .NET.
#[derive(Clone, Copy, Debug)]
pub enum Sym<'a> {
    None,
    Command(&'a CommandSpec),
    Option(&'a OptSpec),
    Argument(&'a ArgSpec),
}

impl PartialEq for Sym<'_> {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Sym::None, Sym::None) => true,
            (Sym::Command(a), Sym::Command(b)) => std::ptr::eq(*a, *b),
            (Sym::Option(a), Sym::Option(b)) => std::ptr::eq(*a, *b),
            (Sym::Argument(a), Sym::Argument(b)) => std::ptr::eq(*a, *b),
            _ => false,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Argument,
    Command,
    Option,
    DoubleDash,
    Directive,
}

#[derive(Clone, Debug)]
pub struct Token<'a> {
    pub value: String,
    pub kind: Kind,
    pub symbol: Sym<'a>,
}

impl PartialEq for Token<'_> {
    fn eq(&self, other: &Self) -> bool {
        self.value == other.value && self.kind == other.kind && self.symbol == other.symbol
    }
}

/// Арность значения параметра и его свойства разбора (`Option.Argument.Arity`, `Greedy`, `IsBoolean`).
#[derive(Clone, Copy)]
pub struct OptionShape {
    pub min: usize,
    pub max: usize,
    pub boolean: bool,
    pub per_token: bool,
}

impl OptionShape {
    /// `Option.Greedy`: значение обязательно и это не флаг.
    fn greedy(&self) -> bool {
        self.min > 0 && !self.boolean
    }
}

pub fn shape(option: &OptSpec) -> OptionShape {
    if std::ptr::eq(option, &HELP) || std::ptr::eq(option, &VERSION) {
        return OptionShape {
            min: 0,
            max: 0,
            boolean: false,
            per_token: false,
        };
    }
    match option.kind {
        // Option<bool> и Option<bool?>: ArgumentArity.ZeroOrOne.
        OptKind::Flag | OptKind::OptionalBool => OptionShape {
            min: 0,
            max: 1,
            boolean: true,
            per_token: false,
        },
        // Option<string?>, Option<int>: ExactlyOne (у значения параметра родитель — параметр, а не команда).
        OptKind::Value { multiple: false, .. } => OptionShape {
            min: 1,
            max: 1,
            boolean: false,
            per_token: false,
        },
        // Option<string[]>: OneOrMore.
        OptKind::Value {
            multiple: true, per_token, ..
        } => OptionShape {
            min: 1,
            max: MAXIMUM_ARITY,
            boolean: false,
            per_token,
        },
    }
}

fn argument_max(argument: &ArgSpec) -> usize {
    match argument.arity {
        Arity::ExactlyOne | Arity::ZeroOrOne => 1,
        Arity::ZeroOrMore => MAXIMUM_ARITY,
    }
}

/// Дерево команд с родителями и параметрами в том виде, в каком их видит System.CommandLine: у корня — справка, версия и общие
/// параметры (рекурсивные — справка и общие), у остальных команд — их собственные.
pub struct Tree<'a> {
    pub root: &'a CommandSpec,
    parents: HashMap<*const CommandSpec, &'a CommandSpec>,
}

impl<'a> Tree<'a> {
    pub fn new(root: &'a CommandSpec) -> Tree<'a> {
        fn walk<'a>(command: &'a CommandSpec, parents: &mut HashMap<*const CommandSpec, &'a CommandSpec>) {
            for sub in &command.subcommands {
                parents.insert(sub as *const CommandSpec, command);
                walk(sub, parents);
            }
        }
        let mut parents = HashMap::new();
        walk(root, &mut parents);
        Tree { root, parents }
    }

    pub fn parent(&self, command: &CommandSpec) -> Option<&'a CommandSpec> {
        self.parents.get(&(command as *const CommandSpec)).copied()
    }

    pub fn is_root(&self, command: &CommandSpec) -> bool {
        std::ptr::eq(command, self.root)
    }

    /// `Command.Options`.
    pub fn options(&self, command: &'a CommandSpec) -> Vec<&'a OptSpec> {
        if self.is_root(command) {
            let mut all: Vec<&'a OptSpec> = vec![&HELP, &VERSION];
            all.extend(GLOBALS.iter());
            all
        } else {
            command.options.iter().collect()
        }
    }

    /// Общий параметр корня по имени (`--workspace`, `--project`…).
    pub fn global(name: &str) -> Option<&'static OptSpec> {
        GLOBALS.iter().find(|o| o.name == name)
    }

    pub fn recursive(&self, option: &OptSpec) -> bool {
        std::ptr::eq(option, &HELP) || GLOBALS.iter().any(|g| std::ptr::eq(option, g))
    }

    /// Команда и её предки, начиная с неё самой.
    pub fn chain(&self, command: &'a CommandSpec) -> Vec<&'a CommandSpec> {
        let mut chain = vec![command];
        while let Some(parent) = self.parent(chain.last().expect("a command")) {
            chain.push(parent);
        }
        chain
    }

    /// `ValidTokens`: команда, её подкоманды и параметры, рекурсивные параметры предков (первое имя выигрывает).
    fn valid_tokens(&self, command: &'a CommandSpec) -> HashMap<String, Token<'a>> {
        let mut tokens: HashMap<String, Token<'a>> = HashMap::new();
        let add_command = |tokens: &mut HashMap<String, Token<'a>>, cmd: &'a CommandSpec| {
            for name in cmd.all_names() {
                tokens.insert(name.to_string(), token(name, Kind::Command, Sym::Command(cmd)));
            }
        };
        add_command(&mut tokens, command);
        for sub in &command.subcommands {
            add_command(&mut tokens, sub);
        }
        let add_option = |tokens: &mut HashMap<String, Token<'a>>, option: &'a OptSpec| {
            for name in option.all_names() {
                tokens
                    .entry(name.to_string())
                    .or_insert_with(|| token(name, Kind::Option, Sym::Option(option)));
            }
        };
        for option in self.options(command) {
            add_option(&mut tokens, option);
        }
        let mut current = command;
        while let Some(parent) = self.parent(current) {
            for option in self.options(parent) {
                if self.recursive(option) {
                    add_option(&mut tokens, option);
                }
            }
            current = parent;
        }
        tokens
    }
}

fn token<'a>(value: &str, kind: Kind, symbol: Sym<'a>) -> Token<'a> {
    Token {
        value: value.to_string(),
        kind,
        symbol,
    }
}

/// `CommandLineParser.SplitCommandLine`: слова по пробелам, двойные кавычки объединяют и выбрасываются.
pub fn split_command_line(line: &str) -> Vec<String> {
    #[derive(PartialEq)]
    enum Boundary {
        TokenStart,
        WordEnd,
        QuoteStart,
        QuoteEnd,
    }
    let chars: Vec<char> = line.chars().collect();
    let mut result = Vec::new();
    let mut start = 0;
    let mut pos = 0;
    let mut seeking = Boundary::TokenStart;
    let mut seeking_quote = Boundary::QuoteStart;
    let current = |start: usize, pos: usize| -> String { chars[start..pos].iter().filter(|c| **c != '"').collect() };
    while pos < chars.len() {
        let c = chars[pos];
        if c.is_whitespace() {
            if seeking_quote == Boundary::QuoteStart {
                match seeking {
                    Boundary::WordEnd => {
                        result.push(current(start, pos));
                        start = pos;
                        seeking = Boundary::TokenStart;
                    }
                    Boundary::TokenStart => start = pos,
                    _ => {}
                }
            }
        } else if c == '"' {
            if seeking == Boundary::TokenStart {
                match seeking_quote {
                    Boundary::QuoteEnd => {
                        result.push(current(start, pos));
                        start = pos;
                        seeking_quote = Boundary::QuoteStart;
                    }
                    Boundary::QuoteStart => {
                        start = pos + 1;
                        seeking_quote = Boundary::QuoteEnd;
                    }
                    _ => {}
                }
            } else {
                seeking_quote = if seeking_quote == Boundary::QuoteEnd {
                    Boundary::QuoteStart
                } else {
                    Boundary::QuoteEnd
                };
            }
        } else if seeking == Boundary::TokenStart && seeking_quote == Boundary::QuoteStart {
            seeking = Boundary::WordEnd;
            start = pos;
        }
        pos += 1;
        if pos == chars.len() && seeking != Boundary::TokenStart {
            result.push(current(start, pos));
        }
    }
    result
}

/// Ошибка, которую .NET не ловит внутри разбора (файл ответов — каталог или без доступа): директива тогда молчит.
pub struct Abort;

/// `TrySplitIntoSubtokens`: `--x=значение` и `--x:значение`.
fn split_subtokens(arg: &str) -> Option<(String, Option<String>)> {
    let at = arg.find([':', '='])?;
    let rest = &arg[at + 1..];
    Some((arg[..at].to_string(), (!rest.is_empty()).then(|| rest.to_string())))
}

/// Слова файла ответов `@файл` (`ExpandResponseFile`): строки без пустых и `#`-комментариев, вложенные `@файл` раскрываются.
fn response_file(path: &str, depth: usize) -> Result<Option<Vec<String>>, Abort> {
    let text = match std::fs::read(path) {
        Ok(bytes) => String::from_utf8_lossy(&bytes).into_owned(),
        Err(e) if e.kind() == std::io::ErrorKind::PermissionDenied || e.kind() == std::io::ErrorKind::IsADirectory => {
            return Err(Abort);
        }
        Err(_) if std::path::Path::new(path).is_dir() => return Err(Abort),
        Err(_) => return Ok(None),
    };
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    let mut words = Vec::new();
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        for word in split_command_line(line) {
            match word.strip_prefix('@').filter(|_| word.len() > 1 && depth < 16) {
                // Вложенный файл не прочитать — не читается и весь файл ответов.
                Some(nested) => match response_file(nested, depth + 1)? {
                    Some(nested_words) => words.extend(nested_words),
                    None => return Ok(None),
                },
                None => words.push(word),
            }
        }
    }
    Ok(Some(words))
}

/// `StringExtensions.Tokenize` с `inferRootCommand` (строка, а не массив аргументов).
pub fn tokenize<'a>(tree: &Tree<'a>, args: Vec<String>) -> Result<Vec<Token<'a>>, Abort> {
    let root = tree.root;
    let mut args = args;
    let mut current = root;
    let mut found_double_dash = false;
    let mut found_end_of_directives = false;
    let mut tokens: Vec<Token<'a>> = Vec::new();
    let mut known = tree.valid_tokens(root);

    let first_is_root = args.first().is_some_and(|first| {
        if let Some((head, _)) = split_subtokens(first)
            && known.get(&head).is_some_and(|t| t.kind == Kind::Option)
        {
            return false;
        }
        let name = first.rsplit('/').next().unwrap_or(first);
        name == ROOT_NAME
    });
    let mut i: isize = if first_is_root { 0 } else { -1 };

    let previous_greedy = |tokens: &Vec<Token<'a>>| -> Option<&'a OptSpec> {
        if tokens.len() > 1
            && let Some(last) = tokens.last()
            && last.kind == Kind::Option
            && let Sym::Option(option) = last.symbol
            && shape(option).greedy()
        {
            return Some(option);
        }
        None
    };

    while (i as usize) < args.len() || i < 0 {
        let arg = if i < 0 { ROOT_NAME.to_string() } else { args[i as usize].clone() };
        let index = i;
        i += 1;
        if found_double_dash {
            tokens.push(token(&arg, Kind::Argument, Sym::Command(current)));
            continue;
        }
        if arg == "--" {
            tokens.push(token(&arg, Kind::DoubleDash, Sym::None));
            found_double_dash = true;
            continue;
        }
        if !found_end_of_directives {
            let chars: Vec<char> = arg.chars().collect();
            if chars.len() > 2 && chars[0] == '[' && chars[1] != ']' && chars[1] != ':' && chars[chars.len() - 1] == ']' {
                tokens.push(token(&arg, Kind::Directive, Sym::None));
                continue;
            }
            if !root.all_names().any(|n| n == arg) {
                found_end_of_directives = true;
            }
        }
        if arg.len() > 1 && arg.starts_with('@') {
            if let Some(words) = response_file(&arg[1..], 0)?
                && !words.is_empty()
            {
                let at = index as usize + 1;
                args.splice(at..at, words);
            }
            continue;
        }
        if let Some(found) = known.get(&arg).cloned() {
            if let Some(option) = previous_greedy(&tokens) {
                tokens.push(token(&arg, Kind::Argument, Sym::Option(option)));
            } else {
                match found.kind {
                    Kind::Option => tokens.push(token(&arg, Kind::Option, found.symbol)),
                    Kind::Command => {
                        let Sym::Command(command) = found.symbol else { unreachable!() };
                        if !std::ptr::eq(command, current) {
                            if !std::ptr::eq(command, root) {
                                known = tree.valid_tokens(command);
                            }
                            current = command;
                            tokens.push(token(&arg, Kind::Command, found.symbol));
                        } else {
                            tokens.push(token(&arg, Kind::Argument, Sym::None));
                        }
                    }
                    _ => {}
                }
            }
        } else if let Some((first, rest)) = split_subtokens(&arg)
            && let Some(found) = known.get(&first).filter(|t| t.kind == Kind::Option).cloned()
        {
            tokens.push(token(&first, Kind::Option, found.symbol));
            if let Some(rest) = rest {
                tokens.push(token(&rest, Kind::Argument, Sym::None));
            }
        } else if !can_be_unbundled(&arg, previous_greedy(&tokens).is_some()) || !unbundle(&arg, &known, &mut tokens) {
            tokens.push(token(&arg, Kind::Argument, Sym::None));
        }
    }
    Ok(tokens)
}

fn can_be_unbundled(arg: &str, previous_greedy: bool) -> bool {
    let chars: Vec<char> = arg.chars().collect();
    chars.len() > 2 && chars[0] == '-' && chars[1] != '-' && chars[2] != ':' && chars[2] != '=' && !previous_greedy
}

/// `TryUnbundle`: `-qp X` → `-q -p X`; жадный параметр забирает остаток слова значением.
fn unbundle<'a>(arg: &str, known: &HashMap<String, Token<'a>>, tokens: &mut Vec<Token<'a>>) -> bool {
    let alias: Vec<char> = arg.chars().skip(1).collect();
    let before = tokens.len();
    for i in 0..alias.len() {
        if alias[i] == ':' || alias[i] == '=' {
            tokens.push(token(&alias[i + 1..].iter().collect::<String>(), Kind::Argument, Sym::None));
            return true;
        }
        let candidate = format!("-{}", alias[i]);
        let Some(found) = known.get(&candidate) else {
            if before != tokens.len() && tokens.last().is_some_and(|t| t.kind == Kind::Option) {
                tokens.push(token(&alias[i..].iter().collect::<String>(), Kind::Argument, Sym::None));
                return true;
            }
            return false;
        };
        tokens.push(found.clone());
        if i != alias.len() - 1
            && let Sym::Option(option) = found.symbol
            && shape(option).greedy()
        {
            let mut index = i + 1;
            if alias[index] == ':' || alias[index] == '=' {
                index += 1;
            }
            tokens.push(token(&alias[index..].iter().collect::<String>(), Kind::Argument, Sym::None));
            return true;
        }
    }
    true
}

/// Результат разбора одного символа (`SymbolResult`): родитель — индекс результата команды, токены — индексы в списке токенов.
pub enum Result_<'a> {
    Command {
        command: &'a CommandSpec,
    },
    Option {
        option: &'a OptSpec,
        parent: usize,
        tokens: Vec<usize>,
    },
    Argument {
        argument: &'a ArgSpec,
        parent: usize,
        tokens: Vec<usize>,
    },
}

/// `ParseResult`: токены, результаты в порядке добавления (`SymbolResultTree`) и самая внутренняя команда.
pub struct Parsed<'a> {
    pub line: String,
    pub tokens: Vec<Token<'a>>,
    pub results: Vec<Result_<'a>>,
    pub innermost: usize,
}

/// `Command.Parse(line)`; Err — разбор упал бы исключением (директива тогда ничего не печатает).
pub fn parse<'a>(tree: &Tree<'a>, line: &str) -> Result<Parsed<'a>, Abort> {
    let tokens = tokenize(tree, split_command_line(line))?;
    let mut parsed = Parsed {
        line: line.to_string(),
        tokens,
        results: vec![Result_::Command { command: tree.root }],
        innermost: 0,
    };
    let mut index = 1;
    while index < parsed.tokens.len() && parsed.tokens[index].kind == Kind::Directive {
        index += 1;
    }
    let mut argument_count = 0usize;
    let mut argument_index = 0usize;
    while index < parsed.tokens.len() {
        let command = parsed.command(parsed.innermost);
        while argument_index < command.arguments.len() && argument_count >= argument_max(&command.arguments[argument_index]) {
            argument_count = 0;
            argument_index += 1;
        }
        match parsed.tokens[index].kind {
            Kind::Command => {
                let Sym::Command(sub) = parsed.tokens[index].symbol else {
                    unreachable!()
                };
                parsed.results.push(Result_::Command { command: sub });
                parsed.innermost = parsed.results.len() - 1;
                index += 1;
                // ParseSubcommand → ParseCommandChildren заново: счётчики аргументов свои у каждой команды.
                argument_count = 0;
                argument_index = 0;
            }
            Kind::Option => {
                let Sym::Option(option) = parsed.tokens[index].symbol else {
                    unreachable!()
                };
                let at = match parsed.option_result(option) {
                    Some(at) => at,
                    None => {
                        parsed.results.push(Result_::Option {
                            option,
                            parent: parsed.innermost,
                            tokens: Vec::new(),
                        });
                        parsed.results.len() - 1
                    }
                };
                index += 1;
                index = parsed.option_arguments(at, index);
            }
            Kind::Argument => {
                index = parsed.command_arguments(index, &mut argument_count, &mut argument_index);
            }
            _ => index += 1,
        }
    }
    Ok(parsed)
}

impl<'a> Parsed<'a> {
    fn command(&self, at: usize) -> &'a CommandSpec {
        match self.results[at] {
            Result_::Command { command } => command,
            _ => unreachable!("a command result"),
        }
    }

    /// Самая внутренняя команда (`ParseResult.CommandResult.Command`).
    pub fn innermost_command(&self) -> &'a CommandSpec {
        self.command(self.innermost)
    }

    pub fn option_result(&self, option: &OptSpec) -> Option<usize> {
        self.results
            .iter()
            .position(|r| matches!(r, Result_::Option { option: o, .. } if std::ptr::eq(*o, option)))
    }

    fn argument_result(&self, argument: &ArgSpec) -> Option<usize> {
        self.results
            .iter()
            .position(|r| matches!(r, Result_::Argument { argument: a, .. } if std::ptr::eq(*a, argument)))
    }

    /// `ParseOptionArguments`.
    fn option_arguments(&mut self, at: usize, mut index: usize) -> usize {
        let Result_::Option { option, .. } = self.results[at] else {
            unreachable!()
        };
        let shape = shape(option);
        let mut count = 0;
        let mut contiguous = 0;
        while index < self.tokens.len() && self.tokens[index].kind == Kind::Argument {
            if count >= shape.max {
                if contiguous > 0 || shape.max == 0 {
                    break;
                }
            } else if shape.boolean && !parses_as_bool(&self.tokens[index].value) {
                break;
            }
            if let Result_::Option { tokens, .. } = &mut self.results[at] {
                tokens.push(index);
            }
            count += 1;
            contiguous += 1;
            index += 1;
            if !shape.per_token {
                return index;
            }
        }
        index
    }

    /// `ParseCommandArguments`.
    fn command_arguments(&mut self, mut index: usize, count: &mut usize, argument_index: &mut usize) -> usize {
        let command = self.innermost_command();
        while index < self.tokens.len() && self.tokens[index].kind == Kind::Argument {
            while *argument_index < command.arguments.len() {
                let argument = &command.arguments[*argument_index];
                if *count < argument_max(argument) {
                    let at = match self.argument_result(argument) {
                        Some(at) => at,
                        None => {
                            self.results.push(Result_::Argument {
                                argument,
                                parent: self.innermost,
                                tokens: Vec::new(),
                            });
                            self.results.len() - 1
                        }
                    };
                    if let Result_::Argument { tokens, .. } = &mut self.results[at] {
                        tokens.push(index);
                    }
                    // Токен без символа становится токеном аргумента (`CurrentToken.Symbol = argument`): так его и сравнивают.
                    if self.tokens[index].symbol == Sym::None {
                        self.tokens[index].symbol = Sym::Argument(argument);
                    }
                    *count += 1;
                    index += 1;
                    break;
                }
                *count = 0;
                *argument_index += 1;
            }
            if *count == 0 {
                // Лишний токен: в несопоставленные.
                index += 1;
            }
        }
        index
    }

    pub fn tokens_of(&self, at: usize) -> &[usize] {
        match &self.results[at] {
            Result_::Option { tokens, .. } | Result_::Argument { tokens, .. } => tokens,
            Result_::Command { .. } => &[],
        }
    }

    /// Значения параметра, набранные в строке (`GetResult(option).Tokens`); None — параметра в строке нет.
    pub fn option_tokens(&self, option: &OptSpec) -> Option<Vec<String>> {
        let at = self.option_result(option)?;
        Some(self.tokens_of(at).iter().map(|t| self.tokens[*t].value.clone()).collect())
    }

    /// `GetValue` параметра с одним значением: ровно одно набранное значение, иначе (нет, ошибка разбора) — None.
    pub fn single_value(&self, option: &OptSpec) -> Option<String> {
        match self.option_tokens(option)?.as_slice() {
            [one] => Some(one.clone()),
            _ => None,
        }
    }

    /// `GetValue` аргумента команды: его единственный токен.
    pub fn argument_value(&self, argument: &ArgSpec) -> Option<String> {
        let at = self.argument_result(argument)?;
        match self.tokens_of(at) {
            [one] => Some(self.tokens[*one].value.clone()),
            _ => None,
        }
    }

    /// Результаты аргументов, дочерние результату `at` (`CommandResult.Children.OfType<ArgumentResult>()`).
    pub fn argument_children(&self, at: usize) -> impl Iterator<Item = (&'a ArgSpec, &[usize])> + '_ {
        self.results.iter().filter_map(move |r| match r {
            Result_::Argument { argument, parent, tokens } if *parent == at => Some((*argument, tokens.as_slice())),
            _ => None,
        })
    }

    /// Результаты параметров, дочерние результату `at`.
    pub fn option_children(&self, at: usize) -> impl Iterator<Item = (usize, &'a OptSpec)> + '_ {
        self.results.iter().enumerate().filter_map(move |(i, r)| match r {
            Result_::Option { option, parent, .. } if *parent == at => Some((i, *option)),
            _ => None,
        })
    }

    fn limit_reached(&self, at: usize) -> bool {
        let Result_::Option { option, tokens, .. } = &self.results[at] else {
            return false;
        };
        shape(option).max == tokens.len()
    }

    /// `CompletionContext.GetWordToComplete` с курсором в конце: кусок строки после последнего пробела.
    pub fn word_to_complete(&self) -> String {
        if self.line.trim().is_empty() {
            return String::new();
        }
        match self.line.rfind(' ') {
            Some(at) => self.line[at + 1..].to_string(),
            None => self.line.clone(),
        }
    }

    /// `ParseResult.SymbolToComplete`: самая внутренняя команда или последний её параметр, который ещё примет значение.
    pub fn symbol_to_complete(&self) -> Result<Sym<'a>, Abort> {
        let mut current = Sym::Command(self.innermost_command());
        let word = self.word_to_complete();
        for (at, option) in self.option_children(self.innermost) {
            let accepts = if !self.limit_reached(at) {
                true
            } else if !word.is_empty() {
                // `Tokens.Last(t => t.Value == word)`: такого токена нет — исключение, и директива молчит.
                let Some(last) = self.tokens.iter().rposition(|t| t.value == word) else {
                    return Err(Abort);
                };
                self.tokens_of(at).iter().any(|t| self.tokens[*t] == self.tokens[last])
            } else {
                false
            };
            if accepts {
                current = Sym::Option(option);
            }
        }
        Ok(current)
    }

    /// Имена параметров самой внутренней команды, у которых набрано максимум значений (`OptionsWithArgumentLimitReached`).
    pub fn options_with_limit_reached(&self) -> Vec<&'static str> {
        self.option_children(self.innermost)
            .filter(|(at, _)| self.limit_reached(*at))
            .flat_map(|(_, option)| option.all_names())
            .collect()
    }

    /// Параметры, набранные у самой внутренней команды (`CommandResult.Children.OfType<OptionResult>()`).
    pub fn used_options(&self) -> Vec<&'a OptSpec> {
        self.option_children(self.innermost).map(|(_, o)| o).collect()
    }
}

/// `bool.TryParse`: true/false без учёта регистра, пробелы по краям допустимы.
fn parses_as_bool(value: &str) -> bool {
    let value = value.trim_matches(|c: char| c.is_whitespace() || c == '\0');
    value.eq_ignore_ascii_case("true") || value.eq_ignore_ascii_case("false")
}

/// `OrdinalIgnoreCase` .NET: посимвольно, простое соответствие верхнего регистра.
pub fn fold(text: &str) -> Vec<char> {
    text.chars().map(upper_char).collect()
}

pub fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    let (t, p) = (fold(text), fold(prefix));
    t.len() >= p.len() && t[..p.len()] == p[..]
}

/// `ContainsCaseInsensitive`.
pub fn contains_ignore_case(text: &str, part: &str) -> bool {
    let (t, p) = (fold(text), fold(part));
    p.is_empty() || t.windows(p.len()).any(|w| w == &p[..])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_command_line_is_split_like_system_commandline() {
        assert_eq!(split_command_line("task get \"a b\" c"), ["task", "get", "a b", "c"]);
        // Незакрытая кавычка в начале слова: слово до конца строки теряется (так у System.CommandLine).
        assert_eq!(split_command_line("status get \"В р"), ["status", "get"]);
        assert_eq!(split_command_line("x a\"b c\"d"), ["x", "ab cd"]);
        assert_eq!(split_command_line("  "), Vec::<String>::new());
        assert_eq!(split_command_line("x \"\" y"), ["x", "", "y"]);
    }

    #[test]
    fn options_bundles_and_the_program_name_are_tokenized() {
        let root = crate::spec::root();
        let tree = Tree::new(&root);
        let kinds = |line: &str| -> Vec<(String, Kind)> {
            let Ok(tokens) = tokenize(&tree, split_command_line(line)) else {
                panic!()
            };
            tokens.into_iter().map(|t| (t.value, t.kind)).collect()
        };
        let k = kinds("tasker task list -qp X --status=A");
        assert_eq!(
            k.iter().map(|(v, _)| v.as_str()).collect::<Vec<_>>(),
            ["tasker", "task", "list", "-q", "-p", "X", "--status", "A"]
        );
        assert_eq!(k[3].1, Kind::Option);
        // Имя программы первым словом — корень; иначе корень подставляется сам.
        assert_eq!(kinds("task")[0].0, "tasker");
        assert_eq!(kinds("task")[1].1, Kind::Command);
        // Корень, набранный ещё раз, — просто аргумент.
        assert_eq!(kinds("tasker tasker")[1].1, Kind::Argument);
    }

    #[test]
    fn a_flag_takes_only_a_boolean_value() {
        let root = crate::spec::root();
        let tree = Tree::new(&root);
        let Ok(parsed) = parse(&tree, "task list --all true x") else {
            panic!()
        };
        let all = parsed.used_options()[0];
        assert_eq!(parsed.option_tokens(all).unwrap(), ["true"]);
        let Ok(parsed) = parse(&tree, "task list --all x") else { panic!() };
        assert!(parsed.option_tokens(parsed.used_options()[0]).unwrap().is_empty());
    }
}
