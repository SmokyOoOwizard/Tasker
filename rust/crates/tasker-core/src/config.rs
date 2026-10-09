//! `ConfigDiagnostics`: опечатки в настройках. Парсер .NET молча пропускает всё, что не знает, — и `--sqllite=tasker.db` просто не
//! действует. Здесь такие случаи находят и описывают словами; консоль пишет их в stderr с префиксом `Warning: `.
//!
//! - Переменная окружения `TASKER_*`, которой нет среди настроек и известных переменных Tasker, — предупреждение (с подсказкой
//!   «возможно, вы имели в виду», если похожая есть).
//! - Аргумент, похожий на известный (расстояние до алиаса или ключа не больше пары правок), но не совпадающий с ним, —
//!   предупреждение с подсказкой. Аргумент с префиксом `TASKER_`, которого нет, — тоже. Остальные незнакомые аргументы не трогаем.
//!
//! Ключи и алиасы в .NET собираются рефлексией по классам `*Configs`; здесь они перечислены явно, в том же порядке.
use crate::validate::to_upper_invariant;

/// Переменные `TASKER_*`, которые Tasker читает не как группы настроек, а напрямую.
pub const OTHER_VARIABLES: [&str; 14] = [
    "TASKER_HOME",
    "TASKER_PROJECT",
    "TASKER_LANG",
    "TASKER_WIDTH",
    "TASKER_SERVICE_DIR",
    "TASKER_SERVICE_LABEL",
    "TASKER_FRONTEND_DEV_URL",
    "TASKER_PROFILE",
    "TASKER_MCP_OPEN_WAIT_MS",
    "TASKER_MCP_OPEN_DELAY_MS",
    "TASKER_MCP_DRAIN_QUIET_MS",
    "TASKER_MCP_DRAIN_TIMEOUT_MS",
    "TASKER_MCP_SUPERVISOR",
    "TASKER_MCP_HANDOFF",
];

/// Полные ключи групп настроек (`ConfigKeys.For`): AuthConfigs, McpConfigs, FilesConfigs, DbConfigs.
pub const FULL_KEYS: [&str; 7] = [
    "TASKER_AUTH_CONFIGS_SIGNING_KEY",
    "TASKER_AUTH_CONFIGS_ACCESS_TOKEN_LIFETIME",
    "TASKER_AUTH_CONFIGS_REFRESH_TOKEN_LIFETIME",
    "TASKER_MCP_CONFIGS_PORT",
    "TASKER_FILES_CONFIGS_PATH",
    "TASKER_DB_CONFIGS_POSTGRES_CONNECTION_STRING",
    "TASKER_DB_CONFIGS_SQLITE_FILE",
];

/// Короткие алиасы аргументов (`[ConfigAlias]`).
pub const ALIASES: [&str; 6] = ["jwtkey", "mcpport", "files", "postgres", "db", "sqlite"];

// Аргументы самого ASP.NET Core и хоста: они не настройки Tasker, но и не опечатки.
const FRAMEWORK_ARGUMENTS: [&str; 17] = [
    "urls",
    "environment",
    "contentRoot",
    "applicationName",
    "webroot",
    "hostingStartupAssemblies",
    "hostingStartupExcludeAssemblies",
    "preventHostingStartup",
    "startupAssembly",
    "captureStartupErrors",
    "detailedErrors",
    "https_port",
    "shutdownTimeoutSeconds",
    "help",
    "h",
    "version",
    "?",
];

const PREFIX: &str = "TASKER_";

/// Предупреждения по аргументам командной строки и именам переменных окружения.
pub fn check<'a>(args: &[String], environment: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let mut warnings = check_arguments(args);
    warnings.extend(check_environment(environment));
    warnings
}

/// Только переменные окружения процесса — для консоли, у которой нет групп настроек в аргументах.
pub fn check_process_environment() -> Vec<String> {
    let names: Vec<String> = std::env::vars_os().map(|(k, _)| k.to_string_lossy().into_owned()).collect();
    check_environment(names.iter().map(String::as_str))
}

/// Переменные `TASKER_*`, не являющиеся настройками, в порядке имён (ordinal).
pub fn check_environment<'a>(environment: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let known: Vec<&str> = FULL_KEYS.iter().chain(OTHER_VARIABLES.iter()).copied().collect();
    let mut names: Vec<&str> = environment.into_iter().filter(|x| x.starts_with(PREFIX)).collect();
    names.sort_unstable();
    names
        .into_iter()
        .filter(|name| !known.contains(name))
        .map(|name| {
            let hint = suggest(name, &known).map(|s| format!(" Did you mean {s}?")).unwrap_or_default();
            format!("Environment variable {name} is not a Tasker setting and is ignored.{hint}")
        })
        .collect()
}

pub fn check_arguments(args: &[String]) -> Vec<String> {
    let known: Vec<&str> = ALIASES.iter().chain(FULL_KEYS.iter()).copied().collect();
    let mut warnings = Vec::new();
    let mut i = 0;
    while i < args.len() {
        let raw = &args[i];
        i += 1;
        if !raw.starts_with('-') {
            continue;
        }
        let token = raw.trim_start_matches('-');
        let name = token.split_once('=').map(|(n, _)| n).unwrap_or(token);
        // «--ключ значение»: следующий аргумент — значение, а не ещё один ключ (как в ArgsConfigSource).
        if !token.contains('=') && i < args.len() && !args[i].starts_with('-') {
            i += 1;
        }
        if name.is_empty()
            || contains_ignore_case(&known, name)
            || contains_ignore_case(&FRAMEWORK_ARGUMENTS, name)
            || name.contains(':')
            || name.contains("__")
        {
            continue;
        }
        if let Some(suggestion) = suggest(name, &known) {
            warnings.push(format!("Unknown argument --{name} is ignored. Did you mean --{suggestion}?"));
        } else if to_upper_invariant(name).starts_with(PREFIX) {
            warnings.push(format!("Unknown argument --{name} is ignored: it is not a Tasker setting."));
        }
    }
    warnings
}

fn contains_ignore_case(list: &[&str], name: &str) -> bool {
    list.iter().any(|x| crate::validate::eq_ignore_case(x, name))
}

/// Самое похожее известное имя (без учёта регистра), если оно отличается не больше чем парой правок
/// (порог `clamp(len / 3, 1, 3)` по длине в единицах UTF-16; при равном расстоянии — первое в списке).
pub fn suggest<'a>(name: &str, known: &[&'a str]) -> Option<&'a str> {
    let limit = (crate::validate::utf16_len(name) / 3).clamp(1, 3);
    let mut best = None;
    let mut best_distance = usize::MAX;
    for candidate in known {
        let distance = distance(name, candidate);
        if distance < best_distance {
            best = Some(*candidate);
            best_distance = distance;
        }
    }
    if best_distance <= limit { best } else { None }
}

/// Расстояние Дамерау — Левенштейна (вставка, удаление, замена, перестановка соседних букв) без учёта регистра,
/// по единицам UTF-16, как в .NET.
pub fn distance(a: &str, b: &str) -> usize {
    let a: Vec<u16> = to_upper_invariant(a).encode_utf16().collect();
    let b: Vec<u16> = to_upper_invariant(b).encode_utf16().collect();
    let w = b.len() + 1;
    let mut d = vec![0usize; (a.len() + 1) * w];
    for i in 0..=a.len() {
        d[i * w] = i;
    }
    for (j, cell) in d.iter_mut().enumerate().take(b.len() + 1) {
        *cell = j;
    }
    for i in 1..=a.len() {
        for j in 1..=b.len() {
            let cost = usize::from(a[i - 1] != b[j - 1]);
            let mut value = (d[(i - 1) * w + j] + 1)
                .min(d[i * w + j - 1] + 1)
                .min(d[(i - 1) * w + j - 1] + cost);
            if i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1] {
                value = value.min(d[(i - 2) * w + j - 2] + 1);
            }
            d[i * w + j] = value;
        }
    }
    d[a.len() * w + b.len()]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(list: &[&str]) -> Vec<String> {
        check(&list.iter().map(|s| s.to_string()).collect::<Vec<_>>(), [])
    }

    fn env(list: &[&str]) -> Vec<String> {
        check(&[], list.iter().copied())
    }

    #[test]
    fn a_typo_in_an_alias_is_reported_with_a_hint() {
        assert_eq!(
            args(&["--sqllite=tasker.db"]),
            ["Unknown argument --sqllite is ignored. Did you mean --sqlite?"]
        );
    }

    #[test]
    fn swapped_letters_count_as_one_typo() {
        assert_eq!(
            args(&["--mcpprot", "6000"]),
            ["Unknown argument --mcpprot is ignored. Did you mean --mcpport?"]
        );
    }

    #[test]
    fn the_hint_is_found_in_every_form_of_an_argument() {
        assert!(args(&["--flies", "data"])[0].contains("Did you mean --files?"));
        assert!(args(&["-fils=data"])[0].contains("Did you mean --files?"));
        assert!(args(&["--postgress=Host=a"])[0].contains("Did you mean --postgres?"));
    }

    #[test]
    fn known_arguments_are_silent_in_any_form_and_case() {
        assert!(
            args(&[
                "--sqlite=a.db",
                "--files",
                "data",
                "--postgres=Host=a",
                "--db=b",
                "--mcpport",
                "5719",
                "--jwtkey=k"
            ])
            .is_empty()
        );
        assert!(args(&["-sqlite=a.db", "--SQLITE", "a.db", "---Files=data", "--MCPPORT=1"]).is_empty());
        assert!(args(&["--TASKER_DB_CONFIGS_SQLITE_FILE=a.db", "--tasker_mcp_configs_port=5719"]).is_empty());
    }

    #[test]
    fn a_value_is_not_mistaken_for_an_argument() {
        assert!(args(&["--sqlite", "sqllite_value", "--files", "sqlite"]).is_empty());
    }

    #[test]
    fn arguments_of_aspnet_core_are_not_typos() {
        assert!(
            args(&[
                "--urls",
                "http://127.0.0.1:0",
                "--environment",
                "Development",
                "--contentRoot=/x",
                "--applicationName=a",
                "--Logging:LogLevel:Default=Debug",
                "--Kestrel__Endpoints__Http__Url=http://*:1",
                "--help",
                "-h",
                "-?",
            ])
            .is_empty()
        );
    }

    #[test]
    fn an_argument_far_from_every_known_one_is_left_alone() {
        assert!(args(&["--banana", "--some-other-flag=1"]).is_empty());
    }

    #[test]
    fn a_tasker_prefixed_key_that_does_not_exist_is_reported() {
        assert_eq!(
            args(&["--TASKER_DB_CONFIGS_SQLITE_FIL=a.db"]),
            ["Unknown argument --TASKER_DB_CONFIGS_SQLITE_FIL is ignored. Did you mean --TASKER_DB_CONFIGS_SQLITE_FILE?"]
        );
        assert_eq!(
            args(&["--TASKER_BANANA=1"]),
            ["Unknown argument --TASKER_BANANA is ignored: it is not a Tasker setting."]
        );
    }

    #[test]
    fn several_typos_give_several_warnings_in_order() {
        let warnings = args(&["--sqllite=a.db", "--files=data", "--mcpprot=1"]);
        assert_eq!(warnings.len(), 2);
        assert!(warnings[0].contains("--sqllite"));
        assert!(warnings[1].contains("--mcpprot"));
    }

    #[test]
    fn a_typo_in_a_variable_is_reported_with_a_hint() {
        assert_eq!(
            env(&["TASKER_DB_CONFIGS_SQLITE_FIL"]),
            [
                "Environment variable TASKER_DB_CONFIGS_SQLITE_FIL is not a Tasker setting and is ignored. Did you mean TASKER_DB_CONFIGS_SQLITE_FILE?"
            ]
        );
    }

    #[test]
    fn typos_of_the_other_tasker_variables_are_found_too() {
        assert!(env(&["TASKER_PROJCT"])[0].contains("Did you mean TASKER_PROJECT?"));
        assert!(env(&["TASKER_HOEM"])[0].contains("Did you mean TASKER_HOME?"));
        assert!(env(&["TASKER_SERVICE_LABLE"])[0].contains("Did you mean TASKER_SERVICE_LABEL?"));
    }

    #[test]
    fn a_tasker_variable_nobody_knows_is_reported_without_a_hint() {
        assert_eq!(
            env(&["TASKER_SOMETHING_ELSE_ENTIRELY"]),
            ["Environment variable TASKER_SOMETHING_ELSE_ENTIRELY is not a Tasker setting and is ignored."]
        );
    }

    #[test]
    fn known_and_foreign_variables_are_silent() {
        let mut known: Vec<&str> = OTHER_VARIABLES.to_vec();
        known.extend([
            "TASKER_DB_CONFIGS_SQLITE_FILE",
            "TASKER_MCP_CONFIGS_PORT",
            "PATH",
            "SQLITE",
            "MY_TASKER_THING",
            "tasker_home",
        ]);
        assert!(env(&known).is_empty());
    }

    #[test]
    fn the_variables_are_listed_in_a_stable_order() {
        let warnings = env(&["TASKER_ZZZ_UNKNOWN", "TASKER_AAA_UNKNOWN"]);
        assert!(warnings[0].contains("TASKER_AAA_UNKNOWN"));
        assert!(warnings[1].contains("TASKER_ZZZ_UNKNOWN"));
    }

    #[test]
    fn the_nearest_known_name_is_suggested() {
        let known = ["files", "sqlite", "postgres", "db", "mcpport", "jwtkey"];
        for (typo, expected) in [
            ("sqllite", "sqlite"),
            ("SQLITE", "sqlite"),
            ("sqlte", "sqlite"),
            ("mcpprot", "mcpport"),
            ("jwtky", "jwtkey"),
        ] {
            assert_eq!(suggest(typo, &known), Some(expected), "{typo}");
        }
        for typo in ["banana", "sqlitefile", "xyz"] {
            assert_eq!(suggest(typo, &known), None, "{typo}");
        }
    }

    #[test]
    fn distance_counts_insertions_deletions_substitutions_and_swaps() {
        for (a, b, expected) in [
            ("abc", "abc", 0),
            ("abc", "ABC", 0),
            ("abc", "abd", 1),
            ("abc", "ab", 1),
            ("abc", "abcd", 1),
            ("abc", "acb", 1),
            ("kitten", "sitting", 3),
            ("", "abc", 3),
        ] {
            assert_eq!(distance(a, b), expected, "{a} {b}");
        }
    }

    #[test]
    fn process_environment_is_checked() {
        let warnings = check_process_environment();
        assert!(warnings.iter().all(|w| !w.contains("TASKER_HOME") && !w.contains("TASKER_PROJECT")));
    }
}
