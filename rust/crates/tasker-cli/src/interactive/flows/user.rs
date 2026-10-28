use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use tasker_core::ids::guid_d;
use tasker_core::model::UserKind;

pub fn run(w: &mut Wizard<'_, '_>, action: Action, agent: bool) -> Steps<Vec<String>> {
    let group = if agent { "agent" } else { "user" };
    match action {
        Action::Create => {
            let name = w.text(&format!("{} name", if agent { "Agent" } else { "User" }), "", Check::Required)?;
            Ok(cmd![group, "create", name])
        }
        Action::Change => {
            let user = w.select(group, users(w, agent)?)?;
            let name = w.text("New name", &user.label, Check::Required)?;
            let mut argv = cmd![group, "update", user.key, "--name", name];
            argv.extend(w.expected_version(&user));
            Ok(argv)
        }
        Action::Delete => {
            let user = w.select(group, users(w, agent)?)?;
            super::confirm_delete(w, group, &user.label)?;
            let mut argv = cmd![group, "delete", user.key];
            argv.extend(w.expected_version(&user));
            Ok(argv)
        }
    }
}

fn users(w: &Wizard<'_, '_>, agent: bool) -> Steps<Vec<Item>> {
    let kind = if agent { UserKind::Agent } else { UserKind::Human };
    Ok(w.ctx
        .session
        .workspace()
        .users()
        .get_all(Some(kind))?
        .iter()
        .map(|u| Item::new(guid_d(&u.id), &u.username).version(&u.version))
        .collect())
}
