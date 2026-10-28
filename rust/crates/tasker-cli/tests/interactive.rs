mod common;

use common::*;
use portable_pty::{Child, CommandBuilder, MasterPty, PtySize, native_pty_system};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const ESC: &str = "\x1b";
const ENTER: &str = "\r";
const DOWN: &str = "\x1b[B";

struct Screen {
    cells: Vec<Vec<char>>,
    row: usize,
    col: usize,
    pending: Vec<u8>,
}

impl Screen {
    fn new(rows: usize, cols: usize) -> Screen {
        Screen {
            cells: vec![vec![' '; cols]; rows],
            row: 0,
            col: 0,
            pending: Vec::new(),
        }
    }

    fn text(&self) -> String {
        self.cells
            .iter()
            .map(|r| r.iter().collect::<String>().trim_end().to_string())
            .collect::<Vec<_>>()
            .join("\n")
    }

    fn feed(&mut self, bytes: &[u8]) {
        self.pending.extend_from_slice(bytes);
        let text = match std::str::from_utf8(&self.pending) {
            Ok(text) => text.to_string(),
            Err(error) => {
                let valid = error.valid_up_to();
                if error.error_len().is_some() {
                    String::from_utf8_lossy(&self.pending).into_owned()
                } else {
                    std::str::from_utf8(&self.pending[..valid]).unwrap().to_string()
                }
            }
        };
        let consumed = text.len();
        let chars: Vec<char> = text.chars().collect();
        let mut i = 0;
        while i < chars.len() {
            let c = chars[i];
            if c == '\x1b' {
                match chars.get(i + 1) {
                    Some('[') => {
                        let mut j = i + 2;
                        let mut params = String::new();
                        while j < chars.len() && !(('@'..='~').contains(&chars[j])) {
                            params.push(chars[j]);
                            j += 1;
                        }
                        if j >= chars.len() {
                            break;
                        }
                        self.csi(&params, chars[j]);
                        i = j + 1;
                    }
                    Some(']') => {
                        let mut j = i + 2;
                        while j < chars.len() && chars[j] != '\x07' && !(chars[j] == '\x1b' && chars.get(j + 1) == Some(&'\\')) {
                            j += 1;
                        }
                        if j >= chars.len() {
                            break;
                        }
                        i = if chars[j] == '\x07' { j + 1 } else { j + 2 };
                    }
                    Some('(') | Some(')') => i = (i + 3).min(chars.len()),
                    Some(_) => i += 2,
                    None => break,
                }
                continue;
            }
            match c {
                '\r' => self.col = 0,
                '\n' => self.down(),
                '\x08' => self.col = self.col.saturating_sub(1),
                '\t' => self.col = (self.col / 8 + 1) * 8,
                '\x07' => {}
                c if c.is_control() => {}
                c => {
                    if self.col >= self.cells[0].len() {
                        self.col = 0;
                        self.down();
                    }
                    let row = self.row.min(self.cells.len() - 1);
                    self.cells[row][self.col] = c;
                    self.col += 1;
                }
            }
            i += 1;
        }
        let rest: String = chars[i..].iter().collect();
        self.pending = rest.into_bytes();
        let _ = consumed;
    }

    fn down(&mut self) {
        if self.row + 1 >= self.cells.len() {
            self.cells.remove(0);
            let cols = self.cells[0].len();
            self.cells.push(vec![' '; cols]);
        } else {
            self.row += 1;
        }
    }

    fn csi(&mut self, params: &str, final_byte: char) {
        let numbers: Vec<usize> = params.trim_start_matches('?').split(';').filter_map(|p| p.parse().ok()).collect();
        let first = numbers.first().copied().unwrap_or(1).max(1);
        let rows = self.cells.len();
        let cols = self.cells[0].len();
        match final_byte {
            'H' | 'f' => {
                self.row = first.min(rows) - 1;
                self.col = numbers.get(1).copied().unwrap_or(1).max(1).min(cols) - 1;
            }
            'A' => self.row = self.row.saturating_sub(first),
            'B' => self.row = (self.row + first).min(rows - 1),
            'C' => self.col = (self.col + first).min(cols - 1),
            'D' => self.col = self.col.saturating_sub(first),
            'G' => self.col = first.min(cols) - 1,
            'd' => self.row = first.min(rows) - 1,
            'J' => {
                let mode = numbers.first().copied().unwrap_or(0);
                if mode == 2 || mode == 3 {
                    for row in &mut self.cells {
                        row.iter_mut().for_each(|c| *c = ' ');
                    }
                } else if mode == 0 {
                    let (row, col) = (self.row, self.col);
                    for (r, line) in self.cells.iter_mut().enumerate() {
                        for (c, cell) in line.iter_mut().enumerate() {
                            if r > row || (r == row && c >= col) {
                                *cell = ' ';
                            }
                        }
                    }
                }
            }
            'K' => {
                let mode = numbers.first().copied().unwrap_or(0);
                let (row, col) = (self.row, self.col);
                for (c, cell) in self.cells[row].iter_mut().enumerate() {
                    if (mode == 0 && c >= col) || (mode == 1 && c <= col) || mode == 2 {
                        *cell = ' ';
                    }
                }
            }
            'X' => {
                let (row, col) = (self.row, self.col);
                for c in col..(col + first).min(cols) {
                    self.cells[row][c] = ' ';
                }
            }
            'h' | 'l' if params.starts_with('?') && params.contains("1049") => {
                for row in &mut self.cells {
                    row.iter_mut().for_each(|c| *c = ' ');
                }
                self.row = 0;
                self.col = 0;
            }
            _ => {}
        }
    }
}

struct Tui {
    master: Box<dyn MasterPty + Send>,
    writer: Arc<Mutex<Box<dyn Write + Send>>>,
    child: Box<dyn Child + Send + Sync>,
    screen: Arc<Mutex<Screen>>,
    log: Arc<Mutex<String>>,
}

impl Tui {
    fn start(root: &Path, workspace: &Path, extra: &[&str], env: &[(&str, &str)]) -> Tui {
        let pty = native_pty_system();
        let pair = pty
            .openpty(PtySize {
                rows: 30,
                cols: 100,
                pixel_width: 0,
                pixel_height: 0,
            })
            .unwrap();
        let mut command = CommandBuilder::new(env!("CARGO_BIN_EXE_tasker"));
        command.arg("-i");
        command.arg("-w");
        command.arg(workspace);
        for arg in extra {
            command.arg(arg);
        }
        command.env("TASKER_HOME", root.join(".home"));
        command.env("TASKER_SERVICE_DIR", root.join(".home").join("service"));
        command.env("TASKER_SERVICE_LABEL", format!("com.tasker.cli-test-{}", std::process::id()));
        command.env_remove("TASKER_PROJECT");
        command.env_remove("TASKER_WIDTH");
        command.env("TERM", "xterm-256color");
        for (name, value) in env {
            command.env(name, value);
        }
        command.cwd(root);
        let child = pair.slave.spawn_command(command).unwrap();
        drop(pair.slave);
        let mut reader = pair.master.try_clone_reader().unwrap();
        let writer = Arc::new(Mutex::new(pair.master.take_writer().unwrap()));
        let screen = Arc::new(Mutex::new(Screen::new(30, 100)));
        let log = Arc::new(Mutex::new(String::new()));
        let (screen_in_thread, log_in_thread, writer_in_thread) = (screen.clone(), log.clone(), writer.clone());
        std::thread::spawn(move || {
            let mut buffer = [0u8; 4096];
            loop {
                match reader.read(&mut buffer) {
                    Ok(0) | Err(_) => break,
                    Ok(n) => {
                        let chunk = String::from_utf8_lossy(&buffer[..n]).into_owned();
                        log_in_thread.lock().unwrap().push_str(&chunk);
                        let (row, col) = {
                            let mut screen = screen_in_thread.lock().unwrap();
                            screen.feed(&buffer[..n]);
                            (screen.row, screen.col)
                        };
                        if chunk.contains("\x1b[6n") {
                            let mut writer = writer_in_thread.lock().unwrap();
                            let _ = writer.write_all(format!("\x1b[{};{}R", row + 1, col + 1).as_bytes());
                            let _ = writer.flush();
                        }
                    }
                }
            }
        });
        Tui {
            master: pair.master,
            writer,
            child,
            screen,
            log,
        }
    }

    fn screen(&self) -> String {
        self.screen.lock().unwrap().text()
    }

    fn wait_for(&self, text: &str) -> String {
        let started = Instant::now();
        loop {
            let screen = self.screen();
            if screen.contains(text) {
                return screen;
            }
            if started.elapsed() >= Duration::from_secs(20) {
                let raw = self.log.lock().unwrap().clone();
                panic!("waited for {text:?}, the screen is:\n{screen}\n--- raw output:\n{raw}");
            }
            std::thread::sleep(Duration::from_millis(30));
        }
    }

    fn send(&mut self, text: &str) {
        let mut writer = self.writer.lock().unwrap();
        writer.write_all(text.as_bytes()).unwrap();
        writer.flush().unwrap();
        drop(writer);
        std::thread::sleep(Duration::from_millis(40));
    }

    fn pick(&mut self, title: &str, filter: &str, label: &str) {
        self.wait_for(title);
        self.send("\x1b[H");
        self.send(filter);
        let started = Instant::now();
        loop {
            let screen = self.screen();
            if screen.lines().any(|l| l.starts_with("│› ") && l.contains(label)) {
                break;
            }
            assert!(
                started.elapsed() < Duration::from_secs(20),
                "waited for the cursor on {label:?}, the screen is:\n{screen}"
            );
            std::thread::sleep(Duration::from_millis(30));
        }
        self.send(ENTER);
    }

    fn mark(&mut self, filter: &str, label: &str, number: usize) {
        self.send(filter);
        self.wait_for(&format!("› [ ] {label}"));
        self.send(" ");
        self.wait_for(&format!("[{number}] {label}"));
        for _ in filter.chars() {
            self.send("\x7f");
        }
    }

    fn type_text(&mut self, title: &str, text: &str) {
        self.wait_for(title);
        self.send("\x1b[F");
        self.send(&"\x7f".repeat(60));
        self.send(text);
        self.wait_for(text);
        self.send(ENTER);
    }

    fn done(&mut self, text: &str) -> String {
        let screen = self.wait_for(" Done ");
        assert!(screen.contains(text), "expected {text:?} on the result screen:\n{screen}");
        self.send(ENTER);
        self.wait_for("What to do");
        screen
    }

    fn quit(mut self) -> u32 {
        self.wait_for("What to do");
        self.send("q");
        let started = Instant::now();
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                let _ = &self.master;
                return status.exit_code();
            }
            assert!(
                started.elapsed() < Duration::from_secs(20),
                "the program did not exit:\n{}",
                self.screen()
            );
            std::thread::sleep(Duration::from_millis(30));
        }
    }
}

fn start(root: &Path, workspace: &Path) -> Tui {
    Tui::start(root, workspace, &["-p", "Golden"], &[])
}

fn ws(workspace: &Path) -> String {
    workspace.to_string_lossy().into_owned()
}

fn fake_editor(root: &Path, text: &str) -> PathBuf {
    let content = root.join("editor-content.txt");
    std::fs::write(&content, text).unwrap();
    if cfg!(windows) {
        let script = root.join("editor.cmd");
        std::fs::write(&script, format!("@copy /y \"{}\" %1 >nul\r\n", content.display())).unwrap();
        script
    } else {
        let script = root.join("editor.sh");
        std::fs::write(&script, format!("#!/bin/sh\ncp \"{}\" \"$1\"\n", content.display())).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&script, std::fs::Permissions::from_mode(0o755)).unwrap();
        }
        script
    }
}

#[test]
fn outside_a_terminal_interactive_mode_is_refused_with_code_1() {
    let (root, workspace) = fresh_copy();
    let run = tasker(&root, &["-i", "-w", &ws(&workspace)]);
    assert_eq!(run.code, 1);
    assert_eq!(run.stdout, "");
    assert_eq!(
        run.stderr,
        "Error: Interactive mode needs a terminal: run 'tasker -i' in a terminal, not with redirected input or output\n"
    );
    let run = tasker(&root, &["-i", "task", "list", "-w", &ws(&workspace)]);
    assert_eq!(run.code, 1);
    assert_eq!(run.stderr, "--interactive option cannot be combined with a command.\n\n");
    let run = tasker(&root, &["--help"]);
    assert!(run.stdout.contains("  -i, --interactive            Interactive mode:"));
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn the_menu_opens_and_q_or_ctrl_c_quits_with_code_0() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    let screen = tui.wait_for("What to do");
    assert!(screen.contains("project: Golden"), "{screen}");
    assert!(screen.contains("› Browse"), "{screen}");
    assert!(screen.contains("Switch project"), "{screen}");
    tui.send(DOWN);
    tui.wait_for("› Create");
    tui.send(ESC);
    std::thread::sleep(Duration::from_millis(100));
    assert!(tui.screen().contains("› Create"), "Esc at the top menu does nothing");
    assert_eq!(tui.quit(), 0);

    let mut tui = start(&root, &workspace);
    tui.wait_for("What to do");
    tui.send("\x03");
    let started = Instant::now();
    loop {
        if let Some(status) = tui.child.try_wait().unwrap() {
            assert_eq!(status.exit_code(), 0);
            break;
        }
        assert!(started.elapsed() < Duration::from_secs(20));
        std::thread::sleep(Duration::from_millis(30));
    }
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn with_several_projects_the_session_starts_by_choosing_one_and_the_menu_can_switch_it() {
    let (root, workspace) = fresh_copy();
    let mut tui = Tui::start(&root, &workspace, &[], &[]);
    tui.wait_for("Choose a project");
    tui.send("leg");
    tui.wait_for("› Legacy");
    tui.send(ENTER);
    let screen = tui.wait_for("What to do");
    assert!(screen.contains("project: Legacy"), "{screen}");
    tui.pick("What to do", &DOWN.repeat(4), "Switch project");
    tui.pick("Choose a project", "gol", "Golden");
    let screen = tui.wait_for("project: Golden");
    assert!(screen.contains("now: Golden"), "{screen}");
    assert_eq!(tui.quit(), 0);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn esc_goes_one_step_back_keeping_the_typed_value_and_cancels_without_changes() {
    let (root, workspace) = fresh_copy();
    let before = files(&workspace.join(".tasker"));
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "status", "Status");
    tui.type_text("Status name", "Draft");
    tui.wait_for("Color (#RRGGBB)");
    tui.send(ESC);
    let screen = tui.wait_for("Status name");
    assert!(screen.contains("> Draft"), "the typed name is kept:\n{screen}");
    tui.send(ESC);
    tui.wait_for("Create what");
    tui.send(ESC);
    tui.wait_for("What to do");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "status", "Status");
    tui.pick("Status", "back", "Backlog");
    let screen = tui.wait_for("Delete status 'Backlog'?");
    assert!(screen.contains("This cannot be undone"), "{screen}");
    tui.send("n");
    tui.wait_for("› Backlog");
    tui.send(ESC);
    tui.send(ESC);
    assert_eq!(tui.quit(), 0);
    assert_eq!(files(&workspace.join(".tasker")), before, "nothing was written");
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn a_version_conflict_shows_the_same_text_as_the_command_and_r_retries_with_the_current_data() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "status", "Status");
    tui.pick("Status", "back", "Backlog");
    tui.pick("What to change", "", "Name");
    tui.wait_for("New name");
    let run = tasker(
        &root,
        &[
            "status",
            "update",
            "Backlog",
            "--color",
            "#123456",
            "-w",
            &ws(&workspace),
            "-p",
            "Golden",
        ],
    );
    assert_eq!(run.code, 0, "{}", run.stderr);
    tui.send(" 2");
    tui.wait_for("> Backlog 2");
    tui.send(ENTER);
    let screen = tui.wait_for(" Conflict ");
    assert!(screen.contains("Modified by someone else:"), "{screen}");
    tui.send("r");
    tui.done("Updated status 'Backlog 2'");
    assert_eq!(tui.quit(), 0);
    let run = tasker(&root, &["status", "get", "Backlog 2", "-w", &ws(&workspace), "-p", "Golden"]);
    assert_eq!(run.code, 0, "{}", run.stderr);
    assert!(run.stdout.contains("color:        #123456"), "{}", run.stdout);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn an_error_of_the_command_is_shown_and_the_steps_stay_to_fix_it() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "series", "Series");
    tui.type_text("Series name", "Second");
    tui.type_text("Prefix", "GLD");
    let screen = tui.wait_for(" Error ");
    assert!(
        screen.contains("In use: Series prefix 'GLD' is already used in the project"),
        "{screen}"
    );
    tui.send(ENTER);
    let screen = tui.wait_for("Prefix");
    assert!(screen.contains("> GLD"), "{screen}");
    tui.send("\x7f\x7f\x7fSEC");
    tui.wait_for("> SEC");
    tui.send(ENTER);
    tui.done("Created series 'Second' (SEC)");
    assert_eq!(tui.quit(), 0);
    std::fs::remove_dir_all(&root).unwrap();
}

fn get(root: &Path, workspace: &Path, args: &[&str]) -> Run {
    let mut full: Vec<&str> = args.to_vec();
    let w = ws(workspace);
    full.extend(["-w", &w, "-p", "Golden"]);
    let run = tasker(root, &full);
    assert_eq!(run.code, 0, "{}: {}", args.join(" "), run.stderr);
    run
}

#[test]
fn project_user_and_agent_are_created_renamed_and_deleted() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "proj", "Project");
    tui.type_text("Project name", "Sandbox");
    tui.done("Created project 'Sandbox'");
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "proj", "Project");
    tui.pick("Project", "sand", "Sandbox");
    tui.wait_for("> Sandbox");
    tui.send(" 2");
    tui.send(ENTER);
    tui.done("Updated project 'Sandbox 2'");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "proj", "Project");
    tui.pick("Project", "sand", "Sandbox 2");
    let screen = tui.wait_for("Delete project 'Sandbox 2'?");
    assert!(screen.contains("Project 'Sandbox 2' is empty and will be deleted."), "{screen}");
    tui.send("y");
    tui.done("Deleted project 'Sandbox 2'");

    for (kind, filter) in [("User", "user"), ("Agent", "agent")] {
        tui.pick("What to do", DOWN, "Create");
        tui.pick("Create what", filter, kind);
        tui.type_text(&format!("{kind} name"), "robot");
        tui.done(&format!("Created {filter} 'robot'"));
        tui.pick("What to do", &DOWN.repeat(2), "Change");
        tui.pick("Change what", filter, kind);
        tui.pick(filter, "rob", "robot");
        tui.wait_for("> robot");
        tui.send("2");
        tui.send(ENTER);
        tui.done(&format!("Updated {filter} 'robot2'"));
        tui.pick("What to do", &DOWN.repeat(3), "Delete");
        tui.pick("Delete what", filter, kind);
        tui.pick(filter, "rob", "robot2");
        tui.wait_for(&format!("Delete {filter} 'robot2'?"));
        tui.send(ENTER);
        tui.done(&format!("Deleted {filter} 'robot2'"));
    }
    assert_eq!(tui.quit(), 0);
    let run = get(&root, &workspace, &["project", "list"]);
    assert!(!run.stdout.contains("Sandbox"), "{}", run.stdout);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn status_and_status_set_flows() {
    let (root, workspace) = fresh_copy();
    let editor = fake_editor(&root, "Needs a look\nsecond line\n");
    let mut tui = Tui::start(&root, &workspace, &["-p", "Golden"], &[("EDITOR", editor.to_str().unwrap())]);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "status", "Status");
    tui.type_text("Status name", "Review");
    tui.type_text("Color (#RRGGBB)", "#112233");
    tui.wait_for("Description (optional)");
    tui.send("e");
    tui.wait_for("Needs a look");
    tui.send(ENTER);
    tui.done("Created status 'Review'");
    let run = get(&root, &workspace, &["status", "get", "Review"]);
    assert!(
        run.stdout.contains("color:        #112233\n") && run.stdout.ends_with("Needs a look\nsecond line\n"),
        "{}",
        run.stdout
    );

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "status", "Status");
    tui.pick("Status", "rev", "Review");
    tui.pick("What to change", DOWN, "Color");
    tui.wait_for("> #112233");
    tui.send("\x7f\x7f\x7f\x7f\x7f\x7f445566");
    tui.send(ENTER);
    tui.done("Updated status 'Review'");
    let run = get(&root, &workspace, &["status", "get", "Review"]);
    assert!(run.stdout.contains("color:        #445566\n"), "{}", run.stdout);

    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "status set", "Status set");
    tui.type_text("Status set name", "Flow");
    tui.wait_for("Statuses of the set");
    tui.mark("rev", "Review", 1);
    tui.mark("done", "Done", 2);
    tui.send(ENTER);
    tui.done("Created status set 'Flow'");
    let run = get(&root, &workspace, &["status-set", "get", "Flow"]);
    let lines: Vec<&str> = run.stdout.lines().collect();
    assert!(
        lines[lines.len() - 2].starts_with("  Review (") && lines[lines.len() - 1].starts_with("  Done ("),
        "{}",
        run.stdout
    );

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "status set", "Status set");
    tui.pick("Status set", "flow", "Flow");
    tui.pick("What to change", "", "Name");
    tui.wait_for("> Flow");
    tui.send("2");
    tui.send(ENTER);
    tui.done("Updated status set 'Flow2'");

    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "status set", "Status set");
    tui.pick("Status set", "flow", "Flow2");
    tui.wait_for("Delete status set 'Flow2'?");
    tui.send("y");
    tui.done("Deleted status set 'Flow2'");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "status", "Status");
    tui.pick("Status", "rev", "Review");
    tui.wait_for("Delete status 'Review'?");
    tui.send("y");
    tui.done("Deleted status 'Review'");
    assert_eq!(tui.quit(), 0);
    let run = get(&root, &workspace, &["status", "list"]);
    assert!(!run.stdout.contains("Review"), "{}", run.stdout);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn task_type_link_type_and_series_flows() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "task type", "Task type");
    tui.type_text("Task type name", "Chore");
    tui.pick("Status set of the type", "main", "Main");
    tui.wait_for("Description (optional)");
    tui.send(ENTER);
    tui.wait_for("Fields of the catalog to attach");
    tui.mark("estim", "Estimate", 1);
    tui.send(ENTER);
    tui.pick("Must every task of the type have a value of 'Estimate'", DOWN, "No");
    tui.done("Created task type 'Chore'");
    let run = get(&root, &workspace, &["task-type", "get", "Chore"]);
    assert!(
        run.stdout.contains("status set:   Main") && run.stdout.contains("  Estimate (int)\n"),
        "{}",
        run.stdout
    );

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task type", "Task type");
    tui.pick("Task type", "chore", "Chore");
    tui.pick("What to change", "attach", "Attach a field");
    tui.pick("Field of the catalog", "tags", "Tags");
    tui.pick("Must every task of the type have a value of 'Tags'", "", "Yes");
    tui.done("Updated task type 'Chore'");
    let run = get(&root, &workspace, &["task-type", "get", "Chore"]);
    assert!(run.stdout.contains("  Tags (string[], required)\n"), "{}", run.stdout);

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task type", "Task type");
    tui.pick("Task type", "chore", "Chore");
    tui.pick("What to change", "detach", "Detach a field");
    tui.pick("Field to detach", "estim", "Estimate");
    tui.pick("Values of the field in the tasks of the type", "", "Clear them");
    tui.done("Updated task type 'Chore'");
    let run = get(&root, &workspace, &["task-type", "get", "Chore"]);
    assert!(!run.stdout.contains("Estimate"), "{}", run.stdout);

    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "task type", "Task type");
    tui.pick("Task type", "chore", "Chore");
    tui.wait_for("Delete task type 'Chore'?");
    tui.send("y");
    tui.done("Deleted task type 'Chore'");

    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "link type", "Link type");
    tui.type_text("Link type name", "Needs");
    tui.type_text("Outward name", "needs");
    tui.type_text("Inward name", "is needed by");
    tui.pick("May a chain of links of this type close a cycle", &DOWN.repeat(2), "Default");
    tui.pick("Is it a parent/child type", DOWN, "No");
    tui.done("Created link type 'Needs'");
    let run = get(&root, &workspace, &["link-type", "get", "Needs"]);
    assert!(
        run.stdout.contains("outward:      needs\n") && run.stdout.contains("inward:       is needed by\n"),
        "{}",
        run.stdout
    );

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "link type", "Link type");
    tui.pick("Link type", "needs", "Needs");
    tui.pick("What to change", "cycles", "Cycles allowed");
    tui.pick("May a chain of links of this type close a cycle", DOWN, "No");
    tui.done("Updated link type 'Needs'");
    let run = get(&root, &workspace, &["link-type", "get", "Needs"]);
    assert!(run.stdout.contains("allowCycles:  False\n"), "{}", run.stdout);

    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "link type", "Link type");
    tui.pick("Link type", "needs", "Needs");
    tui.wait_for("Delete link type 'Needs'?");
    tui.send(ENTER);
    tui.done("Deleted link type 'Needs'");

    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "series", "Series");
    tui.type_text("Series name", "Temp");
    tui.type_text("Prefix", "TMP");
    tui.done("Created series 'Temp' (TMP)");
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "series", "Series");
    tui.pick("Series", "tmp", "TMP");
    tui.pick("What to change", "", "Name");
    tui.wait_for("> Temp");
    tui.send("2");
    tui.send(ENTER);
    tui.done("Updated series 'Temp2' (TMP)");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "series", "Series");
    tui.pick("Series", "tmp", "TMP");
    tui.wait_for("Delete series 'Temp2' (TMP)?");
    tui.send("y");
    tui.done("Deleted series 'Temp2'");
    assert_eq!(tui.quit(), 0);
    let run = get(&root, &workspace, &["series", "list"]);
    assert!(!run.stdout.contains("TMP"), "{}", run.stdout);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn field_enum_and_board_flows() {
    let (root, workspace) = fresh_copy();
    let editor = fake_editor(&root, "S\nM\nL\n");
    let mut tui = Tui::start(&root, &workspace, &["-p", "Golden"], &[("EDITOR", editor.to_str().unwrap())]);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "enum", "Enum");
    tui.type_text("Enum name", "Size");
    tui.wait_for("Values, one per line");
    tui.send("e");
    tui.wait_for("│M");
    tui.send(ENTER);
    tui.done("Created enum 'Size'");
    let run = get(&root, &workspace, &["enum", "get", "Size"]);
    assert!(
        run.stdout.contains("  S  ") && run.stdout.contains("  M  ") && run.stdout.contains("  L  "),
        "{}",
        run.stdout
    );

    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "field", "Field");
    tui.type_text("Field name", "Shirt");
    tui.pick("Type of the value", "enum", "enum");
    tui.pick("Does the field hold several values", DOWN, "No");
    tui.pick("Enum of the values", "size", "Size");
    tui.done("Created field 'Shirt'");
    let run = get(&root, &workspace, &["field", "list"]);
    assert!(run.stdout.contains("Shirt        enum Size"), "{}", run.stdout);

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "enum", "Enum");
    tui.pick("Enum", "size", "Size");
    tui.pick("What to change", "add", "Add a value");
    tui.type_text("New value", "XL");
    tui.done("Updated enum 'Size'");
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "enum", "Enum");
    tui.pick("Enum", "size", "Size");
    tui.pick("What to change", "rename", "Rename a value");
    tui.pick("Value to rename", "S", "S");
    tui.type_text("New name of the value", "Small");
    tui.done("Updated enum 'Size'");
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "enum", "Enum");
    tui.pick("Enum", "size", "Size");
    tui.pick("What to change", "remove", "Remove a value");
    tui.pick("Value to remove", "xl", "XL");
    tui.pick("Tasks with this value", "", "Clear it");
    tui.done("Updated enum 'Size'");
    let run = get(&root, &workspace, &["enum", "get", "Size"]);
    assert!(run.stdout.contains("  Small  ") && !run.stdout.contains("XL"), "{}", run.stdout);

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "field", "Field");
    tui.pick("Field", "shirt", "Shirt");
    tui.pick("What to change", "", "Name");
    tui.wait_for("> Shirt");
    tui.send("2");
    tui.send(ENTER);
    tui.done("Updated field 'Shirt2'");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "field", "Field");
    tui.pick("Field", "shirt", "Shirt2");
    tui.wait_for("Delete field 'Shirt2'?");
    tui.send("y");
    tui.done("Deleted field 'Shirt2'");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "enum", "Enum");
    tui.pick("Enum", "size", "Size");
    tui.wait_for("Delete enum 'Size'?");
    tui.send("y");
    tui.done("Deleted enum 'Size'");

    let columns = fake_editor(&root, "Todo=Backlog\nDoing=In Progress\nFinished=Done\n");
    assert_eq!(tui.quit(), 0);
    let mut tui = Tui::start(&root, &workspace, &["-p", "Golden"], &[("EDITOR", columns.to_str().unwrap())]);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "board", "Board");
    tui.type_text("Board name", "Kanban");
    tui.wait_for("Status sets whose tasks are on the board");
    tui.mark("main", "Main", 1);
    tui.send(ENTER);
    tui.wait_for("One column per line");
    tui.send("e");
    tui.wait_for("Finished=Done");
    tui.send(ENTER);
    tui.done("Created board 'Kanban'");
    let run = get(&root, &workspace, &["board", "get", "Kanban"]);
    assert!(run.stdout.contains("Finished") && run.stdout.contains("Doing"), "{}", run.stdout);
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "board", "Board");
    tui.pick("Board", "kanban", "Kanban");
    tui.pick("What to change", "", "Name");
    tui.wait_for("> Kanban");
    tui.send("2");
    tui.send(ENTER);
    tui.done("Updated board 'Kanban2'");
    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "board", "Board");
    tui.pick("Board", "kanban", "Kanban2");
    tui.wait_for("Delete board 'Kanban2'?");
    tui.send("y");
    tui.done("Deleted board 'Kanban2'");
    assert_eq!(tui.quit(), 0);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn task_flows_cover_fields_series_parents_links_status_and_deletion() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", DOWN, "Create");
    tui.pick("Create what", "task", "Task");
    tui.type_text("Task title", "New bug");
    tui.pick("Task type", "bug", "Bug");
    tui.pick("Status", "", "(default: the first status of the type's set)");
    tui.wait_for("Description (optional)");
    tui.send(ENTER);
    tui.wait_for("Series to put the task into");
    tui.mark("bug", "BUG", 1);
    tui.send(ENTER);
    tui.wait_for("Parent tasks (optional)");
    tui.send(ENTER);
    tui.pick("Priority (required)", "high", "High");
    tui.type_text("Estimate (optional", "5");
    tui.type_text("Tags (optional", "one; two");
    let screen = tui.done("Created task 'New bug'");
    assert!(screen.contains("(BUG-"), "{screen}");
    let run = get(&root, &workspace, &["task", "list", "--series", "BUG", "--flat"]);
    let line = run.stdout.lines().find(|l| l.ends_with("New bug")).unwrap_or_default().to_string();
    let words: Vec<&str> = line.split_whitespace().collect();
    assert!(
        words.len() >= 4 && words[0].starts_with("BUG-") && words[1] == "Backlog" && words[2] == "Bug",
        "{}",
        run.stdout
    );
    let reference = words[0].to_string();
    let run = get(&root, &workspace, &["task", "get", &reference]);
    assert!(
        run.stdout.contains("Priority (enum, required): High")
            && run.stdout.contains("Estimate (int): 5")
            && run.stdout.contains("Tags (string[]): one, two"),
        "{}",
        run.stdout
    );

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug");
    tui.pick("What to change", "", "Title");
    tui.wait_for("> New bug");
    tui.send(" 2");
    tui.send(ENTER);
    tui.done("Updated task 'New bug 2'");

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "status", "Status");
    tui.pick("New status", "progress", "In Progress");
    tui.done("Updated task 'New bug 2'");

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "field", "Value of a field");
    tui.pick("Field", "estim", "Estimate");
    tui.wait_for("> 5");
    tui.send("\x7f7");
    tui.send(ENTER);
    tui.done("Updated task 'New bug 2'");
    let run = get(&root, &workspace, &["task", "get", &reference]);
    assert!(
        run.stdout.contains("status:       In Progress") && run.stdout.contains("Estimate (int): 7"),
        "{}",
        run.stdout
    );

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "parents", "Parents");
    tui.pick("Parents", "", "Add a parent");
    tui.pick("Parent task", "plain task", "GLD-1  Plain task");
    tui.pick("Hierarchical link type of the parent", "parent", "Parent/Child");
    tui.done("Updated task 'New bug 2'");
    let run = get(&root, &workspace, &["task", "links", &reference]);
    assert!(run.stdout.contains("is part of  GLD-1"), "{}", run.stdout);
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "parents", "Parents");
    tui.pick("Parents", DOWN, "Remove a parent");
    tui.pick("Parent to remove", "plain", "GLD-1  Plain task");
    tui.done("Updated task 'New bug 2'");
    let run = get(&root, &workspace, &["task", "links", &reference]);
    assert!(!run.stdout.contains("GLD-1"), "{}", run.stdout);

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "links", "Links");
    tui.pick("Links", "", "Link to another task");
    tui.pick("How this task relates to the other one", "blocks", "blocks");
    tui.pick("The other task", "gld-2  007", "GLD-2  007");
    tui.done("Linked:");
    let run = get(&root, &workspace, &["task", "links", &reference]);
    assert!(run.stdout.contains("blocks  GLD-2"), "{}", run.stdout);
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "links", "Links");
    tui.pick("Links", DOWN, "Remove a link");
    tui.pick("Link to remove", "blocks", "blocks GLD-2");
    tui.done("Unlinked:");

    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "series", "Series");
    tui.pick("Series", DOWN, "Remove from a series");
    tui.pick("Series to remove the task from", "bug", "BUG");
    tui.done("Removed");
    tui.pick("What to do", &DOWN.repeat(2), "Change");
    tui.pick("Change what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.pick("What to change", "series", "Series");
    tui.pick("Series", "", "Add to a series");
    tui.pick("Series to add the task to", "gld", "GLD");
    tui.done("GLD-");
    let run = get(&root, &workspace, &["task", "list", "--series", "GLD", "--flat", "--all"]);
    assert!(run.stdout.contains("New bug 2"), "{}", run.stdout);
    let reference = run
        .stdout
        .lines()
        .find(|l| l.contains("New bug 2"))
        .unwrap()
        .split_whitespace()
        .next()
        .unwrap()
        .to_string();

    tui.pick("What to do", &DOWN.repeat(3), "Delete");
    tui.pick("Delete what", "task", "Task");
    tui.pick("Task", "new bug", "New bug 2");
    tui.wait_for("Delete task '");
    tui.send("y");
    tui.done("Deleted task 'New bug 2'");
    assert_eq!(tui.quit(), 0);
    let run = tasker(&root, &["task", "get", &reference, "-w", &ws(&workspace), "-p", "Golden"]);
    assert_eq!(run.code, 1, "{}", run.stdout);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn browse_shows_filtered_task_lists_cards_boards_and_other_lists() {
    let (root, workspace) = fresh_copy();
    let mut tui = start(&root, &workspace);
    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "task", "Task");
    tui.pick("Tasks: what to show", "list", "List with filters");
    tui.wait_for("Only these task types");
    tui.send(ENTER);
    tui.wait_for("Only these statuses");
    tui.mark("done", "Done", 1);
    tui.send(ENTER);
    tui.wait_for("Only these series");
    tui.send(ENTER);
    tui.type_text("Field conditions", "");
    tui.type_text("Sort (optional)", "-created");
    tui.pick("Shape of the list", "flat", "Flat");
    let screen = tui.done("Found ");
    assert!(
        screen.contains("GLD-1") && screen.contains("Plain task") && !screen.contains("Backlog"),
        "{screen}"
    );

    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "task", "Task");
    tui.pick("Tasks: what to show", "card", "Card of a task");
    tui.pick("Task", "plain task", "GLD-1  Plain task");
    let screen = tui.done("title:        Plain task");
    assert!(screen.contains("Updated description"), "{screen}");

    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "board", "Board");
    tui.pick("Boards: what to show", "whole", "Whole board");
    tui.pick("Board", "main", "Main board");
    let screen = tui.done("Todo");
    assert!(screen.contains("Doing") && screen.contains("Done"), "{screen}");

    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "board", "Board");
    tui.pick("Boards: what to show", "one col", "One column");
    tui.pick("Board", "main", "Main board");
    tui.pick("Column", "doing", "Doing");
    tui.done("Found ");

    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "series", "Series");
    tui.pick("Series: what to show", "list", "List");
    let screen = tui.done("Found 2");
    assert!(screen.contains("GLD") && screen.contains("BUG"), "{screen}");

    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "status", "Status");
    tui.pick("Status: what to show", "card", "Card of one");
    tui.pick("Status", "backlog", "Backlog");
    tui.done("name:         Backlog");

    tui.pick("What to do", "", "Browse");
    tui.pick("Browse what", "user", "User");
    tui.done("Found ");
    assert_eq!(tui.quit(), 0);
    std::fs::remove_dir_all(&root).unwrap();
}
