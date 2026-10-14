//! Папки проектов (`ProjectFolders` в .NET): `projects/<slug имени>-<id8>/` — по названию проекта и первым 8 знакам его id, как файлы
//! сущностей ([`names`]); прежнее имя — полный Guid `projects/<guid>/` — читается наравне с новым. Настоящий id проекта — в
//! `project.yaml`, имя папки сверяется с ним по первым 8 знакам.
//!
//! У одного проекта может оказаться несколько папок (тот же id): ветка git переименовала проект, а в другой ветке в старую папку
//! добавили задачу. Чтение и индекс объединяют их: сущности всех папок — сущности проекта, `project.yaml` берётся из основной
//! папки ([`ProjectFolders::folders_of`]: папка с текущим именем, затем папка с `project.yaml`, затем по имени), остальные — вторые
//! папки: о них сообщают `tasker sync` и `list_workspace_problems` ([`second_folder_problems`]), а `tasker cleanup` и
//! `tasker migrate` сливают их файлы в основную ([`tidy`]).
use crate::files;
use crate::layout::{PROJECT_FILE_NAME, TaskerDirectory};
use crate::migration::{MigrationProblem, MigrationRename};
use crate::names::{self, ParsedName};
use crate::write::{self, IndexRefresh};
use regex::Regex;
use std::cell::{OnceCell, RefCell};
use std::cmp::Ordering;
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;
use tasker_core::ids::parse_guid;
use tasker_core::io::{self as atomic, FileLock};
use unicode_normalization::UnicodeNormalization as _;
use uuid::Uuid;

/// Имя папки, если в названии проекта нет ни одной буквы или цифры.
pub const EMPTY_SLUG: &str = "project";

/// Что имя папки говорит о проекте.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FolderName {
    /// Прежнее имя `<guid>`.
    Guid(Uuid),
    /// Имя `<slug>-<id8>`: первые 8 знаков id.
    Named(String),
}

impl FolderName {
    /// Первые 8 знаков id: у прежнего имени — из Guid.
    pub fn id8(&self) -> String {
        match self {
            FolderName::Guid(id) => names::id_prefix(id),
            FolderName::Named(prefix) => prefix.clone(),
        }
    }
}

/// None — имя не похоже на папку проекта.
pub fn parse_name(folder: &str) -> Option<FolderName> {
    match names::try_parse_stem(folder)? {
        ParsedName::FullId(id) => Some(FolderName::Guid(id)),
        ParsedName::IdPrefix(prefix) => Some(FolderName::Named(prefix)),
    }
}

/// Имя папки проекта: `мой-проект-3f2a9c1e`.
pub fn folder_name(name: &str, id: &Uuid) -> String {
    format!("{}-{}", names::slug(Some(name), EMPTY_SLUG), names::id_prefix(id))
}

/// Совпадает ли имя папки с тем, каким оно должно быть у проекта (без учёта формы Unicode: файловые системы её меняют).
pub fn is_current(folder: &str, name: &str, id: &Uuid) -> bool {
    folder.nfc().collect::<String>() == folder_name(name, id)
}

/// `StringComparer.Ordinal`: по единицам UTF-16.
pub(crate) fn ordinal(a: &str, b: &str) -> Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

/// Строка `id:` верхнего уровня — id проекта из файла, который целиком не читается (маркер конфликта git в имени): по нему
/// папка всё равно узнаётся, и сущности проекта не пропадают из списков.
fn id_line() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r#"(?m)^id:[ \t]*['"]?(?P<id>[0-9A-Fa-f-]{36})['"]?[ \t]*\r?$"#).expect("regex"))
}

/// Что говорит `project.yaml` папки.
#[derive(Debug, Clone, Default)]
struct Head {
    exists: bool,
    id: Option<Uuid>,
    /// Название — только у файла, который читается целиком.
    name: Option<String>,
}

/// Папки проектов одной области — снимок `projects/` на время одной операции: список папок и прочитанные `project.yaml`
/// запоминаются, поэтому создавать его нужно на одну операцию, а не держать.
pub struct ProjectFolders {
    projects: PathBuf,
    listing: OnceCell<Vec<(String, FolderName)>>,
    heads: RefCell<HashMap<String, Head>>,
}

impl ProjectFolders {
    pub fn new(directory: &TaskerDirectory) -> ProjectFolders {
        ProjectFolders {
            projects: directory.projects(),
            listing: OnceCell::new(),
            heads: RefCell::new(HashMap::new()),
        }
    }

    /// Папки `projects/`, похожие на папки проектов, по имени (Ordinal).
    pub fn listing(&self) -> &[(String, FolderName)] {
        self.listing.get_or_init(|| {
            let mut result = Vec::new();
            if let Ok(entries) = std::fs::read_dir(&self.projects) {
                for entry in entries.flatten() {
                    if !entry.file_type().is_ok_and(|t| t.is_dir()) {
                        continue;
                    }
                    let name = entry.file_name().to_string_lossy().into_owned();
                    if let Some(parsed) = parse_name(&name) {
                        result.push((name, parsed));
                    }
                }
            }
            result.sort_by(|a, b| ordinal(&a.0, &b.0));
            result
        })
    }

    fn head(&self, folder: &str) -> Head {
        if let Some(head) = self.heads.borrow().get(folder) {
            return head.clone();
        }
        let path = self.projects.join(folder).join(PROJECT_FILE_NAME);
        let head = match write::read_bytes(&path) {
            Ok(Some(bytes)) => match files::project::parse(&bytes, &path) {
                Ok(versioned) => Head {
                    exists: true,
                    id: Some(versioned.model.id),
                    name: Some(versioned.model.name),
                },
                Err(_) => {
                    let text = String::from_utf8_lossy(&bytes);
                    let id = id_line().captures(&text).and_then(|m| parse_guid(&m["id"]));
                    Head {
                        exists: true,
                        id,
                        name: None,
                    }
                }
            },
            Ok(None) => Head::default(),
            // Не прочитался (занят, нет прав): папку узнаём только по имени.
            Err(_) => Head {
                exists: true,
                ..Head::default()
            },
        };
        self.heads.borrow_mut().insert(folder.to_string(), head.clone());
        head
    }

    /// Id проекта, чьи файлы лежат в папке; None — не папка проекта или проект по ней не узнать. Прежнее имя — сам Guid. Новое —
    /// id из `project.yaml` папки, если он согласуется с именем; если `project.yaml` в папке нет (вторая папка после слияния), —
    /// проект с теми же первыми 8 знаками id из соседних папок.
    pub fn project_id(&self, folder: &str) -> Option<Uuid> {
        let id8 = match parse_name(folder)? {
            FolderName::Guid(id) => return Some(id),
            FolderName::Named(id8) => id8,
        };
        let head = self.head(folder);
        if head.exists {
            return head.id.filter(|id| names::id_prefix(id) == id8);
        }
        for (other, parsed) in self.listing() {
            if other == folder || parsed.id8() != id8 {
                continue;
            }
            match parsed {
                FolderName::Guid(id) => return Some(*id),
                FolderName::Named(_) => {
                    if let Some(id) = self.head(other).id.filter(|id| names::id_prefix(id) == id8) {
                        return Some(id);
                    }
                }
            }
        }
        None
    }

    /// Порядок папок одного проекта: 0 — папка с текущим именем, затем папки с `project.yaml` (новое имя раньше прежнего), затем
    /// без него.
    fn rank(&self, folder: &str, id: &Uuid) -> u8 {
        let head = self.head(folder);
        let guid = matches!(parse_name(folder), Some(FolderName::Guid(_)));
        let own = head.id == Some(*id);
        match (own, head.name.as_deref()) {
            (true, Some(name)) if is_current(folder, name, id) => 0,
            (true, _) if !guid => 1,
            (true, _) => 2,
            (false, _) if !guid => 3,
            (false, _) => 4,
        }
    }

    /// Все папки проекта, основная первой; пусто — папки нет.
    pub fn folders_of(&self, id: &Uuid) -> Vec<String> {
        let id8 = names::id_prefix(id);
        let mut found: Vec<String> = self
            .listing()
            .iter()
            .filter(|(folder, parsed)| match parsed {
                FolderName::Guid(guid) => guid == id,
                FolderName::Named(prefix) => *prefix == id8 && self.project_id(folder) == Some(*id),
            })
            .map(|(folder, _)| folder.clone())
            .collect();
        found.sort_by(|a, b| self.rank(a, id).cmp(&self.rank(b, id)).then_with(|| ordinal(a, b)));
        found
    }

    /// Основная папка проекта (её `project.yaml` — проект); None — папки нет.
    pub fn primary(&self, id: &Uuid) -> Option<String> {
        self.folders_of(id).into_iter().next()
    }

    /// Папки по проектам: id и папки (основная первой), в порядке имён основных папок; папки, проект которых не узнать, — во
    /// втором списке.
    pub fn groups(&self) -> (Vec<(Uuid, Vec<String>)>, Vec<String>) {
        let mut ids: Vec<Uuid> = Vec::new();
        let mut unknown = Vec::new();
        for (folder, _) in self.listing() {
            match self.project_id(folder) {
                Some(id) if !ids.contains(&id) => ids.push(id),
                Some(_) => {}
                None => unknown.push(folder.clone()),
            }
        }
        let mut groups: Vec<(Uuid, Vec<String>)> = ids.into_iter().map(|id| (id, self.folders_of(&id))).collect();
        groups.sort_by(|a, b| ordinal(&a.1[0], &b.1[0]));
        (groups, unknown)
    }

    /// Название проекта из `project.yaml` папки; None — файла нет или он не читается.
    pub fn name_in(&self, folder: &str) -> Option<String> {
        self.head(folder).name
    }

    /// Есть ли `project.yaml` в папке.
    pub fn has_project_file(&self, folder: &str) -> bool {
        self.head(folder).exists
    }
}

/// Текст проблемы второй папки проекта.
pub fn second_folder_reason(primary: &str) -> String {
    format!(
        "Second folder of the project in projects/{primary}: its files are listed with the project; run 'tasker cleanup' to merge the folders"
    )
}

/// Вторые папки проектов области как проблемы (`projects/<папка>`), по пути.
pub fn second_folder_problems(directory: &TaskerDirectory) -> Vec<(String, String)> {
    let folders = ProjectFolders::new(directory);
    let mut result = Vec::new();
    for (_, group) in folders.groups().0 {
        for second in &group[1..] {
            result.push((format!("projects/{second}"), second_folder_reason(&group[0])));
        }
    }
    result.sort_by(|a, b| a.0.cmp(&b.0));
    result
}

/// Что делать с папками проектов: `rename` — ещё и переименовать папку проекта с одной папкой, если её имя не текущее
/// (`tasker migrate`); без него — только слить вторые папки (`tasker cleanup`). `dry_run` — только рассказать. `project` — только
/// папки этого проекта.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct TidyOptions {
    pub dry_run: bool,
    pub rename: bool,
    pub project: Option<Uuid>,
}

/// Что сделано с папками проектов: переименования и слияния (`projects/<прежняя>` → `projects/<новая>`) и то, что осталось как было.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FolderReport {
    pub renamed: Vec<MigrationRename>,
    pub merged: Vec<MigrationRename>,
    pub problems: Vec<MigrationProblem>,
}

impl FolderReport {
    pub fn is_empty(&self) -> bool {
        self.renamed.is_empty() && self.merged.is_empty() && self.problems.is_empty()
    }
}

pub const NO_PROJECT_FILE_REASON: &str = "No project.yaml: cannot tell which project the folder belongs to";
pub const MISMATCH_REASON: &str = "The id in project.yaml does not match the folder name: not touched";

/// Приводит папки проектов к текущим именам и сливает вторые папки в основную — под блокировкой записи. Основная папка
/// переименовывается в `<slug имени>-<id8>` (имя занято — в отчёт). Файлы второй папки переносятся в основную; файл, который там
/// уже есть с тем же содержимым, удаляется, с другим — остаётся на месте и попадает в отчёт: ничего не теряется. Опустевшая
/// вторая папка удаляется. Индекс обновляется после.
pub fn tidy(directory: &TaskerDirectory, index: &dyn IndexRefresh, options: TidyOptions) -> io::Result<FolderReport> {
    if !directory.projects().is_dir() {
        return Ok(FolderReport::default());
    }
    let mut changed = Vec::new();
    let run = |changed: &mut Vec<PathBuf>| tidy_unlocked(directory, options, changed);
    let report = if options.dry_run {
        run(&mut changed)?
    } else {
        FileLock::run(&directory.write_lock(), None, || run(&mut changed))??
    };
    if !changed.is_empty() {
        index.refresh(&changed)?;
    }
    Ok(report)
}

fn tidy_unlocked(directory: &TaskerDirectory, options: TidyOptions, changed: &mut Vec<PathBuf>) -> io::Result<FolderReport> {
    let folders = ProjectFolders::new(directory);
    let projects = directory.projects();
    let mut report = FolderReport::default();
    let (groups, unknown) = folders.groups();

    if options.rename && options.project.is_none() {
        for folder in unknown {
            let reason = if folders.has_project_file(&folder) {
                MISMATCH_REASON
            } else {
                NO_PROJECT_FILE_REASON
            };
            report.problems.push(MigrationProblem {
                path: format!("projects/{folder}"),
                reason: reason.to_string(),
            });
        }
    }

    for (id, group) in groups {
        if (group.len() < 2 && !options.rename) || options.project.is_some_and(|only| only != id) {
            continue;
        }
        let primary = &group[0];
        let mut target = primary.clone();
        if let Some(name) = folders.name_in(primary).filter(|_| folders.project_id(primary) == Some(id))
            && !is_current(primary, &name, &id)
        {
            let current = folder_name(&name, &id);
            if group.contains(&current) {
                // Папка с текущим именем уже есть (без project.yaml): сливаем всё в неё.
                target = current;
            } else if projects.join(&current).exists() {
                report.problems.push(MigrationProblem {
                    path: format!("projects/{primary}"),
                    reason: format!("Cannot rename to '{current}': a folder with that name exists"),
                });
            } else {
                report.renamed.push(MigrationRename {
                    from: format!("projects/{primary}"),
                    to: format!("projects/{current}"),
                });
                if !options.dry_run {
                    atomic::move_file(&projects.join(primary), &projects.join(&current), false)?;
                    changed.push(projects.join(primary));
                    changed.push(projects.join(&current));
                }
                target = current;
            }
        }
        // Переименованная основная папка — это и есть `target`.
        let moved_primary = target != *primary && !group.contains(&target);
        // Где файлы основной папки сейчас: без записи переименование только рассказано.
        let target_dir = if moved_primary && options.dry_run {
            projects.join(primary)
        } else {
            projects.join(&target)
        };
        for second in &group {
            if *second == target || (moved_primary && second == primary) {
                continue;
            }
            merge(&projects, second, &target, &target_dir, options.dry_run, &mut report, changed)?;
        }
    }
    Ok(report)
}

/// Все файлы папки (в подпапках тоже) — пути относительно неё, по Ordinal.
fn files_under(root: &Path) -> Vec<PathBuf> {
    fn walk(dir: &Path, root: &Path, out: &mut Vec<PathBuf>) {
        let Ok(entries) = std::fs::read_dir(dir) else {
            return;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            match entry.file_type() {
                Ok(t) if t.is_dir() => walk(&path, root, out),
                Ok(_) => {
                    if let Ok(relative) = path.strip_prefix(root) {
                        out.push(relative.to_path_buf());
                    }
                }
                Err(_) => {}
            }
        }
    }
    let mut out = Vec::new();
    walk(root, root, &mut out);
    out.sort_by(|a, b| ordinal(&a.to_string_lossy(), &b.to_string_lossy()));
    out
}

fn slashed(path: &Path) -> String {
    path.components()
        .map(|c| c.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

/// Сливает вторую папку `second` в `target` (её файлы сейчас — в `to`).
fn merge(
    projects: &Path,
    second: &str,
    target: &str,
    to: &Path,
    dry_run: bool,
    report: &mut FolderReport,
    changed: &mut Vec<PathBuf>,
) -> io::Result<()> {
    let from = projects.join(second);
    report.merged.push(MigrationRename {
        from: format!("projects/{second}"),
        to: format!("projects/{target}"),
    });
    for relative in files_under(&from) {
        let source = from.join(&relative);
        let destination = to.join(&relative);
        if destination.exists() {
            let same = std::fs::read(&source)
                .ok()
                .is_some_and(|a| std::fs::read(&destination).is_ok_and(|b| a == b));
            if !same {
                report.problems.push(MigrationProblem {
                    path: format!("projects/{second}/{}", slashed(&relative)),
                    reason: format!(
                        "Not merged: projects/{target}/{} exists with other content; merge the two files by hand",
                        slashed(&relative)
                    ),
                });
                continue;
            }
            if !dry_run {
                atomic::delete(&source)?;
            }
        } else if !dry_run {
            if let Some(parent) = destination.parent() {
                std::fs::create_dir_all(parent)?;
            }
            atomic::move_file(&source, &destination, false)?;
        }
    }
    if !dry_run {
        remove_empty_dirs(&from);
        changed.push(from);
        changed.push(to.to_path_buf());
    }
    Ok(())
}

/// Удаляет пустые папки снизу вверх, включая саму `dir`, если она опустела.
fn remove_empty_dirs(dir: &Path) {
    if let Ok(entries) = std::fs::read_dir(dir) {
        for entry in entries.flatten() {
            if entry.file_type().is_ok_and(|t| t.is_dir()) {
                remove_empty_dirs(&entry.path());
            }
        }
    }
    let _ = std::fs::remove_dir(dir);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::temp_dir;
    use crate::write::NoIndex;
    use tasker_core::Timestamp;
    use tasker_core::model::Project;

    fn project(id: Uuid, name: &str) -> Vec<u8> {
        files::project::serialize(&Project {
            id,
            name: name.into(),
            created_at: Timestamp::parse("2026-01-01T00:00:00.0000000+00:00").unwrap(),
            version: String::new(),
        })
    }

    fn put(path: PathBuf, bytes: &[u8]) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, bytes).unwrap();
    }

    const ID: &str = "3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f";

    #[test]
    fn folder_names_follow_the_entity_file_rules() {
        let id = Uuid::parse_str(ID).unwrap();
        assert_eq!(folder_name("Мой проект: v2", &id), "мой-проект-v2-3f2a9c1e");
        assert_eq!(folder_name("!!!", &id), "project-3f2a9c1e");
        assert!(is_current("мой-проект-v2-3f2a9c1e", "Мой проект: v2", &id));
        assert!(!is_current(ID, "Мой проект: v2", &id));
        assert_eq!(parse_name(ID), Some(FolderName::Guid(id)));
        assert_eq!(parse_name("golden-11111111"), Some(FolderName::Named("11111111".into())));
        assert_eq!(parse_name("golden"), None);
        assert_eq!(parse_name("golden-1111111G"), None);
        assert_eq!(FolderName::Guid(id).id8(), "3f2a9c1e");
    }

    #[test]
    fn a_folder_is_recognised_by_its_project_file_or_a_sibling() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let id = Uuid::parse_str(ID).unwrap();
        let projects = dir.projects();
        put(projects.join("new-name-3f2a9c1e").join("project.yaml"), &project(id, "New name"));
        // Вторая папка после слияния: только задача, project.yaml нет.
        put(projects.join("old-name-3f2a9c1e").join("tasks").join("a-00000001.yaml"), b"x");
        // Прежнее имя без project.yaml.
        put(projects.join(ID).join("tasks").join("b-00000002.yaml"), b"x");
        // Чужой id в project.yaml: папка не узнаётся.
        put(projects.join("wrong-00000000").join("project.yaml"), &project(id, "Wrong"));
        // Не папка проекта.
        std::fs::create_dir_all(projects.join("notes")).unwrap();

        let folders = ProjectFolders::new(&dir);
        assert_eq!(folders.project_id("new-name-3f2a9c1e"), Some(id));
        assert_eq!(folders.project_id("old-name-3f2a9c1e"), Some(id));
        assert_eq!(folders.project_id(ID), Some(id));
        assert_eq!(folders.project_id("wrong-00000000"), None);
        assert_eq!(folders.project_id("notes"), None);
        assert_eq!(folders.folders_of(&id), vec!["new-name-3f2a9c1e", "old-name-3f2a9c1e", ID]);
        let (groups, unknown) = folders.groups();
        assert_eq!(
            groups,
            vec![(id, vec!["new-name-3f2a9c1e".to_string(), "old-name-3f2a9c1e".into(), ID.into()])]
        );
        assert_eq!(unknown, vec!["wrong-00000000".to_string()]);
        assert_eq!(
            second_folder_problems(&dir),
            vec![
                (format!("projects/{ID}"), second_folder_reason("new-name-3f2a9c1e")),
                ("projects/old-name-3f2a9c1e".to_string(), second_folder_reason("new-name-3f2a9c1e")),
            ]
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn a_conflicted_project_file_still_names_its_project() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let id = Uuid::parse_str(ID).unwrap();
        let text = format!("formatVersion: 10\nid: {ID}\n<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> x\n");
        put(dir.projects().join("a-3f2a9c1e").join("project.yaml"), text.as_bytes());
        let folders = ProjectFolders::new(&dir);
        assert_eq!(folders.project_id("a-3f2a9c1e"), Some(id));
        assert_eq!(folders.name_in("a-3f2a9c1e"), None);
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn tidy_renames_and_merges_without_losing_files() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let id = Uuid::parse_str(ID).unwrap();
        let projects = dir.projects();
        put(projects.join(ID).join("project.yaml"), &project(id, "Renamed"));
        put(projects.join(ID).join("tasks").join("same-00000001.yaml"), b"same");
        put(projects.join(ID).join("tasks").join("clash-00000002.yaml"), b"ours");
        put(projects.join("old-3f2a9c1e").join("tasks").join("same-00000001.yaml"), b"same");
        put(projects.join("old-3f2a9c1e").join("tasks").join("clash-00000002.yaml"), b"theirs");
        put(projects.join("old-3f2a9c1e").join("tasks").join("new-00000003.yaml"), b"new");

        // Без rename (cleanup) — то же: у проекта две папки, сливаются в папку с текущим именем.
        let dry = tidy(
            &dir,
            &NoIndex,
            TidyOptions {
                dry_run: true,
                rename: false,
                project: None,
            },
        )
        .unwrap();
        assert_eq!(
            dry.renamed,
            vec![MigrationRename {
                from: format!("projects/{ID}"),
                to: "projects/renamed-3f2a9c1e".into()
            }]
        );
        assert_eq!(
            dry.merged,
            vec![MigrationRename {
                from: "projects/old-3f2a9c1e".into(),
                to: "projects/renamed-3f2a9c1e".into()
            }]
        );
        assert_eq!(dry.problems.len(), 1);
        assert_eq!(dry.problems[0].path, "projects/old-3f2a9c1e/tasks/clash-00000002.yaml");
        assert!(projects.join(ID).is_dir(), "dry run changes nothing");

        let done = tidy(
            &dir,
            &NoIndex,
            TidyOptions {
                dry_run: false,
                rename: false,
                project: None,
            },
        )
        .unwrap();
        assert_eq!(done, dry);
        let tasks = projects.join("renamed-3f2a9c1e").join("tasks");
        assert_eq!(std::fs::read(tasks.join("same-00000001.yaml")).unwrap(), b"same");
        assert_eq!(std::fs::read(tasks.join("clash-00000002.yaml")).unwrap(), b"ours");
        assert_eq!(std::fs::read(tasks.join("new-00000003.yaml")).unwrap(), b"new");
        assert_eq!(
            std::fs::read(projects.join("old-3f2a9c1e").join("tasks").join("clash-00000002.yaml")).unwrap(),
            b"theirs"
        );
        assert!(!projects.join(ID).exists());
        assert!(!projects.join("old-3f2a9c1e").join("tasks").join("new-00000003.yaml").exists());

        // Конфликт разрешили руками: вторая папка уходит целиком.
        std::fs::remove_file(projects.join("old-3f2a9c1e").join("tasks").join("clash-00000002.yaml")).unwrap();
        let again = tidy(
            &dir,
            &NoIndex,
            TidyOptions {
                dry_run: false,
                rename: false,
                project: None,
            },
        )
        .unwrap();
        assert_eq!(again.merged.len(), 1);
        assert!(again.problems.is_empty());
        assert!(!projects.join("old-3f2a9c1e").exists());
        assert!(
            tidy(
                &dir,
                &NoIndex,
                TidyOptions {
                    dry_run: false,
                    rename: true,
                    project: None
                }
            )
            .unwrap()
            .is_empty()
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn tidy_with_rename_renames_single_folders_and_reports_unknown_ones() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let id = Uuid::parse_str(ID).unwrap();
        let projects = dir.projects();
        put(projects.join(ID).join("project.yaml"), &project(id, "Legacy"));
        put(projects.join("lost-00000001").join("tasks").join("a-00000001.yaml"), b"x");
        assert!(
            tidy(
                &dir,
                &NoIndex,
                TidyOptions {
                    dry_run: false,
                    rename: false,
                    project: None
                }
            )
            .unwrap()
            .is_empty()
        );
        let report = tidy(
            &dir,
            &NoIndex,
            TidyOptions {
                dry_run: false,
                rename: true,
                project: None,
            },
        )
        .unwrap();
        assert_eq!(report.renamed.len(), 1);
        assert_eq!(
            report.problems,
            vec![MigrationProblem {
                path: "projects/lost-00000001".into(),
                reason: NO_PROJECT_FILE_REASON.into()
            }]
        );
        assert!(projects.join("legacy-3f2a9c1e").join("project.yaml").is_file());
        std::fs::remove_dir_all(&ws).unwrap();
    }
}
