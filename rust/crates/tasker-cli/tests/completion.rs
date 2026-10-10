//! Автодополнение по Tab — сценарии `CompletionTests` и `ManualCompletionTests` (.NET) против бинарника: директива `[suggest]`
//! (команды, параметры, значения из области), тишина и неизменность данных, скрипты оболочек (bash и zsh с подставным `tasker`).
//! Область SQLite этой сборкой не открывается, поэтому сценарии `Storages` идут только на папке; `--sqlite` проверяется как
//! «пусто и ничего не создано». Полная сверка с .NET-консолью на ~18 тыс. строк — `scripts/completion-diff.py`.

use std::path::{Path, PathBuf};
use std::process::Command;

struct Ws {
    root: PathBuf,
    home: PathBuf,
}

impl Ws {
    fn new() -> Ws {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(format!("completion-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(root.join("home")).unwrap();
        let root = canonical(&root);
        Ws {
            home: root.join("home"),
            root,
        }
    }

    fn command(&self, args: &[&str]) -> Command {
        let mut command = Command::new(env!("CARGO_BIN_EXE_tasker"));
        command
            .args(args)
            .env("TASKER_HOME", &self.home)
            .env_remove("TASKER_PROJECT")
            .env_remove("TASKER_LANG")
            .env_remove("TASKER_PROFILE")
            .current_dir(&self.root);
        command
    }

    fn ok(&self, args: &[&str]) -> String {
        let mut all: Vec<&str> = args.to_vec();
        let root = self.root.to_string_lossy().into_owned();
        all.extend(["-w", &root]);
        let output = self.command(&all).output().unwrap();
        assert!(output.status.success(), "{args:?}: {}", String::from_utf8_lossy(&output.stderr));
        String::from_utf8(output.stdout).unwrap()
    }

    /// Что предложит Tab после `line` (курсор в конце), без области в строке: текущая папка — корень теста.
    fn suggest(&self, line: &str) -> Vec<String> {
        let output = self
            .command(&[&format!("[suggest:{}]", line.chars().count()), line])
            .output()
            .unwrap();
        assert_eq!(output.status.code(), Some(0), "{line}");
        assert!(output.stderr.is_empty(), "{line}: {}", String::from_utf8_lossy(&output.stderr));
        String::from_utf8(output.stdout).unwrap().lines().map(str::to_string).collect()
    }

    /// То же в этой области (`--workspace "<корень>" строка`), как `Suggest(ws, line)` в .NET.
    fn suggest_in(&self, line: &str) -> Vec<String> {
        self.suggest(&format!("--workspace \"{}\" {line}", self.root.display()))
    }

    /// Только значения: без параметров.
    fn values(&self, line: &str) -> Vec<String> {
        self.suggest_in(line).into_iter().filter(|x| !x.starts_with('-')).collect()
    }

    /// Проект «Мой проект», статусы, набор, тип «Фича», серия TSK и 11 задач, перечисление и поля, доска с двумя колонками.
    fn seed(&self) {
        self.ok(&["project", "create", "Мой проект"]);
        for status in ["В работе", "Готово", "Лишний"] {
            self.ok(&["status", "create", status]);
        }
        self.ok(&["status-set", "create", "Основной", "--status", "В работе", "Готово"]);
        self.ok(&["task-type", "create", "Фича", "--status-set", "Основной"]);
        self.ok(&["series", "create", "Задачи", "--prefix", "TSK"]);
        for i in 1..=11 {
            self.ok(&["task", "create", &format!("Задача {i}"), "--type", "Фича", "--series", "TSK"]);
        }
        self.ok(&["enum", "create", "Приоритет", "--value", "Высокий", "Низкий", "Очень срочно"]);
        self.ok(&["field", "create", "Приоритет", "--type", "enum", "--enum", "Приоритет"]);
        self.ok(&["field", "create", "Готово", "--type", "bool"]);
        self.ok(&["field", "create", "StoryPoints", "--type", "int"]);
        self.ok(&[
            "board",
            "create",
            "Основная доска",
            "--status-set",
            "Основной",
            "--column",
            "Все=В работе",
            "--column",
            "Сделано=Готово",
        ]);
    }
}

impl Drop for Ws {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn v(items: &[&str]) -> Vec<String> {
    items.iter().map(|x| x.to_string()).collect()
}

// ---- команды и параметры (без рабочей области) ----

#[test]
fn commands_options_and_global_options_are_completed() {
    let ws = Ws::new();
    for (line, expected) in [
        ("pro", "project"),
        ("task cr", "create"),
        ("task-type up", "update"),
        ("mcp workspace re", "remove"),
        ("task create X --st", "--status"),
        ("task list --fi", "--field"),
        ("--js", "--json"),
        ("task list --qui", "--quiet"),
        ("status list --qui", "--quiet"),
        ("sync --qui", "--quiet"),
        ("task list --trun", "--truncate"),
        ("task list --no-t", "--no-truncate"),
        ("board show X --wid", "--width"),
        ("project get --wo", "--workspace"),
    ] {
        assert!(ws.suggest(line).contains(&expected.to_string()), "{line} → {expected}");
    }
}

#[test]
fn options_are_suggested_after_a_multi_value_option() {
    let ws = Ws::new();
    for (line, expected) in [
        ("task list --status Бэклог --al", "--all"),
        ("task list --status Бэклог -", "--all"),
        ("task list --status Бэклог --status В --al", "--all"),
        ("task list --type Баг --al", "--all"),
        ("task list --series TSK --al", "--all"),
        ("task list --field Priority=High --al", "--all"),
        ("task list --status A B --al", "--all"),
        ("task list --status Бэклог --stat", "--status"),
        ("task list --status Бэклог --ty", "--type"),
        ("task list --field Priority=High --fi", "--field"),
        ("task list --sort status --al", "--all"),
        ("enum create X --value A B --js", "--json"),
        ("status-set create X --status A B --js", "--json"),
    ] {
        assert!(ws.suggest(line).contains(&expected.to_string()), "{line} → {expected}");
    }
}

/// Сторож: после любого многозначного параметра любой команды «--параметр значение --» предлагает параметры этой команды.
#[test]
fn options_follow_every_multi_value_option_of_every_command() {
    use tasker_cli::spec::{CommandSpec, OptKind};
    fn walk(command: &CommandSpec, path: &str, cases: &mut Vec<(String, String, Vec<String>)>) {
        for many in command
            .options
            .iter()
            .filter(|o| !o.hidden && matches!(o.kind, OptKind::Value { multiple: true, .. }))
        {
            let others = command
                .options
                .iter()
                .filter(|o| !o.hidden && o.name != many.name && o.name.starts_with("--"))
                .map(|o| o.name.to_string())
                .collect();
            cases.push((path.to_string(), many.name.to_string(), others));
        }
        for sub in &command.subcommands {
            walk(sub, format!("{path} {}", sub.name).trim(), cases);
        }
    }
    let mut cases = Vec::new();
    walk(&tasker_cli::spec::root(), "", &mut cases);
    assert!(!cases.is_empty());
    for (path, option, others) in cases {
        let line = format!("{path} {option} value --");
        let suggestions = tasker_cli::suggest::complete(&line);
        assert!(suggestions.contains(&option), "'{line}' does not repeat {option}: {suggestions:?}");
        for other in others {
            assert!(suggestions.contains(&other), "'{line}' does not suggest {other}: {suggestions:?}");
        }
    }
}

#[test]
fn width_short_options_and_the_cursor_position() {
    let ws = Ws::new();
    assert!(ws.suggest("task list --width ").is_empty());
    let suggestions = ws.suggest("task list ");
    for short in ["-w", "-p", "-q"] {
        assert!(suggestions.contains(&short.to_string()));
    }
    assert!(!suggestions.iter().any(|x| x.starts_with('/') || x == "-?"));
    // Курсор после «tas»: хвост строки не мешает.
    let output = ws.command(&["[suggest:3]", "tas project list"]).output().unwrap();
    assert_eq!(String::from_utf8(output.stdout).unwrap(), "task\ntask-type\n");
}

// ---- значения из области ----

#[test]
fn values_come_from_the_workspace() {
    let ws = Ws::new();
    ws.seed();

    // Имена сущностей.
    assert_eq!(ws.values("project get "), v(&["Мой проект"]));
    assert_eq!(ws.values("status get "), v(&["В работе", "Готово", "Лишний"]));
    assert_eq!(ws.values("status-set get "), v(&["Основной"]));
    assert_eq!(ws.values("task-type get "), v(&["Фича"]));
    assert_eq!(ws.values("series get "), v(&["TSK"]));
    assert_eq!(ws.values("board get "), v(&["Основная доска"]));
    assert_eq!(ws.values("field get "), v(&["StoryPoints", "Готово", "Приоритет"]));
    assert_eq!(ws.values("enum get "), v(&["Приоритет"]));
    assert!(ws.values("link-type get ").contains(&"Blocks".to_string()));

    // Отбор по набранному началу без учёта регистра; кавычки и «\ ».
    assert_eq!(ws.values("status get го"), v(&["Готово"]));
    assert_eq!(ws.values("status get В"), v(&["В работе"]));
    assert!(ws.values("status get Нет").is_empty());
    assert_eq!(ws.values("status get \"В р"), v(&["В работе"]));
    assert_eq!(ws.values("status get 'В р"), v(&["В работе"]));
    assert_eq!(ws.values("status get В\\ р"), v(&["В работе"]));
    assert_eq!(ws.values("board get Основная\\ д"), v(&["Основная доска"]));

    // Статусы задачи — по уже набранному типу.
    assert_eq!(ws.values("task list --status "), v(&["В работе", "Готово", "Лишний"]));
    assert_eq!(ws.values("task create X --type Фича --status "), v(&["В работе", "Готово"]));
    assert_eq!(ws.values("task create X --type "), v(&["Фича"]));

    // Колонки доски после её имени.
    assert_eq!(ws.values("board tasks "), v(&["Основная доска"]));
    assert_eq!(ws.values("board tasks \"Основная доска\" "), v(&["Все", "Сделано"]));
    assert_eq!(ws.values("board tasks Основная\\ доска Сд"), v(&["Сделано"]));
}

#[test]
fn task_references_links_and_lock_entities() {
    let ws = Ws::new();
    ws.seed();

    assert_eq!(ws.values("task get T"), v(&["TSK-"]));
    assert_eq!(ws.values("task get TSK-1"), v(&["TSK-1", "TSK-10", "TSK-11"]));
    assert_eq!(ws.values("task get TSK-2"), v(&["TSK-2"]));
    assert_eq!(ws.values("task get TSK-"), (1..=11).map(|x| format!("TSK-{x}")).collect::<Vec<_>>());
    assert!(ws.values("task get TSK-x").is_empty());
    assert_eq!(ws.values("task link TSK-1 blocks TSK-11"), v(&["TSK-11"]));
    assert!(ws.values("task link TSK-1 ").contains(&"is blocked by".to_string()));
    assert!(ws.values("task link TSK-1 bl").contains(&"blocks".to_string()));
    assert_eq!(ws.values("series add-task TSK TSK-1"), v(&["TSK-1", "TSK-10", "TSK-11"]));

    assert!(ws.values("lock acquire ").contains(&"taskType".to_string()));
    assert_eq!(ws.values("lock acquire taskType "), v(&["Фича"]));
    assert_eq!(ws.values("lock release task TSK-2"), v(&["TSK-2"]));
    assert_eq!(ws.values("lock show board "), v(&["Основная доска"]));
}

#[test]
fn fields_columns_enums_and_hierarchy() {
    let ws = Ws::new();
    ws.seed();

    assert_eq!(ws.values("task list --field "), v(&["StoryPoints=", "Готово=", "Приоритет="]));
    assert_eq!(ws.values("task list --field Пр"), v(&["Приоритет="]));
    assert_eq!(
        ws.values("task list --field Приоритет="),
        v(&["Приоритет=Высокий", "Приоритет=Низкий", "Приоритет=Очень срочно"])
    );
    assert_eq!(
        ws.values("task create X --type Фича --field Приоритет=Оч"),
        v(&["Приоритет=Очень срочно"])
    );
    assert_eq!(
        ws.values("task update TSK-1 --field Приоритет=Очень\\ "),
        v(&["Приоритет=Очень срочно"])
    );
    assert_eq!(
        ws.values("board show Основная\\ доска --field Готово="),
        v(&["Готово=false", "Готово=true"])
    );
    assert!(ws.values("task list --field \"StoryPoints=").is_empty());
    assert_eq!(ws.values("task list --field Story"), v(&["StoryPoints="]));
    assert_eq!(
        ws.values("task list --field Приоритет!="),
        v(&["Приоритет!=Высокий", "Приоритет!=Низкий", "Приоритет!=Очень срочно"])
    );
    assert!(ws.values("task list --field StoryPoints>=").is_empty());
    assert!(ws.values("task list --field \"StoryPoints<").is_empty());
    assert_eq!(
        ws.values("task list --field Приоритет:"),
        v(&["Приоритет:attached", "Приоритет:detached", "Приоритет:set", "Приоритет:unset"])
    );

    let create = "board create X --status-set Основной";
    assert!(ws.values(&format!("{create} --column Вс")).is_empty());
    assert_eq!(
        ws.values(&format!("{create} --column Все=")),
        v(&["Все=В работе", "Все=Готово", "Все=Лишний"])
    );
    assert_eq!(
        ws.values(&format!("{create} --column \"Все=В работе,Го")),
        v(&["Все=В работе,Готово"])
    );
    assert_eq!(
        ws.values(&format!("{create} --column Все=Готово --column Готово=Лишний --column-filter ")),
        v(&["Все:", "Готово:"])
    );
    assert_eq!(
        ws.values(&format!("{create} --column Все=Готово --column-filter Все:Пр")),
        v(&["Все:Приоритет="])
    );
    assert_eq!(
        ws.values(&format!("{create} --column Все=Готово --column-filter Все:Приоритет=")),
        v(&["Все:Приоритет=Высокий", "Все:Приоритет=Низкий", "Все:Приоритет=Очень срочно"])
    );
    assert_eq!(
        ws.values(&format!("{create} --column Все=Готово --column-filter Все:Готово=")),
        v(&["Все:Готово=false", "Все:Готово=true"])
    );
    assert!(
        ws.values(&format!("{create} --column Все=Готово --column-filter Все:StoryPoints>="))
            .is_empty()
    );
    assert_eq!(
        ws.values("board update Основная\\ доска --column-filter "),
        v(&["Все:", "Сделано:"])
    );
    assert_eq!(
        ws.values("board update Основная\\ доска --column-filter Сделано:Пр"),
        v(&["Сделано:Приоритет="])
    );

    assert_eq!(ws.values("field create X --type enum --enum "), v(&["Приоритет"]));
    assert!(ws.values("field create X --type ").contains(&"enum".to_string()));
    assert_eq!(ws.values("field update Готово --several "), v(&["clear", "keep-first"]));
    assert_eq!(
        ws.values("enum update Приоритет --remove-value "),
        v(&["Высокий", "Низкий", "Очень срочно"])
    );
    assert_eq!(
        ws.values("enum update Приоритет --rename-value "),
        v(&["Высокий=", "Низкий=", "Очень срочно="])
    );
    assert_eq!(
        ws.values("task-type update Фича --add-field "),
        v(&["StoryPoints", "Готово", "Приоритет"])
    );
    assert_eq!(ws.values("task-type update Фича --field Приоритет:"), v(&["Приоритет:required"]));
    assert_eq!(ws.values("task-type create X --status-set "), v(&["Основной"]));
    assert_eq!(ws.values("board create X --status-set "), v(&["Основной"]));
    assert_eq!(
        ws.values("status-set update Основной --status "),
        v(&["В работе", "Готово", "Лишний"])
    );

    assert!(ws.suggest_in("task list --").contains(&"--flat".to_string()));
    for option in ["--parent", "--parent-type"] {
        assert!(ws.suggest_in("task create X --type Фича --").contains(&option.to_string()));
    }
    for option in ["--add-parent", "--remove-parent", "--parent-type"] {
        assert!(ws.suggest_in("task update TSK-1 --").contains(&option.to_string()));
    }
    assert!(
        ws.suggest_in("link-type create X --outward a --")
            .contains(&"--hierarchical".to_string())
    );
    assert_eq!(ws.values("task create X --type Фича --parent T"), v(&["TSK-"]));
    assert_eq!(ws.values("task update TSK-1 --add-parent TSK-2"), v(&["TSK-2"]));
    assert_eq!(ws.values("task update TSK-1 --remove-parent TSK-3"), v(&["TSK-3"]));
    assert_eq!(
        ws.values("task update TSK-1 --add-parent TSK-2 --parent-type "),
        v(&["Parent/Child"])
    );
    // true/false у флагов предлагает сама библиотека разбора .NET — с её регистром.
    assert_eq!(ws.values("link-type create X --outward a --hierarchical "), v(&["False", "True"]));
    assert_eq!(ws.values("task list --all "), v(&["False", "True"]));
}

#[test]
fn the_project_comes_from_the_option_or_the_only_project() {
    let ws = Ws::new();
    ws.seed();
    ws.ok(&["project", "create", "Второй"]);

    assert!(ws.values("status get ").is_empty());
    assert_eq!(
        ws.values("status get --project \"Мой проект\" "),
        v(&["В работе", "Готово", "Лишний"])
    );
    assert_eq!(ws.values("task list --project "), v(&["Второй", "Мой проект"]));
    assert_eq!(ws.values("project get Вт"), v(&["Второй"]));
    // Строка, начатая с короткого параметра, теряет его, как у .NET (TSK-151): «-p Второй» не действует.
    assert!(ws.suggest("-p Второй status get ").iter().all(|x| x.starts_with('-')));
}

#[test]
fn mcp_workspaces_come_from_the_global_settings() {
    let ws = Ws::new();
    let folder = ws.root.join("ws one");
    std::fs::create_dir_all(&folder).unwrap();
    let output = ws.command(&["mcp", "workspace", "add", folder.to_str().unwrap()]).output().unwrap();
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    let values: Vec<String> = ws
        .suggest("mcp workspace remove ")
        .into_iter()
        .filter(|x| !x.starts_with('-'))
        .collect();
    assert_eq!(values, vec![folder.to_string_lossy().into_owned()]);
    assert!(ws.suggest("mcp workspace remove /nothing").iter().all(|x| x.starts_with('-')));
}

#[test]
fn at_most_fifty_values_are_suggested() {
    let ws = Ws::new();
    ws.ok(&["project", "create", "Много"]);
    for i in 1..=60 {
        ws.ok(&["status", "create", &format!("S{i:02}")]);
    }
    let values = ws.values("status get ");
    assert_eq!(values.len(), 50);
    assert_eq!(values[0], "S01");
    assert_eq!(ws.values("status get S").len(), 50);
    assert_eq!(ws.values("status get S6"), v(&["S60"]));
}

// ---- тихо и ничего не создаёт ----

#[test]
fn no_workspace_means_no_values_no_messages_and_nothing_is_created() {
    let ws = Ws::new();
    let missing = ws.root.join("missing");
    let empty = ws.root.join("empty");
    std::fs::create_dir_all(&empty).unwrap();
    let sqlite = ws.root.join("absent.db");
    let values = |line: String| -> Vec<String> { ws.suggest(&line).into_iter().filter(|x| !x.starts_with('-')).collect() };

    assert!(values(format!("--workspace \"{}\" status get ", missing.display())).is_empty());
    assert!(values(format!("--workspace \"{}\" status get ", empty.display())).is_empty());
    assert!(values(format!("--sqlite \"{}\" status get ", sqlite.display())).is_empty());
    assert!(
        values(format!(
            "--sqlite \"{}\" --workspace \"{}\" status get ",
            sqlite.display(),
            empty.display()
        ))
        .is_empty()
    );
    assert!(!missing.exists());
    assert!(!empty.join(".tasker").exists(), "Tab must not create the workspace");
    assert!(!sqlite.exists(), "Tab must not create the SQLite file");

    // Файл вместо папки области — пустой ответ, не ошибка.
    let not_database = ws.root.join("not-a-database.db");
    std::fs::write(&not_database, "this is not a SQLite file at all").unwrap();
    assert!(values(format!("--workspace \"{}\" status get ", not_database.display())).is_empty());
    // Источнику проекта нужен проект: без области пусто даже то, что видно из набранного слова (как InProject у .NET).
    assert!(values(format!("--workspace \"{}\" task-type update X --field A:", empty.display())).is_empty());
}

#[test]
fn suggesting_does_not_change_the_data_and_works_in_the_current_folder() {
    let ws = Ws::new();
    ws.seed();
    let before = ws.ok(&["task", "list", "--all", "--json"]);
    ws.values("task get TSK-");
    ws.values("board tasks ");
    ws.values("task list --field Приоритет=");
    assert_eq!(ws.ok(&["task", "list", "--all", "--json"]), before);
    // Без -w: область — текущая папка процесса.
    assert_eq!(ws.suggest("task get TSK-1"), v(&["TSK-1", "TSK-10", "TSK-11"]));
}

// ---- справочник manual/howto ----

#[test]
fn manual_topics_languages_and_workspace_free_options() {
    let ws = Ws::new();
    let topics = [
        "agent",
        "boards",
        "fields",
        "first-project",
        "install-daemon",
        "links",
        "locks",
        "migrate",
        "series",
        "statuses-types",
        "sync-cleanup",
        "tasks",
        "windows",
    ];
    for line in ["manual ", "howto "] {
        let mut found: Vec<String> = ws.suggest(line).into_iter().filter(|x| !x.starts_with('-')).collect();
        found.sort();
        assert_eq!(found, v(&topics), "{line}");
    }
    for line in ["manual fir", "howto fir", "manual FIRST"] {
        assert_eq!(ws.suggest(line), v(&["first-project"]), "{line}");
    }
    assert_eq!(ws.suggest("manual --lang r"), v(&["ru"]));

    // Ни область, ни файл SQLite не нужны и не создаются.
    let folder = ws.root.join("no-workspace");
    assert_eq!(
        ws.suggest(&format!("manual -w \"{}\" fir", folder.display())),
        v(&["first-project"])
    );
    assert_eq!(
        ws.suggest(&format!("manual --sqlite \"{}.db\" --lang r", folder.display())),
        v(&["ru"])
    );
    assert!(!folder.exists());

    for line in ["manual --", "howto --", "completion --"] {
        let options = ws.suggest(line);
        assert!(
            !options
                .iter()
                .any(|x| ["--workspace", "-w", "--sqlite", "--project", "-p"].contains(&x.as_str())),
            "{line}"
        );
        assert!(options.contains(&"--json".to_string()));
    }
    assert!(ws.suggest("manual --").contains(&"--lang".to_string()));
    assert!(ws.suggest("task list --").contains(&"--workspace".to_string()));
    assert!(ws.suggest("sync --").contains(&"--workspace".to_string()));
    assert!(ws.suggest("project get --").contains(&"--project".to_string()));
    let tmp = ws.root.to_string_lossy().into_owned();
    assert!(
        ws.command(&["manual", "-w", &tmp, "-p", "x", "tasks"])
            .output()
            .unwrap()
            .status
            .success()
    );
}

// ---- команда completion и скрипты ----

fn script(ws: &Ws, shell: &str) -> String {
    let output = ws.command(&["completion", shell]).output().unwrap();
    assert!(output.status.success() && output.stderr.is_empty());
    String::from_utf8(output.stdout).unwrap()
}

#[test]
fn the_completion_command_prints_scripts_that_call_the_directive() {
    let ws = Ws::new();
    for (shell, marker) in [("zsh", "#compdef tasker"), ("bash", "complete -o default -F _tasker tasker")] {
        let text = script(&ws, shell);
        assert!(text.contains(marker) && text.contains("[suggest:"), "{shell}");
    }
    let output = ws.command(&["completion", "fish"]).output().unwrap();
    assert_ne!(output.status.code(), Some(0));
    let err = String::from_utf8(output.stderr).unwrap();
    assert!(err.contains("zsh") && err.contains("bash"));
}

fn has_shell(shell: &str) -> bool {
    ["/bin/", "/usr/bin/", "/usr/local/bin/"]
        .iter()
        .any(|d| Path::new(&format!("{d}{shell}")).exists())
}

/// Подставной `tasker`: отвечает на директиву готовыми строками (и записывает вызовы, если нужно).
struct Fake {
    dir: PathBuf,
}

impl Fake {
    fn new(ws: &Ws, answer: &[&str], record: bool) -> Fake {
        let dir = ws.root.join(format!("fake-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut body = String::from("#!/bin/sh\n");
        if record {
            body.push_str(&format!("printf '%s\\n' \"$1|$2\" >> '{}'\n", dir.join("calls").display()));
        }
        for line in answer {
            body.push_str(&format!("printf '%s\\n' '{}'\n", line.replace('\'', "'\\''")));
        }
        let file = dir.join("tasker");
        std::fs::write(&file, body).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o755)).unwrap();
        }
        Fake { dir }
    }

    fn calls(&self) -> String {
        std::fs::read_to_string(self.dir.join("calls")).unwrap()
    }
}

fn shell(shell: &str, args: &[&str], path: &Path) -> (i32, String, String) {
    let output = Command::new(shell)
        .args(args)
        .env("PATH", format!("{}:{}", path.display(), std::env::var("PATH").unwrap_or_default()))
        .output()
        .unwrap();
    (
        output.status.code().unwrap_or(-1),
        String::from_utf8(output.stdout).unwrap(),
        String::from_utf8(output.stderr).unwrap(),
    )
}

fn bash_complete(ws: &Ws, line: &str, answer: &[&str]) -> Vec<String> {
    let fake = Fake::new(ws, answer, false);
    let driver = r#"
eval "$1"
COMP_LINE=$2
COMP_POINT=${#COMP_LINE}
COMP_WORDBREAKS=$' \t\n"\'><=;|&(:'
read -r -a COMP_WORDS <<< "$COMP_LINE"
if [[ $COMP_LINE == *' ' ]]; then COMP_WORDS+=(''); fi
COMP_CWORD=$(( ${#COMP_WORDS[@]} - 1 ))
_tasker
if (( ${#COMPREPLY[@]} )); then printf '%s\n' "${COMPREPLY[@]}"; fi
"#;
    let (code, out, err) = shell("bash", &["-c", driver, "driver", &script(ws, "bash"), line], &fake.dir);
    assert!(code == 0 && err.is_empty(), "{err}");
    out.lines().map(str::to_string).collect()
}

#[test]
fn the_bash_script_works_with_the_directive() {
    if !has_shell("bash") {
        return;
    }
    let ws = Ws::new();
    // Строка без имени программы и позиция курсора.
    let fake = Fake::new(&ws, &["project"], true);
    let driver = r#"
eval "$1"
COMP_LINE='tasker task  cr'
COMP_POINT=${#COMP_LINE}
COMP_WORDS=(tasker task cr)
COMP_CWORD=2
COMP_WORDBREAKS=$' \t\n"\'><=;|&(:'
_tasker
"#;
    let (code, _, err) = shell("bash", &["-c", driver, "driver", &script(&ws, "bash")], &fake.dir);
    assert_eq!(code, 0, "{err}");
    assert_eq!(fake.calls(), "[suggest:8]|task  cr\n");

    assert_eq!(
        bash_complete(&ws, "tasker status get В", &["В работе", "Готово", "--version", "Весна"]),
        v(&["В\\ работе", "Весна"])
    );
    assert_eq!(
        bash_complete(&ws, "tasker x a", &["a $b(1);&\\"]),
        v(&["a\\ \\$b\\(1\\)\\;\\&\\\\"])
    );
    assert_eq!(bash_complete(&ws, "tasker x \"В р", &["В работе"]), v(&["В работе"]));
    assert_eq!(bash_complete(&ws, "tasker x 'a", &["a'b"]), v(&["a'\\''b"]));
    assert_eq!(bash_complete(&ws, "tasker x ", &["~tilde"]), v(&["\\~tilde"]));
    assert_eq!(
        bash_complete(
            &ws,
            "tasker task list --field Приоритет=",
            &["Приоритет=Высокий", "Приоритет=Очень срочно"]
        ),
        v(&["Высокий", "Очень\\ срочно"])
    );
    assert!(bash_complete(&ws, "tasker status get ", &[]).is_empty());
}

fn zsh_complete(ws: &Ws, words: &[&str], answer: &[&str]) -> Vec<String> {
    let fake = Fake::new(ws, answer, false);
    let driver = r#"
compdef() { :; }
compadd() { print -r -- "compadd $*"; }
_files() { print -r -- "_files"; }
eval "$1"
shift
words=("$@")
CURRENT=$#words
_tasker
"#;
    let script = script(ws, "zsh");
    let mut args = vec!["-f", "-c", driver, "driver", script.as_str()];
    args.extend(words);
    let (code, out, err) = shell("zsh", &args, &fake.dir);
    assert!(code == 0 && err.is_empty(), "{err}");
    out.lines().map(str::to_string).collect()
}

#[test]
fn the_zsh_script_works_with_the_directive() {
    if !has_shell("zsh") {
        return;
    }
    let ws = Ws::new();
    assert_eq!(
        zsh_complete(&ws, &["tasker", "status", "get", ""], &["В работе", "Готово"]),
        v(&["compadd -- В работе Готово"])
    );
    assert_eq!(
        zsh_complete(&ws, &["tasker", "x", ""], &["project", "Приоритет=", "TSK-"]),
        v(&["compadd -- project", "compadd -S  -- Приоритет= TSK-"])
    );
    assert_eq!(zsh_complete(&ws, &["tasker", "task", "list", "-w", ""], &[]), v(&["_files"]));

    let fake = Fake::new(&ws, &["x"], true);
    let driver = r#"
compdef() { :; }
compadd() { :; }
eval "$1"
words=(tasker status get 'В\ р' ignored)
CURRENT=4
_tasker
"#;
    let (code, _, err) = shell("zsh", &["-f", "-c", driver, "driver", &script(&ws, "zsh")], &fake.dir);
    assert_eq!(code, 0, "{err}");
    let call = fake.calls();
    assert!(call.starts_with("[suggest:") && call.ends_with("]|status get В\\ р\n"), "{call}");
}

/// `canonicalize` без префикса `\\?\` на Windows: программы печатают пути в обычном виде (`D:\…`), и сравнение идёт с ними.
fn canonical(path: impl AsRef<std::path::Path>) -> std::path::PathBuf {
    let path = std::fs::canonicalize(path).unwrap();
    match path.to_str().and_then(|text| text.strip_prefix(r"\\?\")) {
        Some(plain) if cfg!(windows) => std::path::PathBuf::from(plain),
        _ => path,
    }
}
