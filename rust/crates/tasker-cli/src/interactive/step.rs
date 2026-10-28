use crate::table::width;
use crate::terminal::parse_int;
use ratatui::widgets::ListState as ScrollState;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Item {
    pub key: String,
    pub label: String,
    pub detail: String,
    pub version: Option<String>,
}

impl Item {
    pub fn new(key: impl Into<String>, label: impl Into<String>) -> Item {
        Item {
            key: key.into(),
            label: label.into(),
            detail: String::new(),
            version: None,
        }
    }

    pub fn detail(mut self, detail: impl Into<String>) -> Item {
        self.detail = detail.into();
        self
    }

    pub fn version(mut self, version: &str) -> Item {
        self.version = Some(version.to_string());
        self
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Check {
    Any,
    Required,
    Int,
    Float,
    Date,
    Color,
    Prefix,
}

impl Check {
    pub fn error(self, text: &str, allow_empty: bool) -> Option<String> {
        let trimmed = text.trim();
        if trimmed.is_empty() {
            return if allow_empty || self == Check::Any {
                None
            } else {
                Some("A value is required".to_string())
            };
        }
        match self {
            Check::Any | Check::Required => None,
            Check::Int => parse_int(trimmed).is_none().then(|| format!("'{trimmed}' is not a whole number")),
            Check::Float => trimmed
                .parse::<f64>()
                .is_err()
                .then(|| format!("'{trimmed}' is not a number (use a dot for decimals)")),
            Check::Date => {
                let bytes = trimmed.as_bytes();
                let digits =
                    |from: usize, count: usize| bytes.len() >= from + count && bytes[from..from + count].iter().all(u8::is_ascii_digit);
                if bytes.len() == 10 && digits(0, 4) && bytes[4] == b'-' && digits(5, 2) && bytes[7] == b'-' && digits(8, 2) {
                    None
                } else {
                    Some(format!("'{trimmed}' is not a date (yyyy-MM-dd)"))
                }
            }
            Check::Color => {
                let hex = trimmed.strip_prefix('#').unwrap_or("");
                if hex.len() == 6 && hex.bytes().all(|b| b.is_ascii_hexdigit()) {
                    None
                } else {
                    Some(format!("'{trimmed}' is not a color in #RRGGBB format"))
                }
            }
            Check::Prefix => {
                if tasker_core::model::SeriesPrefix::is_valid(Some(trimmed)) {
                    None
                } else {
                    Some("Prefix must be 1-20 Latin letters or digits".to_string())
                }
            }
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StepSpec {
    Select {
        title: String,
        items: Vec<Item>,
        initial: Option<String>,
        filterable: bool,
    },
    MultiSelect {
        title: String,
        items: Vec<Item>,
        initial: Vec<String>,
        allow_empty: bool,
    },
    Text {
        title: String,
        initial: String,
        hint: String,
        check: Check,
        allow_empty: bool,
    },
    Editor {
        title: String,
        initial: String,
        hint: String,
    },
    Confirm {
        title: String,
        text: String,
    },
    Form {
        title: String,
        sections: Vec<Section>,
        submit: String,
    },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Chain {
    pub spec: StepSpec,
    pub then: Vec<(String, Chain)>,
    pub prefix: bool,
}

impl Chain {
    pub fn new(spec: StepSpec) -> Chain {
        Chain {
            spec,
            then: Vec::new(),
            prefix: false,
        }
    }

    pub fn then(mut self, key: impl Into<String>, next: Chain) -> Chain {
        self.then.push((key.into(), next));
        self
    }

    pub fn prefix(mut self) -> Chain {
        self.prefix = true;
        self
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SectionKind {
    Multi { items: Vec<Item> },
    Single { items: Vec<Item> },
    List { add: Chain, add_label: String },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Section {
    pub key: String,
    pub label: String,
    pub empty: String,
    pub kind: SectionKind,
    pub values: Vec<String>,
}

impl Section {
    pub fn new(key: impl Into<String>, label: impl Into<String>, empty: impl Into<String>, kind: SectionKind) -> Section {
        Section {
            key: key.into(),
            label: label.into(),
            empty: empty.into(),
            kind,
            values: Vec::new(),
        }
    }

    pub fn values(mut self, values: Vec<String>) -> Section {
        self.values = values;
        self
    }

    pub fn summary(&self) -> String {
        if self.values.is_empty() {
            return self.empty.clone();
        }
        match &self.kind {
            SectionKind::Multi { items } | SectionKind::Single { items } => self
                .values
                .iter()
                .map(|v| {
                    items
                        .iter()
                        .find(|i| i.key == *v)
                        .map(|i| i.label.clone())
                        .unwrap_or_else(|| v.clone())
                })
                .collect::<Vec<_>>()
                .join(", "),
            SectionKind::List { .. } => self.values.join(", "),
        }
    }

    fn accepts(&self, value: &str) -> bool {
        match &self.kind {
            SectionKind::Multi { items } | SectionKind::Single { items } => items.iter().any(|i| i.key == value),
            SectionKind::List { .. } => true,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FormValue {
    pub key: String,
    pub label: String,
    pub values: Vec<String>,
    pub summary: String,
}

pub type FormValues = Vec<FormValue>;

impl StepSpec {
    pub fn title(&self) -> &str {
        match self {
            StepSpec::Select { title, .. }
            | StepSpec::MultiSelect { title, .. }
            | StepSpec::Text { title, .. }
            | StepSpec::Editor { title, .. }
            | StepSpec::Confirm { title, .. }
            | StepSpec::Form { title, .. } => title,
        }
    }

    pub fn with_initial(mut self, answer: &Answer) -> StepSpec {
        match (&mut self, answer) {
            (StepSpec::Select { initial, .. }, Answer::Choice(item)) => *initial = Some(item.key.clone()),
            (StepSpec::MultiSelect { initial, .. }, Answer::Choices(items)) => *initial = items.iter().map(|i| i.key.clone()).collect(),
            (StepSpec::Text { initial, .. }, Answer::Text(text)) | (StepSpec::Editor { initial, .. }, Answer::Text(text)) => {
                *initial = text.clone();
            }
            (StepSpec::Form { sections, .. }, Answer::Form(values)) => {
                for section in sections.iter_mut() {
                    if let Some(value) = values.iter().find(|v| v.key == section.key) {
                        section.values = value.values.iter().filter(|v| section.accepts(v)).cloned().collect();
                    }
                }
            }
            _ => {}
        }
        self
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Answer {
    Choice(Item),
    Choices(Vec<Item>),
    Text(String),
    Confirmed,
    Form(FormValues),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Key {
    Char(char),
    Enter,
    Esc,
    Up,
    Down,
    Left,
    Right,
    PageUp,
    PageDown,
    Home,
    End,
    Backspace,
    Delete,
    Tab,
    CtrlC,
    CtrlF,
}

pub enum Outcome {
    Nothing,
    Answer(Answer),
    Back,
    OpenEditor(String),
}

pub struct ListStep {
    pub items: Vec<Item>,
    pub filter: String,
    pub cursor: usize,
    pub picked: Vec<String>,
    pub multi: bool,
    pub allow_empty: bool,
    pub filterable: bool,
    pub message: Option<String>,
    pub scroll: ScrollState,
}

impl ListStep {
    fn new(items: Vec<Item>, filterable: bool) -> ListStep {
        ListStep {
            items,
            filter: String::new(),
            cursor: 0,
            picked: Vec::new(),
            multi: false,
            allow_empty: true,
            filterable,
            message: None,
            scroll: ScrollState::default(),
        }
    }

    pub fn visible(&self) -> Vec<usize> {
        let wanted = self.filter.to_lowercase();
        self.items
            .iter()
            .enumerate()
            .filter(|(_, item)| {
                wanted.is_empty() || item.label.to_lowercase().contains(&wanted) || item.detail.to_lowercase().contains(&wanted)
            })
            .map(|(index, _)| index)
            .collect()
    }

    pub fn current(&self) -> Option<&Item> {
        self.visible().get(self.cursor).map(|&index| &self.items[index])
    }

    pub fn pick_number(&self, key: &str) -> Option<usize> {
        self.picked.iter().position(|k| k == key).map(|p| p + 1)
    }

    fn clamp(&mut self) {
        let count = self.visible().len();
        self.cursor = if count == 0 { 0 } else { self.cursor.min(count - 1) };
    }

    fn handle(&mut self, key: Key, page: usize) -> Outcome {
        self.message = None;
        let count = self.visible().len();
        match key {
            Key::Up => self.cursor = self.cursor.saturating_sub(1),
            Key::Down => self.cursor = (self.cursor + 1).min(count.saturating_sub(1)),
            Key::PageUp => self.cursor = self.cursor.saturating_sub(page),
            Key::PageDown => self.cursor = (self.cursor + page).min(count.saturating_sub(1)),
            Key::Home => self.cursor = 0,
            Key::End => self.cursor = count.saturating_sub(1),
            Key::Esc => return Outcome::Back,
            Key::Backspace => {
                self.filter.pop();
                self.clamp();
            }
            Key::Char(' ') | Key::Tab if self.multi => {
                if let Some(item) = self.current() {
                    let key = item.key.clone();
                    match self.picked.iter().position(|k| *k == key) {
                        Some(at) => {
                            self.picked.remove(at);
                        }
                        None => self.picked.push(key),
                    }
                }
            }
            Key::Enter => {
                if self.multi {
                    if self.picked.is_empty() && !self.allow_empty {
                        self.message = Some("Choose at least one (Space marks an item)".to_string());
                        return Outcome::Nothing;
                    }
                    let items: Vec<Item> = self
                        .picked
                        .iter()
                        .filter_map(|key| self.items.iter().find(|i| i.key == *key).cloned())
                        .collect();
                    return Outcome::Answer(Answer::Choices(items));
                }
                if let Some(item) = self.current() {
                    return Outcome::Answer(Answer::Choice(item.clone()));
                }
            }
            Key::Char(c) if self.filterable && !c.is_control() => {
                self.filter.push(c);
                self.clamp();
            }
            _ => {}
        }
        Outcome::Nothing
    }
}

pub struct TextStep {
    pub value: String,
    pub cursor: usize,
    pub hint: String,
    pub check: Check,
    pub allow_empty: bool,
    pub error: Option<String>,
}

impl TextStep {
    pub fn cursor_column(&self) -> usize {
        width(&self.value.chars().take(self.cursor).collect::<String>())
    }

    fn byte_at(&self, chars: usize) -> usize {
        self.value.char_indices().nth(chars).map(|(at, _)| at).unwrap_or(self.value.len())
    }

    fn handle(&mut self, key: Key) -> Outcome {
        self.error = None;
        let length = self.value.chars().count();
        match key {
            Key::Char(c) if !c.is_control() => {
                let at = self.byte_at(self.cursor);
                self.value.insert(at, c);
                self.cursor += 1;
            }
            Key::Backspace if self.cursor > 0 => {
                let at = self.byte_at(self.cursor - 1);
                self.value.remove(at);
                self.cursor -= 1;
            }
            Key::Delete if self.cursor < length => {
                let at = self.byte_at(self.cursor);
                self.value.remove(at);
            }
            Key::Left => self.cursor = self.cursor.saturating_sub(1),
            Key::Right => self.cursor = (self.cursor + 1).min(length),
            Key::Home => self.cursor = 0,
            Key::End => self.cursor = length,
            Key::Esc => return Outcome::Back,
            Key::Enter => match self.check.error(&self.value, self.allow_empty) {
                Some(error) => self.error = Some(error),
                None => return Outcome::Answer(Answer::Text(self.value.trim().to_string())),
            },
            _ => {}
        }
        Outcome::Nothing
    }
}

pub struct EditorStep {
    pub text: String,
    pub hint: String,
    pub message: Option<String>,
}

impl EditorStep {
    fn handle(&mut self, key: Key) -> Outcome {
        self.message = None;
        match key {
            Key::Enter => Outcome::Answer(Answer::Text(self.text.clone())),
            Key::Char('e') | Key::Char('E') => Outcome::OpenEditor(self.text.clone()),
            Key::Esc => Outcome::Back,
            _ => Outcome::Nothing,
        }
    }
}

pub struct ConfirmStep {
    pub text: String,
}

impl ConfirmStep {
    fn handle(&mut self, key: Key) -> Outcome {
        match key {
            Key::Enter | Key::Char('y') | Key::Char('Y') => Outcome::Answer(Answer::Confirmed),
            Key::Esc | Key::Char('n') | Key::Char('N') => Outcome::Back,
            _ => Outcome::Nothing,
        }
    }
}

pub enum Inner {
    List(ListStep),
    Text(TextStep),
}

pub struct Layer {
    pub title: String,
    pub inner: Inner,
    pub then: Vec<(String, Chain)>,
    pub prefix: bool,
}

impl Layer {
    fn of(spec: &StepSpec, then: Vec<(String, Chain)>, prefix: bool) -> Layer {
        let inner = match StepState::from_spec(spec) {
            StepState::List(list) => Inner::List(list),
            StepState::Text(text) => Inner::Text(text),
            _ => Inner::Text(TextStep {
                value: String::new(),
                cursor: 0,
                hint: String::new(),
                check: Check::Any,
                allow_empty: true,
                error: None,
            }),
        };
        Layer {
            title: spec.title().to_string(),
            inner,
            then,
            prefix,
        }
    }
}

pub struct Popup {
    pub section: usize,
    pub layers: Vec<Layer>,
    pieces: Vec<(String, bool)>,
}

impl Popup {
    pub fn layer(&self) -> &Layer {
        self.layers.last().expect("a layer")
    }

    pub fn layer_mut(&mut self) -> &mut Layer {
        self.layers.last_mut().expect("a layer")
    }
}

pub struct FormStep {
    pub sections: Vec<Section>,
    pub submit: String,
    pub cursor: usize,
    pub popup: Option<Popup>,
}

impl FormStep {
    pub fn values(&self) -> FormValues {
        self.sections
            .iter()
            .map(|s| FormValue {
                key: s.key.clone(),
                label: s.label.clone(),
                values: s.values.clone(),
                summary: s.summary(),
            })
            .collect()
    }

    fn entries_layer(section: &Section) -> Layer {
        let SectionKind::List { add_label, .. } = &section.kind else {
            unreachable!()
        };
        let mut items: Vec<Item> = section
            .values
            .iter()
            .enumerate()
            .map(|(at, v)| Item::new(format!("drop{at}"), v).detail("Enter removes it"))
            .collect();
        items.push(Item::new("add", format!("+ {add_label}")));
        items.push(Item::new("done", "Done"));
        let mut list = ListStep::new(items, true);
        list.cursor = section.values.len();
        Layer {
            title: section.label.clone(),
            inner: Inner::List(list),
            then: Vec::new(),
            prefix: false,
        }
    }

    fn open(&mut self) {
        let Some(section) = self.sections.get(self.cursor) else { return };
        let layer = match &section.kind {
            SectionKind::Multi { items } => {
                let mut list = ListStep::new(items.clone(), true);
                list.multi = true;
                list.picked = section.values.clone();
                Layer {
                    title: section.label.clone(),
                    inner: Inner::List(list),
                    then: Vec::new(),
                    prefix: false,
                }
            }
            SectionKind::Single { items } => {
                let mut list = ListStep::new(items.clone(), true);
                if let Some(at) = section.values.first().and_then(|v| items.iter().position(|i| i.key == *v)) {
                    list.cursor = at;
                }
                Layer {
                    title: section.label.clone(),
                    inner: Inner::List(list),
                    then: Vec::new(),
                    prefix: false,
                }
            }
            SectionKind::List { .. } => FormStep::entries_layer(section),
        };
        self.popup = Some(Popup {
            section: self.cursor,
            layers: vec![layer],
            pieces: Vec::new(),
        });
    }

    fn handle_popup(&mut self, key: Key, page: usize) {
        let Some(popup) = &mut self.popup else { return };
        let at = popup.section;
        let outcome = match &mut popup.layer_mut().inner {
            Inner::List(list) => list.handle(key, page),
            Inner::Text(text) => text.handle(key),
        };
        match outcome {
            Outcome::Nothing | Outcome::OpenEditor(_) => {}
            Outcome::Back => {
                popup.layers.pop();
                if popup.layers.is_empty() {
                    self.popup = None;
                } else {
                    let keep = popup.layers.len().saturating_sub(2);
                    popup.pieces.truncate(keep);
                }
            }
            Outcome::Answer(Answer::Choices(items)) => {
                self.sections[at].values = items.into_iter().map(|i| i.key).collect();
                self.popup = None;
            }
            Outcome::Answer(Answer::Choice(item)) if popup.layers.len() == 1 => match &self.sections[at].kind {
                SectionKind::Single { .. } => {
                    self.sections[at].values = vec![item.key];
                    self.popup = None;
                }
                SectionKind::List { add, .. } => match item.key.as_str() {
                    "done" => self.popup = None,
                    "add" => {
                        let layer = Layer::of(&add.spec, add.then.clone(), add.prefix);
                        popup.layers.push(layer);
                    }
                    key => {
                        if let Some(drop) = key.strip_prefix("drop").and_then(|n| n.parse::<usize>().ok())
                            && drop < self.sections[at].values.len()
                        {
                            self.sections[at].values.remove(drop);
                            popup.layers = vec![FormStep::entries_layer(&self.sections[at])];
                        }
                    }
                },
                SectionKind::Multi { .. } => {}
            },
            Outcome::Answer(answer) => {
                let (piece, chosen) = match answer {
                    Answer::Choice(item) => (item.key.clone(), Some(item.key)),
                    Answer::Text(text) => (text, None),
                    _ => return,
                };
                let layer = popup.layer();
                let next = chosen.and_then(|key| layer.then.iter().find(|(k, _)| *k == key).map(|(_, c)| c.clone()));
                popup.pieces.push((piece, layer.prefix));
                match next {
                    Some(chain) => popup.layers.push(Layer::of(&chain.spec, chain.then, chain.prefix)),
                    None => {
                        let mut value = String::new();
                        for (piece, _) in popup.pieces.iter().filter(|(_, prefix)| *prefix) {
                            value.push_str(piece);
                        }
                        for (piece, _) in popup.pieces.iter().filter(|(_, prefix)| !*prefix) {
                            value.push_str(piece);
                        }
                        popup.pieces.clear();
                        self.sections[at].values.push(value);
                        popup.layers = vec![FormStep::entries_layer(&self.sections[at])];
                    }
                }
            }
        }
    }

    fn handle(&mut self, key: Key, page: usize) -> Outcome {
        if self.popup.is_some() {
            self.handle_popup(key, page);
            return Outcome::Nothing;
        }
        let rows = self.sections.len() + 1;
        match key {
            Key::Up => self.cursor = self.cursor.saturating_sub(1),
            Key::Down => self.cursor = (self.cursor + 1).min(rows - 1),
            Key::PageUp | Key::Home => self.cursor = 0,
            Key::PageDown | Key::End => self.cursor = rows - 1,
            Key::Esc => return Outcome::Back,
            Key::Backspace | Key::Delete => {
                if let Some(section) = self.sections.get_mut(self.cursor) {
                    section.values.clear();
                }
            }
            Key::Enter => {
                if self.cursor < self.sections.len() {
                    self.open();
                } else {
                    return Outcome::Answer(Answer::Form(self.values()));
                }
            }
            _ => {}
        }
        Outcome::Nothing
    }
}

pub enum StepState {
    List(ListStep),
    Text(TextStep),
    Editor(EditorStep),
    Confirm(ConfirmStep),
    Form(FormStep),
}

impl StepState {
    pub fn from_spec(spec: &StepSpec) -> StepState {
        match spec {
            StepSpec::Select {
                items,
                initial,
                filterable,
                ..
            } => {
                let mut list = ListStep::new(items.clone(), *filterable);
                if let Some(key) = initial
                    && let Some(at) = items.iter().position(|i| i.key == *key)
                {
                    list.cursor = at;
                }
                StepState::List(list)
            }
            StepSpec::MultiSelect {
                items,
                initial,
                allow_empty,
                ..
            } => {
                let mut list = ListStep::new(items.clone(), true);
                list.multi = true;
                list.allow_empty = *allow_empty;
                list.picked = initial.iter().filter(|k| items.iter().any(|i| i.key == **k)).cloned().collect();
                StepState::List(list)
            }
            StepSpec::Text {
                initial,
                hint,
                check,
                allow_empty,
                ..
            } => StepState::Text(TextStep {
                value: initial.clone(),
                cursor: initial.chars().count(),
                hint: hint.clone(),
                check: *check,
                allow_empty: *allow_empty,
                error: None,
            }),
            StepSpec::Editor { initial, hint, .. } => StepState::Editor(EditorStep {
                text: initial.clone(),
                hint: hint.clone(),
                message: None,
            }),
            StepSpec::Confirm { text, .. } => StepState::Confirm(ConfirmStep { text: text.clone() }),
            StepSpec::Form { sections, submit, .. } => StepState::Form(FormStep {
                sections: sections.clone(),
                submit: submit.clone(),
                cursor: 0,
                popup: None,
            }),
        }
    }

    pub fn handle(&mut self, key: Key, page: usize) -> Outcome {
        match self {
            StepState::List(list) => list.handle(key, page),
            StepState::Text(text) => text.handle(key),
            StepState::Editor(editor) => editor.handle(key),
            StepState::Confirm(confirm) => confirm.handle(key),
            StepState::Form(form) => form.handle(key, page),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn select(labels: &[&str]) -> StepState {
        StepState::from_spec(&StepSpec::Select {
            title: "Pick".into(),
            items: labels.iter().map(|l| Item::new(l.to_lowercase(), *l)).collect(),
            initial: None,
            filterable: true,
        })
    }

    #[test]
    fn typing_filters_the_list_and_enter_picks_the_item_under_the_cursor() {
        let mut step = select(&["Backlog", "Done", "In progress"]);
        for c in "pro".chars() {
            step.handle(Key::Char(c), 10);
        }
        let StepState::List(list) = &step else { panic!() };
        assert_eq!(list.visible(), [2]);
        match step.handle(Key::Enter, 10) {
            Outcome::Answer(Answer::Choice(item)) => assert_eq!(item.label, "In progress"),
            _ => panic!("no answer"),
        }
        let mut step = select(&["Backlog", "Done"]);
        step.handle(Key::Down, 10);
        step.handle(Key::Down, 10);
        step.handle(Key::Char('z'), 10);
        assert!(matches!(step.handle(Key::Enter, 10), Outcome::Nothing));
        step.handle(Key::Backspace, 10);
        step.handle(Key::End, 10);
        match step.handle(Key::Enter, 10) {
            Outcome::Answer(Answer::Choice(item)) => assert_eq!(item.label, "Done"),
            _ => panic!("no answer"),
        }
    }

    #[test]
    fn a_multi_select_keeps_the_order_of_picking_and_may_require_one() {
        let mut step = StepState::from_spec(&StepSpec::MultiSelect {
            title: "Statuses".into(),
            items: vec![Item::new("a", "A"), Item::new("b", "B"), Item::new("c", "C")],
            initial: vec![],
            allow_empty: false,
        });
        assert!(matches!(step.handle(Key::Enter, 10), Outcome::Nothing));
        step.handle(Key::End, 10);
        step.handle(Key::Char(' '), 10);
        step.handle(Key::Home, 10);
        step.handle(Key::Char(' '), 10);
        match step.handle(Key::Enter, 10) {
            Outcome::Answer(Answer::Choices(items)) => {
                assert_eq!(items.iter().map(|i| i.key.as_str()).collect::<Vec<_>>(), ["c", "a"]);
            }
            _ => panic!("no answer"),
        }
    }

    #[test]
    fn text_input_edits_at_the_cursor_and_checks_the_value_on_enter() {
        let mut step = StepState::from_spec(&StepSpec::Text {
            title: "Color".into(),
            initial: "#80".into(),
            hint: String::new(),
            check: Check::Color,
            allow_empty: false,
        });
        assert!(matches!(step.handle(Key::Enter, 10), Outcome::Nothing));
        let StepState::Text(text) = &step else { panic!() };
        assert_eq!(text.error.as_deref(), Some("'#80' is not a color in #RRGGBB format"));
        for c in "8080".chars() {
            step.handle(Key::Char(c), 10);
        }
        step.handle(Key::Home, 10);
        step.handle(Key::Delete, 10);
        step.handle(Key::Char('#'), 10);
        step.handle(Key::Right, 10);
        step.handle(Key::Backspace, 10);
        step.handle(Key::Char('8'), 10);
        step.handle(Key::End, 10);
        match step.handle(Key::Enter, 10) {
            Outcome::Answer(Answer::Text(value)) => assert_eq!(value, "#808080"),
            _ => panic!("no answer"),
        }
    }

    #[test]
    fn checks() {
        assert_eq!(Check::Required.error("  ", false).as_deref(), Some("A value is required"));
        assert_eq!(Check::Required.error("  ", true), None);
        assert_eq!(Check::Int.error("12", false), None);
        assert_eq!(Check::Int.error("1.5", false).as_deref(), Some("'1.5' is not a whole number"));
        assert_eq!(Check::Float.error("1.5", false), None);
        assert_eq!(Check::Date.error("2026-10-28", false), None);
        assert_eq!(
            Check::Date.error("28.10.2026", false).as_deref(),
            Some("'28.10.2026' is not a date (yyyy-MM-dd)")
        );
        assert_eq!(Check::Prefix.error("TSK", false), None);
        assert!(Check::Prefix.error("T-K", false).is_some());
    }
}
