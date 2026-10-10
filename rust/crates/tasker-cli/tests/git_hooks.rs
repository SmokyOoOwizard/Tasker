//! `tasker hooks install | status | uninstall` и хуки в деле на настоящем git (сценарии `GitHooksTests` .NET): тексты и коды как у
//! .NET, содержимое хука (блок с маркерами в начале, чужие команды сохраняются и возвращаются при удалении, `core.hooksPath`), и
//! главное — после `checkout`, `merge`, `rebase`, `worktree add` хук сам обновляет кэш (`.cache/index-rs.db`): кэш читается
//! напрямую, ни одного процесса Tasker при этом не запускается. Репозитории — во временных каталогах; настройки git пользователя
//! (`core.hooksPath`, шаблоны) не участвуют.
//!
//! Каталоги — в системном временном, а не в `target/tmp`: тот лежит внутри репозитория Tasker, и `hooks install` в папке без своего
//! репозитория поставил бы хуки в `.git/hooks` самого Tasker.
mod common;

use common::*;
use std::path::{Path, PathBuf};
use std::process::Command;

#[cfg(unix)] // на Windows тесты, которые это используют, не собираются
const HOOKS: [&str; 3] = ["post-merge", "post-checkout", "post-rewrite"];

struct Repo {
    root: PathBuf,
}

impl Repo {
    fn new() -> Repo {
        let base = std::env::temp_dir().join(format!("tasker-tests-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(base.join("repo")).unwrap();
        let repo = Repo {
            root: canonical(base.join("repo")),
        };
        repo.git(&["init", "-q", "-b", "main"]);
        assert_eq!(
            canonical(repo.git(&["rev-parse", "--show-toplevel"]).trim()),
            repo.root,
            "the test repository must be its own"
        );
        repo.git(&["config", "user.email", "tests@tasker"]);
        repo.git(&["config", "user.name", "Tasker tests"]);
        repo.git(&["config", "commit.gpgsign", "false"]);
        repo
    }

    /// Каталог `<temp>/tasker-tests-<id>`: в нём `.home` консоли и сам репозиторий `repo`.
    fn base(&self) -> &Path {
        self.root.parent().unwrap()
    }

    fn folder(&self, relative: &str) -> PathBuf {
        let path = if relative.is_empty() {
            self.root.clone()
        } else {
            self.root.join(relative)
        };
        std::fs::create_dir_all(&path).unwrap();
        path
    }

    fn hook(&self, name: &str) -> PathBuf {
        self.root.join(".git").join("hooks").join(name)
    }

    /// git с окружением консоли (хуки наследуют `TASKER_HOME`); ошибка — паника. stdout и stderr вместе.
    fn git_in(&self, directory: &Path, arguments: &[&str]) -> String {
        // Окружение консоли (`common::command`) — и git, и хукам, которые он запускает.
        let console = command(self.base(), &[]);
        let mut command = Command::new("git");
        for (key, value) in console.get_envs() {
            match value {
                Some(value) => command.env(key, value),
                None => command.env_remove(key),
            };
        }
        let output = command
            .args(arguments)
            .current_dir(directory)
            .env("GIT_CONFIG_GLOBAL", if cfg!(windows) { "NUL" } else { "/dev/null" })
            .env("GIT_CONFIG_NOSYSTEM", "1")
            .output()
            .unwrap();
        let text = format!(
            "{}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(output.status.success(), "git {} failed: {text}", arguments.join(" "));
        text
    }

    fn git(&self, arguments: &[&str]) -> String {
        self.git_in(&self.root, arguments)
    }

    #[cfg(unix)] // на Windows тесты, которые это используют, не собираются
    fn commit(&self, message: &str) {
        self.git(&["add", "-A"]);
        self.git(&["commit", "-q", "-m", message]);
    }

    fn tasker(&self, args: &[&str]) -> Run {
        tasker(self.base(), args)
    }

    /// Папка с проектом A на main и веткой b с проектом B (оба закоммичены).
    #[cfg(unix)] // на Windows тесты, которые это используют, не собираются
    fn with_two_branches(&self, relative: &str) -> PathBuf {
        let workspace = self.folder(relative);
        let w = workspace.to_str().unwrap();
        assert_eq!(self.tasker(&["project", "create", "A", "-w", w]).code, 0);
        self.commit("A");
        self.git(&["checkout", "-q", "-b", "b"]);
        assert_eq!(self.tasker(&["project", "create", "B", "-w", w]).code, 0);
        self.commit("B");
        workspace
    }
}

impl Drop for Repo {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(self.base());
    }
}

/// Сколько проектов в кэше папки; -1 — кэша нет. Только чтение: ни один процесс Tasker не запускается.
#[cfg(unix)] // на Windows тесты, которые это используют, не собираются
fn projects_in_cache(workspace: &Path) -> i64 {
    let file = workspace.join(".tasker/.cache/index-rs.db");
    if !file.exists() {
        return -1;
    }
    let connection = rusqlite::Connection::open_with_flags(&file, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap();
    connection
        .query_row("SELECT count(*) FROM files WHERE kind = 'Project' AND error IS NULL", [], |row| {
            row.get(0)
        })
        .unwrap()
}

/// Путь в форме для sh, как в хуке: на Windows с прямыми слэшами (`GitHooks.ToShellPath` в .NET), в одинарных кавычках.
fn shell_quote(text: &str) -> String {
    let text = if cfg!(windows) { text.replace('\\', "/") } else { text.to_string() };
    format!("'{}'", text.replace('\'', "'\\''"))
}

#[test]
fn install_status_and_uninstall_print_what_dotnet_prints_and_write_the_same_block() {
    let repo = Repo::new();
    let workspace = repo.folder("tools/my ws");
    let w = workspace.to_str().unwrap();
    assert_eq!(repo.tasker(&["project", "create", "A", "-w", w]).code, 0);
    let hooks = repo.root.join(".git").join("hooks");

    let status = repo.tasker(&["hooks", "status", "-w", w]);
    assert_eq!(
        (status.stdout.as_str(), status.code),
        (
            "post-merge     not installed\npost-checkout  not installed\npost-rewrite   not installed\n",
            0
        )
    );
    let removed = repo.tasker(&["hooks", "uninstall", "-w", w]);
    assert_eq!((removed.stdout.as_str(), removed.code), ("No Tasker hooks found\n", 0));

    // Чужой хук: свой интерпретатор и exit — блок Tasker встаёт перед командами пользователя.
    std::fs::create_dir_all(&hooks).unwrap();
    let mine = "#!/bin/bash\necho mine\nexit 3\n";
    std::fs::write(repo.hook("post-merge"), mine).unwrap();

    let installed = repo.tasker(&["hooks", "install", "-w", w]);
    assert_eq!(installed.stderr, "");
    assert_eq!(installed.code, 0);
    assert_eq!(
        installed.stdout,
        format!("Installed in {}:\n  post-merge\n  post-checkout\n  post-rewrite\n", hooks.display())
    );
    let program = shell_quote(env!("CARGO_BIN_EXE_tasker"));
    let block = format!(
        "# >>> tasker (managed by 'tasker hooks install', do not edit) >>>\n\
         if [ -e {program} ]; then\n  \
         {program} sync --quiet --workspace \"$(git rev-parse --show-toplevel)\"/'tools/my ws' || true\n\
         fi\n\
         # <<< tasker <<<\n"
    );
    assert_eq!(
        std::fs::read_to_string(repo.hook("post-merge")).unwrap(),
        format!("#!/bin/bash\n{block}echo mine\nexit 3\n")
    );
    for name in ["post-checkout", "post-rewrite"] {
        assert_eq!(std::fs::read_to_string(repo.hook(name)).unwrap(), format!("#!/bin/sh\n{block}"));
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mode = std::fs::metadata(repo.hook(name)).unwrap().permissions().mode();
            assert_ne!(mode & 0o100, 0, "{name} must be executable");
        }
    }

    // Повторная установка ничего не дублирует.
    assert_eq!(repo.tasker(&["hooks", "install", "-w", w]).code, 0);
    assert_eq!(
        std::fs::read_to_string(repo.hook("post-merge"))
            .unwrap()
            .matches("# >>> tasker")
            .count(),
        1
    );

    let status = repo.tasker(&["hooks", "status", "-w", w]);
    assert_eq!(
        status.stdout,
        "post-merge     installed (has other commands)\npost-checkout  installed\npost-rewrite   installed\n"
    );
    let json: serde_json::Value = serde_json::from_str(&repo.tasker(&["hooks", "status", "--json", "-w", w]).stdout).unwrap();
    assert_eq!(json[0]["name"], "post-merge");
    assert_eq!(json[0]["installed"], true);
    assert_eq!(json[0]["foreign"], true);
    assert_eq!(json[1]["foreign"], false);
    assert_eq!(
        json[0].as_object().unwrap().keys().collect::<Vec<_>>(),
        ["name", "path", "installed", "foreign"]
    );

    // Удаление: чужой хук — как был, наши целиком — удалены.
    let removed = repo.tasker(&["hooks", "uninstall", "-w", w]);
    assert_eq!(
        (removed.stdout.as_str(), removed.code),
        ("Removed from: post-merge, post-checkout, post-rewrite\n", 0)
    );
    assert_eq!(std::fs::read_to_string(repo.hook("post-merge")).unwrap(), mine);
    assert!(!repo.hook("post-checkout").exists());
    assert!(!repo.hook("post-rewrite").exists());
}

#[test]
fn a_hook_without_a_shebang_gets_one_and_core_hooks_path_is_respected() {
    let repo = Repo::new();
    let workspace = repo.folder("");
    let w = workspace.to_str().unwrap();
    assert_eq!(repo.tasker(&["project", "create", "A", "-w", w]).code, 0);
    std::fs::create_dir_all(repo.root.join(".git").join("hooks")).unwrap();
    std::fs::write(repo.hook("post-checkout"), "echo mine\n").unwrap();
    assert_eq!(repo.tasker(&["hooks", "install", "-w", w]).code, 0);
    let text = std::fs::read_to_string(repo.hook("post-checkout")).unwrap();
    assert!(text.starts_with("#!/bin/sh\n# >>> tasker"), "{text}");
    assert!(text.ends_with("echo mine\n"), "{text}");
    assert!(
        text.contains(" sync --quiet --workspace \"$(git rev-parse --show-toplevel)\" || true\n"),
        "{text}"
    );

    let other = Repo::new();
    let sub = other.folder("sub");
    assert_eq!(other.tasker(&["project", "create", "A", "-w", sub.to_str().unwrap()]).code, 0);
    other.git(&["config", "core.hooksPath", ".githooks"]);
    assert_eq!(other.tasker(&["hooks", "install", "-w", sub.to_str().unwrap()]).code, 0);
    assert!(other.root.join(".githooks/post-merge").is_file());
    assert!(!other.hook("post-merge").exists());
}

#[test]
fn install_outside_a_repository_or_a_workspace_is_a_clear_error() {
    let repo = Repo::new();
    let plain = repo.folder("");
    let run = repo.tasker(&["hooks", "install", "-w", plain.to_str().unwrap()]);
    assert_eq!(run.code, 1);
    assert_eq!(
        run.stderr,
        format!("Error: {} is not a Tasker workspace: there is no .tasker folder\n", plain.display())
    );

    // Папка вне репозитория git: проверяем, что она и правда вне, — иначе хуки встали бы в чужой репозиторий.
    let outside = repo.base().join("outside");
    std::fs::create_dir_all(outside.join(".tasker")).unwrap();
    let inside_git = Command::new("git")
        .args(["rev-parse", "--show-toplevel"])
        .current_dir(&outside)
        .output()
        .unwrap()
        .status
        .success();
    if inside_git {
        eprintln!("skipped: {} is inside a git repository", outside.display());
        return;
    }
    let run = repo.tasker(&["hooks", "install", "-w", outside.to_str().unwrap()]);
    assert_eq!(run.code, 1);
    assert_eq!(run.stderr, format!("Error: {} is not in a git repository\n", outside.display()));
}

#[cfg(unix)]
#[test]
fn checkout_merge_rebase_and_a_new_worktree_update_the_cache_through_the_hooks() {
    // Без хуков кэш отстаёт, пока кто-нибудь не откроет папку.
    let repo = Repo::new();
    let workspace = repo.with_two_branches("");
    assert_eq!(projects_in_cache(&workspace), 2);
    repo.git(&["checkout", "-q", "main"]);
    assert_eq!(projects_in_cache(&workspace), 2);
    let list = repo.tasker(&["project", "list", "-q", "-w", workspace.to_str().unwrap()]);
    assert_eq!(list.code, 0);
    assert_eq!(projects_in_cache(&workspace), 1);

    // checkout: хук обновляет кэш сразу.
    assert_eq!(repo.tasker(&["hooks", "install", "-w", workspace.to_str().unwrap()]).code, 0);
    repo.git(&["checkout", "-q", "b"]);
    assert_eq!(projects_in_cache(&workspace), 2);
    repo.git(&["checkout", "-q", "main"]);
    assert_eq!(projects_in_cache(&workspace), 1);

    // merge.
    repo.git(&["merge", "-q", "b"]);
    assert_eq!(projects_in_cache(&workspace), 2);

    // rebase: на main ещё проект — ветка c перебазируется на него.
    repo.git(&["checkout", "-q", "-b", "c", "HEAD~1"]);
    assert_eq!(projects_in_cache(&workspace), 1);
    repo.git(&["checkout", "-q", "main"]);
    assert_eq!(repo.tasker(&["project", "create", "C", "-w", workspace.to_str().unwrap()]).code, 0);
    repo.commit("C");
    repo.git(&["checkout", "-q", "c"]);
    assert_eq!(projects_in_cache(&workspace), 1);
    repo.git(&["rebase", "-q", "main"]);
    assert_eq!(projects_in_cache(&workspace), 3);

    // Новое дерево: хук лежит в общем .git, а рабочая папка определяется в момент запуска.
    let other = repo.base().join("worktree");
    repo.git(&["worktree", "add", "-q", other.to_str().unwrap(), "b"]);
    assert_eq!(projects_in_cache(&other), 2);
}

#[cfg(unix)]
#[test]
fn a_workspace_in_a_subfolder_with_a_space_works_and_a_missing_tasker_never_breaks_git() {
    let repo = Repo::new();
    let workspace = repo.with_two_branches("tools/my ws");
    assert_eq!(repo.tasker(&["hooks", "install", "-w", workspace.to_str().unwrap()]).code, 0);
    repo.git(&["checkout", "-q", "main"]);
    assert_eq!(projects_in_cache(&workspace), 1);

    // Программы, поставившей хук, больше нет: git работает как обычно.
    let program = shell_quote(env!("CARGO_BIN_EXE_tasker"));
    let gone = shell_quote(&format!("{}-gone", env!("CARGO_BIN_EXE_tasker")));
    for name in HOOKS {
        let text = std::fs::read_to_string(repo.hook(name)).unwrap();
        assert!(text.contains(&program));
        std::fs::write(repo.hook(name), text.replace(&program, &gone)).unwrap();
    }
    let output = repo.git(&["checkout", "b"]);
    assert!(!output.contains("not found"), "{output}");
    assert_eq!(repo.git(&["rev-parse", "--abbrev-ref", "HEAD"]).trim(), "b");
    assert_eq!(projects_in_cache(&workspace), 1, "nothing updated the cache");
}
