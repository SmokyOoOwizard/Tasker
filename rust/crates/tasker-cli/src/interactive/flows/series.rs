use super::super::app::Action;
use super::super::step::Check;
use super::super::wizard::{Steps, Wizard, cmd};

pub fn run(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Series name", "", Check::Required)?;
            let prefix = w.text("Prefix (Latin letters and digits, e.g. TSK)", "", Check::Prefix)?;
            Ok(cmd!["series", "create", name, "--prefix", prefix])
        }
        Action::Change => {
            let series = w.select("Series", super::series(w, &project)?)?;
            let what = w.menu(
                "What to change",
                vec![
                    super::super::step::Item::new("name", "Name").detail(&series.detail),
                    super::super::step::Item::new("prefix", "Prefix").detail(&series.label),
                ],
            )?;
            let mut argv = cmd!["series", "update", series.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &series.detail, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                _ => {
                    let prefix = w.text(
                        "New prefix (references written elsewhere stop working)",
                        &series.label,
                        Check::Prefix,
                    )?;
                    argv.extend(cmd!["--prefix", prefix]);
                }
            }
            argv.extend(w.expected_version(&series));
            Ok(argv)
        }
        Action::Delete => {
            let series = w.select("Series", super::series(w, &project)?)?;
            w.confirm(
                "Delete series",
                &format!(
                    "Delete series '{}' ({})? Its tasks stay and lose their numbers in it. This cannot be undone.",
                    series.detail, series.label
                ),
            )?;
            let mut argv = cmd!["series", "delete", series.key];
            argv.extend(w.expected_version(&series));
            Ok(argv)
        }
    }
}
