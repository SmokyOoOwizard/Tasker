use super::flows;
use super::step::{Answer, Item, Key, Outcome, StepSpec, StepState};
use super::wizard::{FlowContext, Pending, Wizard};
use crate::errors::CliError;
use crate::session::Session;
use std::time::{Duration, Instant};
use tasker_core::model::Project;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Action {
    View,
    Create,
    Change,
    Delete,
}

impl Action {
    pub fn label(self) -> &'static str {
        match self {
            Action::View => "Browse",
            Action::Create => "Create",
            Action::Change => "Change",
            Action::Delete => "Delete",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Project,
    Task,
    Status,
    StatusSet,
    TaskType,
    Series,
    Field,
    Enum,
    Board,
    LinkType,
    User,
    Agent,
}

impl Kind {
    pub const ALL: [Kind; 12] = [
        Kind::Project,
        Kind::Task,
        Kind::Status,
        Kind::StatusSet,
        Kind::TaskType,
        Kind::Series,
        Kind::Field,
        Kind::Enum,
        Kind::Board,
        Kind::LinkType,
        Kind::User,
        Kind::Agent,
    ];

    pub fn key(self) -> &'static str {
        match self {
            Kind::Project => "project",
            Kind::Task => "task",
            Kind::Status => "status",
            Kind::StatusSet => "status-set",
            Kind::TaskType => "task-type",
            Kind::Series => "series",
            Kind::Field => "field",
            Kind::Enum => "enum",
            Kind::Board => "board",
            Kind::LinkType => "link-type",
            Kind::User => "user",
            Kind::Agent => "agent",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Kind::Project => "Project",
            Kind::Task => "Task",
            Kind::Status => "Status",
            Kind::StatusSet => "Status set",
            Kind::TaskType => "Task type",
            Kind::Series => "Series",
            Kind::Field => "Field",
            Kind::Enum => "Enum",
            Kind::Board => "Board",
            Kind::LinkType => "Link type",
            Kind::User => "User",
            Kind::Agent => "Agent",
        }
    }

    fn from_key(key: &str) -> Option<Kind> {
        Kind::ALL.into_iter().find(|k| k.key() == key)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Flow {
    Menu,
    Kinds(Action),
    Entity(Action, Kind),
    SwitchProject,
    Pick,
}

impl Flow {
    fn is_menu(self) -> bool {
        matches!(self, Flow::Menu | Flow::Kinds(_) | Flow::SwitchProject)
    }

    fn title(self) -> String {
        match self {
            Flow::Menu | Flow::Pick => String::new(),
            Flow::Kinds(action) => action.label().to_string(),
            Flow::Entity(_, kind) => kind.label().to_string(),
            Flow::SwitchProject => "Project".to_string(),
        }
    }
}

pub enum Next {
    Step(StepSpec),
    Enter(Flow),
    Run(Vec<String>),
    SwitchProject(Project),
    JumpTo(usize),
    Quit,
}

pub enum Effect {
    None,
    Quit,
    Run(Vec<String>),
    OpenEditor(String),
}

struct Frame {
    flow: Flow,
    answers: Vec<Answer>,
    titles: Vec<String>,
    tail: Vec<(String, Answer)>,
    current: Option<(String, Answer)>,
    spec: StepSpec,
    state: StepState,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResultKind {
    Done,
    View,
    Failed,
    Retryable,
}

pub struct ResultView {
    pub text: String,
    pub kind: ResultKind,
    pub scroll: u16,
    pub at: Instant,
}

pub struct App {
    session: Session,
    pub project: Option<Project>,
    frames: Vec<Frame>,
    pub result: Option<ResultView>,
    pub quit: bool,
    pub page: usize,
    pub refresh: Duration,
}

fn answer_summary(answer: &Answer) -> String {
    match answer {
        Answer::Choice(item) => item.label.clone(),
        Answer::Choices(items) if items.is_empty() => "(none)".to_string(),
        Answer::Choices(items) => items.iter().map(|i| i.label.as_str()).collect::<Vec<_>>().join(", "),
        Answer::Text(text) if text.is_empty() => "(empty)".to_string(),
        Answer::Text(text) => text.lines().next().unwrap_or_default().to_string(),
        Answer::Confirmed => "yes".to_string(),
    }
}

fn fits(spec: &StepSpec, answer: &Answer) -> bool {
    match (spec, answer) {
        (StepSpec::Select { items, .. }, Answer::Choice(item)) => items.iter().any(|i| i.key == item.key),
        (StepSpec::MultiSelect { items, allow_empty, .. }, Answer::Choices(chosen)) => {
            (*allow_empty || !chosen.is_empty()) && chosen.iter().all(|c| items.iter().any(|i| i.key == c.key))
        }
        (StepSpec::Text { check, allow_empty, .. }, Answer::Text(text)) => check.error(text, *allow_empty).is_none(),
        (StepSpec::Editor { .. }, Answer::Text(_)) => true,
        _ => false,
    }
}

impl App {
    pub fn new(session: Session, project: Option<Project>, refresh: Duration) -> App {
        let mut app = App {
            session,
            project,
            frames: Vec::new(),
            result: None,
            quit: false,
            page: 10,
            refresh,
        };
        let _ = app.enter(Flow::Menu);
        if app.project.is_none() && app.session.all_projects().map(|p| p.len() > 1).unwrap_or(false) {
            let _ = app.enter(Flow::SwitchProject);
        }
        app
    }

    pub fn session(&self) -> &Session {
        &self.session
    }

    pub fn workspace_label(&self) -> String {
        self.session.location().path().to_string()
    }

    pub fn breadcrumb(&self) -> String {
        let mut parts: Vec<String> = Vec::new();
        for frame in &self.frames {
            let title = frame.flow.title();
            if !title.is_empty() {
                parts.push(title);
            }
        }
        if let Some(frame) = self.frames.last() {
            parts.push(frame.spec.title().to_string());
        }
        parts.join(" › ")
    }

    pub fn state(&self) -> Option<&StepState> {
        self.frames.last().map(|f| &f.state)
    }

    pub fn state_mut(&mut self) -> Option<&mut StepState> {
        self.frames.last_mut().map(|f| &mut f.state)
    }

    pub fn in_menu(&self) -> bool {
        self.frames.last().is_some_and(|f| f.flow.is_menu())
    }

    pub fn current_spec(&self) -> Option<&StepSpec> {
        self.frames.last().map(|f| &f.spec)
    }

    fn next(&self, flow: Flow, answers: &[Answer], fresh: bool) -> Result<Next, Pending> {
        let ctx = FlowContext {
            session: &self.session,
            project: self.project.as_ref(),
            fresh,
        };
        let mut wizard = Wizard::new(&ctx, answers);
        match flow {
            Flow::Menu => {
                let choice = wizard.plain_menu(
                    "What to do",
                    vec![
                        Item::new("browse", "Browse").detail("lists with filters, cards, boards"),
                        Item::new("create", "Create")
                            .detail("a project, task, status, set, type, series, field, enum, board, link type, user or agent"),
                        Item::new("change", "Change").detail("the same entities"),
                        Item::new("delete", "Delete").detail("the same entities, with confirmation"),
                        Item::new("project", "Switch project").detail(
                            self.project
                                .as_ref()
                                .map(|p| format!("now: {}", p.name))
                                .unwrap_or_else(|| "none chosen".to_string()),
                        ),
                        Item::new("quit", "Quit").detail("q or Ctrl+C"),
                    ],
                )?;
                Ok(match choice.key.as_str() {
                    "browse" => Next::Enter(Flow::Kinds(Action::View)),
                    "create" => Next::Enter(Flow::Kinds(Action::Create)),
                    "change" => Next::Enter(Flow::Kinds(Action::Change)),
                    "delete" => Next::Enter(Flow::Kinds(Action::Delete)),
                    "project" => Next::Enter(Flow::SwitchProject),
                    _ => Next::Quit,
                })
            }
            Flow::Kinds(action) => {
                let items = Kind::ALL.into_iter().map(|k| Item::new(k.key(), k.label())).collect();
                let choice = wizard.select(&format!("{} what", action.label()), items)?;
                Ok(Next::Enter(Flow::Entity(action, Kind::from_key(&choice.key).unwrap_or(Kind::Task))))
            }
            Flow::SwitchProject => {
                let projects = self.session.all_projects()?;
                let items = projects
                    .iter()
                    .map(|p| Item::new(tasker_core::ids::guid_d(&p.id), &p.name))
                    .collect();
                let choice = wizard.select("Choose a project", items)?;
                let project = projects
                    .into_iter()
                    .find(|p| tasker_core::ids::guid_d(&p.id) == choice.key)
                    .ok_or_else(|| CliError::new("No such project"))?;
                Ok(Next::SwitchProject(project))
            }
            Flow::Entity(action, kind) => Ok(Next::Run(flows::run(&mut wizard, action, kind)?)),
            Flow::Pick => {
                let parent = self
                    .frames
                    .iter()
                    .rev()
                    .find(|f| matches!(f.flow, Flow::Entity(..)))
                    .ok_or_else(|| CliError::new("Nothing to change"))?;
                let items = parent
                    .titles
                    .iter()
                    .zip(&parent.answers)
                    .enumerate()
                    .map(|(index, (title, answer))| Item::new(index.to_string(), title).detail(answer_summary(answer)))
                    .collect();
                let choice = wizard.menu("Which setting to change", items)?;
                Ok(Next::JumpTo(choice.key.parse().unwrap_or(0)))
            }
        }
    }

    fn enter(&mut self, flow: Flow) -> Effect {
        let push = |app: &mut App, spec: StepSpec| {
            let state = StepState::from_spec(&spec);
            app.frames.push(Frame {
                flow,
                answers: Vec::new(),
                titles: Vec::new(),
                tail: Vec::new(),
                current: None,
                spec,
                state,
            });
        };
        match self.next(flow, &[], false) {
            Ok(Next::Step(spec)) | Err(Pending::Step(spec)) => {
                push(self, spec);
                Effect::None
            }
            Ok(Next::Run(argv)) => {
                push(
                    self,
                    StepSpec::Confirm {
                        title: flow.title(),
                        text: String::new(),
                    },
                );
                Effect::Run(self.with_globals(argv))
            }
            Ok(_) => Effect::None,
            Err(Pending::Failed(error)) => {
                self.fail(error.text());
                Effect::None
            }
        }
    }

    fn fail(&mut self, text: String) {
        self.result = Some(ResultView {
            text,
            kind: ResultKind::Failed,
            scroll: 0,
            at: Instant::now(),
        });
    }

    fn advance(&mut self, answer: Answer) -> Effect {
        let Some(frame) = self.frames.last_mut() else {
            return Effect::None;
        };
        let flow = frame.flow;
        let mut answers = frame.answers.clone();
        let mut titles = frame.titles.clone();
        let mut tail = std::mem::take(&mut frame.tail);
        frame.current = None;
        titles.push(frame.spec.title().to_string());
        answers.push(answer);
        loop {
            let next = self.next(flow, &answers, false);
            if let Err(Pending::Step(spec)) | Ok(Next::Step(spec)) = &next
                && let Some((title, old)) = tail.first()
                && title == spec.title()
                && fits(spec, old)
            {
                titles.push(title.clone());
                answers.push(old.clone());
                tail.remove(0);
                continue;
            }
            let frame = self.frames.last_mut().expect("a frame");
            frame.answers = answers;
            frame.titles = titles;
            return match next {
                Err(Pending::Step(spec)) | Ok(Next::Step(spec)) => {
                    frame.state = StepState::from_spec(&spec);
                    frame.spec = spec;
                    Effect::None
                }
                Ok(Next::Enter(next)) => self.enter(next),
                Ok(Next::Run(argv)) => Effect::Run(self.with_globals(argv)),
                Ok(Next::SwitchProject(project)) => {
                    self.project = Some(project);
                    self.frames.pop();
                    self.refresh_menu();
                    Effect::None
                }
                Ok(Next::JumpTo(index)) => {
                    self.frames.pop();
                    self.jump_to(index);
                    Effect::None
                }
                Ok(Next::Quit) => Effect::Quit,
                Err(Pending::Failed(error)) => {
                    self.fail(error.text());
                    Effect::None
                }
            };
        }
    }

    fn jump_to(&mut self, index: usize) {
        let Some(frame) = self.frames.last_mut() else { return };
        if index >= frame.answers.len() {
            return;
        }
        let answers = frame.answers.split_off(index);
        let titles = frame.titles.split_off(index);
        let mut rest: Vec<(String, Answer)> = titles.into_iter().zip(answers).collect();
        let (title, last) = rest.remove(0);
        frame.tail = rest;
        self.show_step(title, last);
    }

    fn show_step(&mut self, title: String, last: Answer) {
        let frame = self.frames.last().expect("a frame");
        match self.next(frame.flow, &frame.answers, false) {
            Err(Pending::Step(spec)) | Ok(Next::Step(spec)) => {
                let spec = spec.with_initial(&last);
                let frame = self.frames.last_mut().expect("a frame");
                frame.state = StepState::from_spec(&spec);
                frame.spec = spec;
                frame.current = Some((title, last));
            }
            Err(Pending::Failed(error)) => self.fail(error.text()),
            Ok(_) => {}
        }
    }

    fn refresh_menu(&mut self) {
        let Some(frame) = self.frames.last_mut() else { return };
        if frame.flow != Flow::Menu {
            return;
        }
        frame.answers.clear();
        let cursor = match &frame.state {
            StepState::List(list) => list.cursor,
            _ => 0,
        };
        if let Ok(Next::Step(spec)) | Err(Pending::Step(spec)) = self.next(Flow::Menu, &[], false) {
            let frame = self.frames.last_mut().expect("a frame");
            frame.state = StepState::from_spec(&spec);
            if let StepState::List(list) = &mut frame.state {
                list.cursor = cursor;
            }
            frame.spec = spec;
        }
    }

    fn back(&mut self) {
        let Some(frame) = self.frames.last_mut() else { return };
        if frame.answers.is_empty() {
            if self.frames.len() > 1 {
                self.frames.pop();
                self.back();
            }
            return;
        }
        if let Some(known) = frame.current.take() {
            frame.tail.insert(0, known);
        }
        let last = frame.answers.pop().expect("an answer");
        let title = frame.titles.pop().unwrap_or_default();
        self.show_step(title, last);
    }

    fn return_to_menu(&mut self) {
        while self
            .frames
            .last()
            .is_some_and(|f| matches!(f.flow, Flow::Entity(..) | Flow::Kinds(_) | Flow::Pick))
        {
            self.frames.pop();
        }
        self.refresh_menu();
    }

    fn with_globals(&self, mut argv: Vec<String>) -> Vec<String> {
        let location = self.session.location();
        let mut full: Vec<String> = Vec::new();
        if location.kind() == tasker_core::settings::WorkspaceKind::Sqlite {
            full.push("--sqlite".into());
        } else {
            full.push("-w".into());
        }
        full.push(location.path().to_string());
        if let Some(project) = &self.project {
            full.push("-p".into());
            full.push(tasker_core::ids::guid_d(&project.id));
        }
        full.append(&mut argv);
        full
    }

    pub fn retry_argv(&self) -> Option<Vec<String>> {
        self.rerun_argv(true)
    }

    pub fn refresh_argv(&self) -> Option<Vec<String>> {
        self.rerun_argv(false)
    }

    fn rerun_argv(&self, fresh: bool) -> Option<Vec<String>> {
        let frame = self.frames.last()?;
        match self.next(frame.flow, &frame.answers, fresh) {
            Ok(Next::Run(argv)) => Some(self.with_globals(argv)),
            _ => None,
        }
    }

    pub fn refresh_due(&self) -> Option<Duration> {
        let result = self.result.as_ref()?;
        (result.kind == ResultKind::View).then(|| self.refresh.saturating_sub(result.at.elapsed()))
    }

    fn viewing(&self) -> bool {
        self.frames.last().is_some_and(|f| matches!(f.flow, Flow::Entity(Action::View, _)))
    }

    pub fn set_result(&mut self, stdout: &str, stderr: &str, code: i32) {
        let mut text = String::new();
        text.push_str(stdout);
        if !stderr.is_empty() {
            if !text.is_empty() && !text.ends_with('\n') {
                text.push('\n');
            }
            text.push_str(stderr);
        }
        let text = text.trim_end().to_string();
        let kind = if code == 0 && self.viewing() {
            ResultKind::View
        } else if code == 0 {
            ResultKind::Done
        } else if stderr.starts_with("Modified by someone else:") || stderr.starts_with("Locked:") {
            ResultKind::Retryable
        } else {
            ResultKind::Failed
        };
        let scroll = match &self.result {
            Some(previous) if previous.kind == ResultKind::View && kind == ResultKind::View => previous.scroll,
            _ => 0,
        };
        self.result = Some(ResultView {
            text,
            kind,
            scroll,
            at: Instant::now(),
        });
    }

    pub fn editor_done(&mut self, text: Option<String>) {
        if let Some(StepState::Editor(editor)) = self.state_mut() {
            match text {
                Some(text) => editor.text = text,
                None => editor.message = Some("The editor did not finish: the text is unchanged".to_string()),
            }
        }
    }

    fn close_result(&mut self) {
        let Some(result) = self.result.take() else { return };
        if result.kind == ResultKind::Done {
            self.return_to_menu();
        } else if result.kind == ResultKind::View
            || self
                .frames
                .last()
                .is_some_and(|f| !f.answers.is_empty() && matches!(f.flow, Flow::Entity(..)))
        {
            self.back();
        }
    }

    pub fn handle(&mut self, key: Key) -> Effect {
        if key == Key::CtrlC {
            return Effect::Quit;
        }
        if let Some(result) = &mut self.result {
            match key {
                Key::Up => result.scroll = result.scroll.saturating_sub(1),
                Key::Down => result.scroll = result.scroll.saturating_add(1),
                Key::PageUp => result.scroll = result.scroll.saturating_sub(self.page as u16),
                Key::PageDown => result.scroll = result.scroll.saturating_add(self.page as u16),
                Key::Char('r') | Key::Char('R') if result.kind == ResultKind::Retryable => {
                    if let Some(argv) = self.retry_argv() {
                        self.result = None;
                        return Effect::Run(argv);
                    }
                }
                Key::Char('r') | Key::Char('R') | Key::Enter if result.kind == ResultKind::View => {
                    if let Some(argv) = self.refresh_argv() {
                        return Effect::Run(argv);
                    }
                }
                Key::Char('f') | Key::Char('F')
                    if result.kind == ResultKind::View && self.frames.last().is_some_and(|f| !f.answers.is_empty()) =>
                {
                    self.result = None;
                    return self.enter(Flow::Pick);
                }
                Key::Char('q') | Key::Char('Q') if result.kind == ResultKind::View => {
                    self.result = None;
                    self.return_to_menu();
                }
                Key::Enter | Key::Esc | Key::Char('q') => self.close_result(),
                _ => {}
            }
            return Effect::None;
        }
        if self.in_menu() && matches!(key, Key::Char('q') | Key::Char('Q')) {
            return Effect::Quit;
        }
        let page = self.page;
        let Some(state) = self.state_mut() else {
            return Effect::None;
        };
        match state.handle(key, page) {
            Outcome::Nothing => Effect::None,
            Outcome::Back => {
                if self.frames.last().is_some_and(|f| f.flow == Flow::Pick) {
                    self.frames.pop();
                    return self.refresh_argv().map(Effect::Run).unwrap_or(Effect::None);
                }
                self.back();
                Effect::None
            }
            Outcome::OpenEditor(text) => Effect::OpenEditor(text),
            Outcome::Answer(answer) => self.advance(answer),
        }
    }
}
