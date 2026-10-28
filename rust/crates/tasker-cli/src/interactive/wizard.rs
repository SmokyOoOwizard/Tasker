use super::step::{Answer, Check, Item, StepSpec};
use crate::errors::{CliError, Result};
use crate::session::Session;
use tasker_core::model::Project;
use uuid::Uuid;

pub struct FlowContext<'a> {
    pub session: &'a Session,
    pub project: Option<&'a Project>,
    pub fresh: bool,
}

impl FlowContext<'_> {
    pub fn project_id(&self) -> Result<Uuid> {
        match self.project {
            Some(project) => Ok(project.id),
            None => self.session.project_id(None),
        }
    }
}

pub enum Pending {
    Step(StepSpec),
    Failed(CliError),
}

impl From<CliError> for Pending {
    fn from(error: CliError) -> Pending {
        Pending::Failed(error)
    }
}

impl From<tasker_services::Error> for Pending {
    fn from(error: tasker_services::Error) -> Pending {
        Pending::Failed(error.into())
    }
}

pub type Steps<T> = std::result::Result<T, Pending>;

pub struct Wizard<'a, 'b> {
    pub ctx: &'a FlowContext<'b>,
    answers: &'a [Answer],
    at: usize,
}

impl<'a, 'b> Wizard<'a, 'b> {
    pub fn new(ctx: &'a FlowContext<'b>, answers: &'a [Answer]) -> Wizard<'a, 'b> {
        Wizard { ctx, answers, at: 0 }
    }

    fn take(&mut self, spec: StepSpec) -> Steps<&'a Answer> {
        match self.answers.get(self.at) {
            Some(answer) => {
                self.at += 1;
                Ok(answer)
            }
            None => Err(Pending::Step(spec)),
        }
    }

    pub fn select(&mut self, title: &str, items: Vec<Item>) -> Steps<Item> {
        if items.is_empty() {
            return Err(Pending::Failed(CliError::new(format!(
                "Nothing to choose from: {}",
                title.to_lowercase()
            ))));
        }
        match self.take(StepSpec::Select {
            title: title.to_string(),
            items,
            initial: None,
            filterable: true,
        })? {
            Answer::Choice(item) => Ok(item.clone()),
            _ => Err(Pending::Failed(CliError::new("Unexpected answer"))),
        }
    }

    pub fn menu(&mut self, title: &str, items: Vec<Item>) -> Steps<Item> {
        self.menu_with(title, items, true)
    }

    pub fn plain_menu(&mut self, title: &str, items: Vec<Item>) -> Steps<Item> {
        self.menu_with(title, items, false)
    }

    fn menu_with(&mut self, title: &str, items: Vec<Item>, filterable: bool) -> Steps<Item> {
        match self.take(StepSpec::Select {
            title: title.to_string(),
            items,
            initial: None,
            filterable,
        })? {
            Answer::Choice(item) => Ok(item.clone()),
            _ => Err(Pending::Failed(CliError::new("Unexpected answer"))),
        }
    }

    pub fn multi(&mut self, title: &str, items: Vec<Item>, allow_empty: bool) -> Steps<Vec<Item>> {
        if items.is_empty() && !allow_empty {
            return Err(Pending::Failed(CliError::new(format!(
                "Nothing to choose from: {}",
                title.to_lowercase()
            ))));
        }
        match self.take(StepSpec::MultiSelect {
            title: title.to_string(),
            items,
            initial: Vec::new(),
            allow_empty,
        })? {
            Answer::Choices(items) => Ok(items.clone()),
            _ => Err(Pending::Failed(CliError::new("Unexpected answer"))),
        }
    }

    pub fn text(&mut self, title: &str, initial: &str, check: Check) -> Steps<String> {
        self.text_with(title, initial, check, false, "")
    }

    pub fn optional_text(&mut self, title: &str, initial: &str, check: Check) -> Steps<String> {
        self.text_with(title, initial, check, true, "Enter with an empty value skips it")
    }

    pub fn text_with(&mut self, title: &str, initial: &str, check: Check, allow_empty: bool, hint: &str) -> Steps<String> {
        match self.take(StepSpec::Text {
            title: title.to_string(),
            initial: initial.to_string(),
            hint: hint.to_string(),
            check,
            allow_empty,
        })? {
            Answer::Text(text) => Ok(text.clone()),
            _ => Err(Pending::Failed(CliError::new("Unexpected answer"))),
        }
    }

    pub fn editor(&mut self, title: &str, initial: &str, hint: &str) -> Steps<String> {
        match self.take(StepSpec::Editor {
            title: title.to_string(),
            initial: initial.to_string(),
            hint: hint.to_string(),
        })? {
            Answer::Text(text) => Ok(text.clone()),
            _ => Err(Pending::Failed(CliError::new("Unexpected answer"))),
        }
    }

    pub fn confirm(&mut self, title: &str, text: &str) -> Steps<()> {
        self.take(StepSpec::Confirm {
            title: title.to_string(),
            text: text.to_string(),
        })?;
        Ok(())
    }

    pub fn yes_no(&mut self, title: &str) -> Steps<bool> {
        Ok(self.menu(title, vec![Item::new("yes", "Yes"), Item::new("no", "No")])?.key == "yes")
    }

    pub fn expected_version(&self, item: &Item) -> Vec<String> {
        match (&item.version, self.ctx.fresh) {
            (Some(version), false) => vec!["--expected-version".to_string(), version.clone()],
            _ => Vec::new(),
        }
    }

    pub fn project_id(&self) -> Steps<Uuid> {
        Ok(self.ctx.project_id()?)
    }
}

macro_rules! cmd {
    ($($x:expr),* $(,)?) => { vec![$($x.to_string()),*] };
}
pub(crate) use cmd;
