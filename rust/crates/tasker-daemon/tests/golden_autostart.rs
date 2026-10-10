//! Эталоны `rust/tests/golden/autostart` (сняты с .NET-генераторов, см. README там): Rust-генераторы дают байт-идентичные файлы
//! для тех же входов, службы вызывают те же команды.
use std::cell::RefCell;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::Mutex;
use tasker_daemon::autostart::{
    AutostartContext, LaunchdService, ProcessResult, ProcessRunner, ServiceManager, SystemdService, WindowsTaskService, render_launchd,
    render_systemd, render_windows_task,
};

// Без пробелов и спецсимволов: в оригинале их не было, а квотирование systemd/Windows от них зависит.
const ROOT: &str = "/tmp/golden-root";
const BIN: &str = "/opt/tasker-bin";
const DOTNET: &str = "/usr/local/share/dotnet/dotnet";
const HOME: &str = "/Users/golden";

fn golden_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden/autostart")
}

/// Файл эталона с подставленными плейсхолдерами (`<ROOT>`, `<BIN>`, `<DOTNET>`, `<HOME>`).
fn golden_text(case: &str, file: &str) -> String {
    substitute(&std::fs::read_to_string(golden_dir().join(case).join(file)).unwrap())
}

fn substitute(text: &str) -> String {
    text.replace("<ROOT>", ROOT)
        .replace("<BIN>", BIN)
        .replace("<DOTNET>", DOTNET)
        .replace("<HOME>", HOME)
}

/// XML Планировщика: UTF-16 LE с BOM — подстановка в тексте, байты собираются обратно.
fn golden_utf16(case: &str, file: &str) -> Vec<u8> {
    let bytes = std::fs::read(golden_dir().join(case).join(file)).unwrap();
    assert_eq!(&bytes[..2], &[0xFF, 0xFE], "{case}/{file}: нет метки порядка байтов UTF-16 LE");
    let units = bytes[2..].chunks(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect::<Vec<_>>();
    let text = substitute(&String::from_utf16(&units).unwrap());
    let mut out = vec![0xFF, 0xFE];
    out.extend(text.encode_utf16().flat_map(u16::to_le_bytes));
    out
}

fn dotnet_command() -> Vec<String> {
    vec![DOTNET.to_string(), format!("{BIN}/tasker-mcpd.dll"), "--detached".to_string()]
}

fn ctx(label: &str, user_id: &str, home: Option<&str>, data_dir: &str) -> AutostartContext {
    AutostartContext {
        command: dotnet_command(),
        environment: home.map(|h| vec![("TASKER_HOME".to_string(), h.to_string())]).unwrap_or_default(),
        data_dir: PathBuf::from(data_dir),
        label: label.to_string(),
        user_id: user_id.to_string(),
    }
}

fn mac_default_data() -> String {
    format!("{HOME}/Library/Application Support/Tasker")
}

const LAUNCHD_LABEL: &str = "com.tasker.golden-autostart";
const SYSTEMD_UNIT: &str = "tasker-golden.service";
const WINDOWS_TASK: &str = "Tasker MCP Golden";

#[test]
#[cfg_attr(windows, ignore = "launchd is macOS only: the plist is checked with Unix paths")]
fn launchd_plist_matches_dotnet_byte_for_byte() {
    let home = format!("{ROOT}/home/data");
    let special = format!("{ROOT}/tasker home/a&b <c>");
    let cli_home = format!("{ROOT}/home");
    let cases = [
        ("launchd", ctx(LAUNCHD_LABEL, "", Some(&home), &home)),
        ("launchd-nohome", ctx(LAUNCHD_LABEL, "", None, &mac_default_data())),
        ("launchd-special", ctx(LAUNCHD_LABEL, "", Some(&special), &special)),
        ("launchd-cli", ctx(LAUNCHD_LABEL, "", Some(&cli_home), &cli_home)),
    ];
    for (case, ctx) in cases {
        assert_eq!(
            render_launchd(&ctx),
            golden_text(case, "com.tasker.golden-autostart.plist"),
            "{case}"
        );
    }
}

#[test]
fn systemd_unit_matches_dotnet_byte_for_byte() {
    let home = format!("{ROOT}/home/data");
    let space = format!("{ROOT}/tasker home");
    let cases = [
        ("systemd", ctx(SYSTEMD_UNIT, "", Some(&home), &home)),
        ("systemd-nohome", ctx(SYSTEMD_UNIT, "", None, &mac_default_data())),
        ("systemd-space", ctx(SYSTEMD_UNIT, "", Some(&space), &space)),
    ];
    for (case, ctx) in cases {
        assert_eq!(render_systemd(&ctx), golden_text(case, "tasker-golden.service"), "{case}");
    }
}

#[test]
fn windows_task_xml_matches_dotnet_byte_for_byte() {
    let home = format!("{ROOT}/home/data");
    let cases = [
        ("windows", ctx(WINDOWS_TASK, r"PC\Артём", Some(&home), &home)),
        ("windows-nohome", ctx(WINDOWS_TASK, r"PC\Артём", None, &mac_default_data())),
    ];
    for (case, ctx) in cases {
        let actual = render_windows_task(&ctx).unwrap();
        let expected = golden_utf16(case, "Tasker MCP Golden.xml");
        assert_eq!(&actual[..2], &[0xFF, 0xFE]);
        assert!(actual == expected, "{case}: байты XML отличаются");
    }
}

// ---- команды служб: как в calls.txt (поддельный IProcessRunner .NET) и launchctl-calls.txt (настоящий `tasker mcp autostart enable`) ----

#[derive(Default)]
struct Recorder(RefCell<Vec<String>>);

impl ProcessRunner for Recorder {
    fn run(&self, file: &str, arguments: &[&str]) -> ProcessResult {
        let mut line = vec![file.to_string()];
        line.extend(arguments.iter().map(|a| a.to_string()));
        self.0.borrow_mut().push(line.join("\t"));
        ProcessResult::ok()
    }
}

static ENV: Mutex<()> = Mutex::new(());

/// Команды службы при `enable` в каталоге `<ROOT>/<case>` с `TASKER_HOME` эталона; строки — `file\targ…`, как в calls.txt.
fn calls_of(case: &str, make: impl FnOnce(Box<dyn ProcessRunner>, PathBuf) -> Box<dyn ServiceManager>) -> (Vec<String>, PathBuf) {
    let _lock = ENV.lock().unwrap_or_else(|e| e.into_inner());
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(format!("golden-autostart-{}", std::process::id()));
    let root = tasker_daemon_full_path(&root);
    let dir = root.join("out").join(case);
    let _ = std::fs::remove_dir_all(&dir);
    // SAFETY: тесты этого файла меняют окружение только под ENV.
    unsafe { std::env::set_var("TASKER_HOME", root.join("home").join("data")) };
    let recorder = Rc::new(Recorder::default());
    let service = make(Box::new(Rc::clone(&recorder)), dir.clone());
    service.enable().unwrap();
    assert!(service.is_enabled());
    unsafe { std::env::remove_var("TASKER_HOME") };
    let calls = recorder.0.borrow().clone();
    (calls, dir)
}

fn tasker_daemon_full_path(path: &Path) -> PathBuf {
    tasker_core::io::full_path(path)
}

/// `calls.txt` эталона с `<ROOT>/out/<case>` → настоящий каталог теста и `gui/<UID>` → uid теста.
fn expected_calls(case: &str, file: &str, dir: &Path, uid: u32) -> Vec<String> {
    let root = dir.parent().unwrap().parent().unwrap();
    std::fs::read_to_string(golden_dir().join(case).join(file))
        .unwrap()
        .lines()
        .map(|l| with_root(&l.replace("gui/<UID>", &format!("gui/{uid}")), &root.to_string_lossy()))
        .collect()
}

/// `<ROOT>` в строке вызова; эталоны сняты на Unix — на Windows разделители пути после `<ROOT>` (до табуляции) — `\\`.
fn with_root(line: &str, root: &str) -> String {
    let mut out = String::new();
    let mut rest = line;
    while let Some(at) = rest.find("<ROOT>") {
        out.push_str(&rest[..at]);
        out.push_str(root);
        rest = &rest[at + "<ROOT>".len()..];
        let end = rest.find('\t').unwrap_or(rest.len());
        let tail = &rest[..end];
        out.push_str(&if cfg!(windows) { tail.replace('/', "\\") } else { tail.to_string() });
        rest = &rest[end..];
    }
    out.push_str(rest);
    out
}

#[test]
#[cfg_attr(windows, ignore = "launchd is macOS only: the plist is checked with Unix paths")]
fn launchd_commands_match_dotnet() {
    for case in ["launchd", "launchd-nohome", "launchd-special"] {
        let (calls, dir) = calls_of(case, |runner, dir| {
            Box::new(
                LaunchdService::new(runner, Some(dir), Some(LAUNCHD_LABEL.to_string()))
                    .with_uid(502)
                    .with_daemon_command(dotnet_command()),
            )
        });
        assert_eq!(calls, expected_calls(case, "calls.txt", &dir, 502), "{case}");
    }
}

#[test]
#[cfg_attr(windows, ignore = "launchd is macOS only: the plist is checked with Unix paths")]
fn launchd_cli_enable_and_status_match_the_real_dotnet_console() {
    // Эталон снят настоящим `tasker mcp autostart enable` + `tasker mcp autostart status` с TASKER_SERVICE_DIR=<ROOT>/service.
    let _lock = ENV.lock().unwrap_or_else(|e| e.into_inner());
    let root = tasker_daemon_full_path(
        &PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(format!("golden-cli-{}", std::process::id())),
    );
    let _ = std::fs::remove_dir_all(&root);
    unsafe { std::env::set_var("TASKER_HOME", root.join("home")) };
    let recorder = Rc::new(Recorder::default());
    let service = LaunchdService::new(
        Box::new(Rc::clone(&recorder)),
        Some(root.join("service")),
        Some(LAUNCHD_LABEL.to_string()),
    )
    .with_uid(502)
    .with_daemon_command(dotnet_command());
    service.enable().unwrap();
    assert!(service.is_loaded());
    unsafe { std::env::remove_var("TASKER_HOME") };

    let expected = std::fs::read_to_string(golden_dir().join("launchd-cli/launchctl-calls.txt"))
        .unwrap()
        .lines()
        .map(|l| l.replace("<ROOT>", &root.to_string_lossy()).replace("gui/<UID>", "gui/502"))
        .collect::<Vec<_>>();
    let actual = recorder.0.borrow().iter().map(|l| l.replace('\t', " ")).collect::<Vec<_>>();
    assert_eq!(actual, expected);

    let plist = std::fs::read_to_string(root.join("service").join("com.tasker.golden-autostart.plist")).unwrap();
    let expected_plist = std::fs::read_to_string(golden_dir().join("launchd-cli/com.tasker.golden-autostart.plist"))
        .unwrap()
        .replace("<ROOT>", &root.to_string_lossy())
        .replace("<BIN>", BIN)
        .replace("<DOTNET>", DOTNET);
    assert_eq!(plist, expected_plist);
}

#[test]
fn systemd_commands_match_dotnet() {
    for case in ["systemd", "systemd-nohome", "systemd-space"] {
        let (calls, dir) = calls_of(case, |runner, dir| {
            Box::new(SystemdService::new(runner, Some(dir), Some(SYSTEMD_UNIT.to_string())).with_daemon_command(dotnet_command()))
        });
        assert_eq!(calls, expected_calls(case, "calls.txt", &dir, 0), "{case}");
    }
}

#[test]
fn windows_commands_match_dotnet() {
    for case in ["windows", "windows-nohome"] {
        let (calls, dir) = calls_of(case, |runner, dir| {
            Box::new(
                WindowsTaskService::new(runner, Some(dir), Some(WINDOWS_TASK.to_string()), Some(r"PC\Артём".to_string()))
                    .with_daemon_command(dotnet_command()),
            )
        });
        assert_eq!(calls, expected_calls(case, "calls.txt", &dir, 0), "{case}");
    }
}
