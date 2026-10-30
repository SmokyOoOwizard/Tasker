pub mod app;
pub mod flows;
pub mod step;
#[cfg(test)]
mod tests;
pub mod ui;
pub mod wizard;

use crate::errors::{ERROR_EXIT_CODE, Result};
use crate::session::Session;
use app::{App, Effect};
use crossterm::event::{Event, KeyCode, KeyEventKind, KeyModifiers};
use std::io::Write;
use std::time::Duration;
use step::Key;

pub const DEFAULT_REFRESH: Duration = Duration::from_secs(10);

pub const NOT_A_TERMINAL: &str = "Interactive mode needs a terminal: run 'tasker -i' in a terminal, not with redirected input or output";

pub fn run(workspace: Option<&str>, sqlite: Option<&str>, project: Option<&str>, err: &mut dyn Write) -> i32 {
    if !crate::terminal::stdin_is_terminal() || !crate::terminal::stdout_is_terminal() {
        let _ = writeln!(err, "Error: {NOT_A_TERMINAL}");
        return ERROR_EXIT_CODE;
    }
    match open(workspace, sqlite, project) {
        Ok(app) => match drive(app) {
            Ok(()) => 0,
            Err(error) => {
                let _ = writeln!(err, "Error: {error}");
                ERROR_EXIT_CODE
            }
        },
        Err(error) => {
            let _ = writeln!(err, "{}", error.text());
            ERROR_EXIT_CODE
        }
    }
}

fn open(workspace: Option<&str>, sqlite: Option<&str>, project: Option<&str>) -> Result<App> {
    let session = Session::open(workspace, sqlite, false)?;
    let chosen = match project.filter(|r| !r.trim().is_empty()) {
        Some(reference) => Some(session.find_project(reference)?),
        None => {
            let projects = session.all_projects()?;
            if projects.len() == 1 { projects.into_iter().next() } else { None }
        }
    };
    Ok(App::new(session, chosen, refresh_interval()))
}

fn refresh_interval() -> Duration {
    std::env::var("TASKER_REFRESH_SECONDS")
        .ok()
        .and_then(|v| v.trim().parse::<f64>().ok())
        .filter(|v| v.is_finite() && *v > 0.0)
        .map(Duration::from_secs_f64)
        .unwrap_or(DEFAULT_REFRESH)
}

fn drive(mut app: App) -> std::io::Result<()> {
    let mut terminal = ratatui::try_init()?;
    let outcome = event_loop(&mut terminal, &mut app);
    ratatui::try_restore()?;
    outcome
}

fn event_loop(terminal: &mut ratatui::DefaultTerminal, app: &mut App) -> std::io::Result<()> {
    loop {
        terminal.draw(|frame| ui::draw(frame, app))?;
        let event = match app.refresh_due() {
            Some(left) => {
                if !crossterm::event::poll(left.min(Duration::from_secs(1)))? {
                    if app.refresh_due().is_some_and(|left| left.is_zero()) {
                        if app.result.is_some() {
                            if let Some(argv) = app.refresh_argv() {
                                execute(app, &argv);
                            }
                        } else {
                            app.refresh_step();
                        }
                    }
                    continue;
                }
                crossterm::event::read()?
            }
            None => crossterm::event::read()?,
        };
        let Some(key) = key_of(&event) else { continue };
        match app.handle(key) {
            Effect::None => {}
            Effect::Quit => return Ok(()),
            Effect::Run(argv) => execute(app, &argv),
            Effect::OpenEditor(text) => {
                ratatui::try_restore()?;
                let edited = edit_in_editor(&text);
                *terminal = ratatui::try_init()?;
                crossterm::execute!(std::io::stdout(), crossterm::terminal::Clear(crossterm::terminal::ClearType::All))?;
                app.editor_done(edited);
            }
        }
    }
}

fn execute(app: &mut App, argv: &[String]) {
    let (mut out, mut err) = (Vec::new(), Vec::new());
    let code = crate::app::run(argv, &mut out, &mut err);
    app.set_result(&String::from_utf8_lossy(&out), &String::from_utf8_lossy(&err), code);
}

fn key_of(event: &Event) -> Option<Key> {
    let Event::Key(key) = event else { return None };
    if key.kind == KeyEventKind::Release {
        return None;
    }
    let control = key.modifiers.contains(KeyModifiers::CONTROL);
    Some(match key.code {
        KeyCode::Char('c') | KeyCode::Char('C') if control => Key::CtrlC,
        KeyCode::Char('f') | KeyCode::Char('F') if control => Key::CtrlF,
        KeyCode::Char('l') | KeyCode::Char('L') if control => Key::CtrlL,
        KeyCode::Char(c) => Key::Char(c),
        KeyCode::Enter => Key::Enter,
        KeyCode::Esc => Key::Esc,
        KeyCode::Up => Key::Up,
        KeyCode::Down => Key::Down,
        KeyCode::Left => Key::Left,
        KeyCode::Right => Key::Right,
        KeyCode::PageUp => Key::PageUp,
        KeyCode::PageDown => Key::PageDown,
        KeyCode::Home => Key::Home,
        KeyCode::End => Key::End,
        KeyCode::Backspace => Key::Backspace,
        KeyCode::Delete => Key::Delete,
        KeyCode::Tab => Key::Tab,
        _ => return None,
    })
}

pub fn editor_command() -> String {
    for name in ["VISUAL", "EDITOR"] {
        if let Ok(value) = std::env::var(name)
            && !value.trim().is_empty()
        {
            return value;
        }
    }
    if cfg!(windows) { "notepad".to_string() } else { "vi".to_string() }
}

fn edit_in_editor(text: &str) -> Option<String> {
    let dir = std::env::temp_dir();
    let file = dir.join(format!("tasker-{}-{}.md", std::process::id(), uuid::Uuid::new_v4().simple()));
    std::fs::write(&file, text).ok()?;
    let command = editor_command();
    let mut words = shell_words(&command);
    if words.is_empty() {
        return None;
    }
    let program = words.remove(0);
    let status = std::process::Command::new(program).args(words).arg(&file).status();
    let result = match status {
        Ok(status) if status.success() => std::fs::read_to_string(&file).ok().map(|t| t.replace("\r\n", "\n")),
        _ => None,
    };
    let _ = std::fs::remove_file(&file);
    result.map(|t| t.trim_end().to_string())
}

fn shell_words(text: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut current = String::new();
    let mut quote: Option<char> = None;
    for c in text.chars() {
        match quote {
            Some(q) if c == q => quote = None,
            Some(_) => current.push(c),
            None if c == '"' || c == '\'' => quote = Some(c),
            None if c.is_whitespace() => {
                if !current.is_empty() {
                    words.push(std::mem::take(&mut current));
                }
            }
            None => current.push(c),
        }
    }
    if !current.is_empty() {
        words.push(current);
    }
    words
}
