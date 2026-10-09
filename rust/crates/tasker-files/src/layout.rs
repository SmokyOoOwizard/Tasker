//! Раскладка каталога `.tasker` внутри рабочей папки (`TaskerDirectory`, `ProjectDirectory`, `EntityFolders`, `WorkspaceLayout`
//! и `PathBudget` в .NET). У каждого проекта своя папка — сущности разных проектов физически не пересекаются. Внутри — по
//! файлу на сущность, чтобы изменения были отдельными диффами в git.
//!
//! ```text
//! <workspace>/.tasker/
//!   .gitignore            — исключает .cache и временные файлы записи
//!   .cache/               — локальный индекс и блокировки, в git не попадает
//!   users/<id>.yaml        — пользователи общие для всех проектов
//!   projects/<projectId>/
//!     project.yaml
//!     tasks/<заголовок>-<id8>.yaml   — задачи; как и у всех сущностей проекта ниже, в имени файла — название (у задачи заголовок)
//!                                      и первые 8 знаков id (см. `names`); старые имена — по полному Guid: <id>.yaml
//!     task-types/, statuses/, status-sets/, series/, link-types/, fields/, enums/, boards/ — <название>-<id8>.yaml
//! ```
use crate::error::{Error, Result};
use crate::names::{self, EXTENSION, ParsedName};
use std::io;
use std::path::{Path, PathBuf};
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::io::full_path;
use uuid::Uuid;

/// Вид файла раскладки (`IndexKind` в .NET).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum EntityKind {
    Project,
    User,
    Status,
    StatusSet,
    TaskType,
    Board,
    Task,
    Series,
    LinkType,
    Field,
    FieldEnum,
}

/// Что файл говорит о сущности: настоящий id и название (то, по чему называется файл).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EntityHead {
    pub id: Uuid,
    pub name: Option<String>,
}

/// Папка проекта, в которой лежат сущности одного вида, по файлу на сущность, с именем по названию ([`names`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EntityFolder {
    pub kind: EntityKind,
    /// Имя папки в проекте: `series`, `task-types`…
    pub name: &'static str,
    /// Имя файла, если в названии нет ни одной буквы или цифры.
    pub empty_slug: &'static str,
}

impl EntityFolder {
    /// Имя файла сущности с этим названием и id: `исправить-вход-3f2a9c1e.yaml`.
    pub fn file_name(&self, name: Option<&str>, id: &Uuid) -> String {
        names::file_name(name, id, self.empty_slug)
    }

    /// Совпадает ли имя файла с тем, каким оно должно быть у сущности с этим названием.
    pub fn is_current(&self, file_name: &str, name: Option<&str>, id: &Uuid) -> bool {
        names::is_current(file_name, name, id, self.empty_slug)
    }

    /// `EntityFolder.Peek`: читает файл (старый формат — в памяти) и возвращает id и название; `None` — файл пуст (YamlDotNet
    /// отдаёт null на пустой документ). Разбор идёт моделью своего вида, как в .NET, поэтому чужой или битый файл — ошибка.
    pub fn peek(&self, project_id: Uuid, bytes: &[u8], path: &Path) -> Result<Option<EntityHead>> {
        use crate::files;
        let head = |id: Uuid, name: &str| EntityHead {
            id,
            name: Some(name.to_string()),
        };
        let result = match self.kind {
            EntityKind::Task => files::task::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.title)),
            EntityKind::TaskType => files::task_type::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::Status => files::status::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::StatusSet => files::status_set::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::Board => files::board::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::Series => files::series::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::LinkType => files::link_type::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::Field => files::field::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::FieldEnum => files::field_enum::parse(project_id, bytes, path).map(|x| head(x.model.id, &x.model.name)),
            EntityKind::Project | EntityKind::User => unreachable!("the kind has no folder of its own in a project"),
        };
        match result {
            Ok(head) => Ok(Some(head)),
            // Пустой документ: YamlDotNet возвращает null вместо модели.
            Err(Error::Yaml(message)) if message.ends_with(": the file is empty") => Ok(None),
            Err(e) => Err(e),
        }
    }
}

pub const TASKS: EntityFolder = EntityFolder {
    kind: EntityKind::Task,
    name: "tasks",
    empty_slug: "task",
};
pub const TASK_TYPES: EntityFolder = EntityFolder {
    kind: EntityKind::TaskType,
    name: "task-types",
    empty_slug: "type",
};
pub const STATUSES: EntityFolder = EntityFolder {
    kind: EntityKind::Status,
    name: "statuses",
    empty_slug: "status",
};
pub const STATUS_SETS: EntityFolder = EntityFolder {
    kind: EntityKind::StatusSet,
    name: "status-sets",
    empty_slug: "set",
};
pub const BOARDS: EntityFolder = EntityFolder {
    kind: EntityKind::Board,
    name: "boards",
    empty_slug: "board",
};
pub const SERIES: EntityFolder = EntityFolder {
    kind: EntityKind::Series,
    name: "series",
    empty_slug: "series",
};
pub const LINK_TYPES: EntityFolder = EntityFolder {
    kind: EntityKind::LinkType,
    name: "link-types",
    empty_slug: "link",
};
pub const FIELDS: EntityFolder = EntityFolder {
    kind: EntityKind::Field,
    name: "fields",
    empty_slug: "field",
};
pub const ENUMS: EntityFolder = EntityFolder {
    kind: EntityKind::FieldEnum,
    name: "enums",
    empty_slug: "enum",
};

/// Все папки сущностей проекта (`EntityFolders.All`). Пользователи (`users/<id>.yaml`) и проекты (`projects/<id>/project.yaml`)
/// сюда не входят: у них файл называется по id.
pub const ALL_FOLDERS: [EntityFolder; 9] = [TASKS, TASK_TYPES, STATUSES, STATUS_SETS, BOARDS, SERIES, LINK_TYPES, FIELDS, ENUMS];

/// `EntityFolders.Find`: None — у этого вида нет папки сущностей в проекте (пользователь, проект).
pub fn folder_of(kind: EntityKind) -> Option<&'static EntityFolder> {
    ALL_FOLDERS.iter().find(|x| x.kind == kind)
}

/// `EntityFolders.Named`: None — это не папка сущностей.
pub fn folder_named(folder: &str) -> Option<&'static EntityFolder> {
    ALL_FOLDERS.iter().find(|x| x.name == folder)
}

/// Чем является файл по своему пути (`LayoutEntry`): вид сущности, её проект и то, что имя говорит об id. У пользователей имя —
/// полный Guid (`id`); у сущностей проекта — название и первые знаки id (`id_prefix`, настоящий id — внутри файла) или, в старом
/// формате, тоже полный Guid; `stem` — имя без расширения.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LayoutEntry {
    pub kind: EntityKind,
    pub project_id: Option<Uuid>,
    pub id: Option<Uuid>,
    pub stem: String,
    pub id_prefix: Option<String>,
}

/// `WorkspaceLayout.Classify`: индексируются только файлы раскладки. Всё остальное в `.tasker` — `.cache`, временные файлы
/// атомарной записи, `.gitignore`, чужие файлы — None. `relative_path` — относительно `.tasker`, через `/`.
pub fn classify(relative_path: &str) -> Option<LayoutEntry> {
    let parts: Vec<&str> = relative_path.split('/').collect();
    match parts.as_slice() {
        ["users", file] => {
            let id = parse_guid(file.strip_suffix(EXTENSION)?)?;
            Some(LayoutEntry {
                kind: EntityKind::User,
                project_id: None,
                id: Some(id),
                stem: names::stem(file).to_string(),
                id_prefix: None,
            })
        }
        ["projects", project, PROJECT_FILE_NAME] => {
            let project_id = parse_guid(project)?;
            Some(LayoutEntry {
                kind: EntityKind::Project,
                project_id: None,
                id: Some(project_id),
                stem: names::stem(PROJECT_FILE_NAME).to_string(),
                id_prefix: None,
            })
        }
        // Сущности проекта: имя — «название-первые знаки id» или, в старом формате, полный Guid.
        ["projects", project, folder, file] => {
            let project_id = parse_guid(project)?;
            let entity_folder = folder_named(folder)?;
            let parsed = names::try_parse(file)?;
            let (id, id_prefix) = match parsed {
                ParsedName::FullId(id) => (Some(id), None),
                ParsedName::IdPrefix(prefix) => (None, Some(prefix)),
            };
            Some(LayoutEntry {
                kind: entity_folder.kind,
                project_id: Some(project_id),
                id,
                stem: names::stem(file).to_string(),
                id_prefix,
            })
        }
        _ => None,
    }
}

pub const NAME: &str = ".tasker";
pub const CACHE_NAME: &str = ".cache";
pub const PROJECT_FILE_NAME: &str = "project.yaml";

const GITIGNORE_RULES: [&str; 2] = ["/.cache/", "*.tmp"];
const GITIGNORE_COMMENT: &str = "# Tasker: local index and locks (rebuilt, not for git) and temporary files of atomic writes";

/// Файл блокировки записи для каталога `.tasker` — единственное место, где задан его путь.
pub fn write_lock_of(tasker_root: &Path) -> PathBuf {
    tasker_root.join(CACHE_NAME).join("write.lock")
}

/// Каталог `.tasker` рабочей папки.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskerDirectory {
    root: PathBuf,
}

impl TaskerDirectory {
    /// `workspace_path` приводится к полному (`Path.GetFullPath`).
    pub fn new(workspace_path: impl AsRef<Path>) -> TaskerDirectory {
        TaskerDirectory {
            root: full_path(workspace_path).join(NAME),
        }
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn projects(&self) -> PathBuf {
        self.root.join("projects")
    }

    pub fn users(&self) -> PathBuf {
        self.root.join("users")
    }

    pub fn cache(&self) -> PathBuf {
        self.root.join(CACHE_NAME)
    }

    pub fn index_file(&self) -> PathBuf {
        self.cache().join("index.db")
    }

    /// Короткая блокировка обращений к индексу (`FileLock`).
    pub fn index_lock(&self) -> PathBuf {
        self.cache().join("index.lock")
    }

    /// Блокировка записи между процессами (`FileLock`): под ней сравнивают версию файла и пишут, выдают номера серий.
    pub fn write_lock(&self) -> PathBuf {
        write_lock_of(&self.root)
    }

    /// Блокировки на время правки: по файлу на сущность, в git не попадают.
    pub fn edit_locks(&self) -> PathBuf {
        self.cache().join("edit-locks")
    }

    /// Короткая блокировка обращений к [`edit_locks`](Self::edit_locks).
    pub fn edit_locks_lock(&self) -> PathBuf {
        self.cache().join("edit-locks.lock")
    }

    pub fn git_ignore(&self) -> PathBuf {
        self.root.join(".gitignore")
    }

    /// Метка «проект удалён здесь» (локально, в `.cache`): по ней запись сущности отличает проект, удалённый, пока запрос шёл, от
    /// проекта, которого просто ещё нет. Вернувшийся проект (есть project.yaml) метку перекрывает.
    pub fn deleted_project_marker(&self, project_id: &Uuid) -> PathBuf {
        self.cache().join("deleted-projects").join(guid_d(project_id))
    }

    /// Ставит метку удалённого проекта (как `ProjectStorage.Delete`: пустой файл).
    pub fn mark_project_deleted(&self, project_id: &Uuid) -> io::Result<()> {
        let marker = self.deleted_project_marker(project_id);
        if let Some(parent) = marker.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(marker, b"")
    }

    /// Можно ли писать в проект (`EntityFiles.Add`): есть `project.yaml` или нет метки удаления.
    pub fn project_accepts_writes(&self, project_id: &Uuid) -> bool {
        self.project(project_id).project_file().exists() || !self.deleted_project_marker(project_id).exists()
    }

    pub fn project(&self, project_id: &Uuid) -> ProjectDirectory {
        ProjectDirectory::new(self.projects().join(guid_d(project_id)))
    }

    pub fn user_file(&self, id: &Uuid) -> PathBuf {
        self.users().join(format!("{}{EXTENSION}", guid_d(id)))
    }

    /// Создаёт `projects/`, `users/` и дописывает служебные правила в `.gitignore`.
    pub fn ensure_created(&self) -> io::Result<()> {
        std::fs::create_dir_all(self.projects())?;
        std::fs::create_dir_all(self.users())?;
        self.ensure_service_files_ignored()
    }

    // Правила дописываются, если их нет: свои правила пользователя в .gitignore не трогаем.
    // Локальный индекс и блокировки лежат в .cache; *.tmp — временные файлы атомарной записи.
    fn ensure_service_files_ignored(&self) -> io::Result<()> {
        let path = self.git_ignore();
        let text = if path.exists() {
            // File.ReadAllText: UTF-8 с распознаванием BOM.
            let bytes = std::fs::read(&path)?;
            let text = String::from_utf8_lossy(&bytes).into_owned();
            text.strip_prefix('\u{FEFF}').map(str::to_string).unwrap_or(text)
        } else {
            String::new()
        };
        let lines: Vec<&str> = text.split('\n').map(str::trim).collect();
        let missing: Vec<&str> = GITIGNORE_RULES.iter().copied().filter(|rule| !lines.contains(rule)).collect();
        if missing.is_empty() {
            return Ok(());
        }
        let separator = if !text.is_empty() && !text.ends_with('\n') { "\n" } else { "" };
        let addition = format!("{separator}{GITIGNORE_COMMENT}\n{}\n", missing.join("\n"));
        use std::io::Write as _;
        let mut file = std::fs::OpenOptions::new().append(true).create(true).open(&path)?;
        file.write_all(addition.as_bytes())
    }
}

/// Папка одного проекта `projects/<id>/`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProjectDirectory {
    root: PathBuf,
}

impl ProjectDirectory {
    pub fn new(root: PathBuf) -> ProjectDirectory {
        ProjectDirectory { root }
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn project_file(&self) -> PathBuf {
        self.root.join(PROJECT_FILE_NAME)
    }

    pub fn folder(&self, folder: &EntityFolder) -> PathBuf {
        self.root.join(folder.name)
    }

    pub fn tasks(&self) -> PathBuf {
        self.folder(&TASKS)
    }

    pub fn task_types(&self) -> PathBuf {
        self.folder(&TASK_TYPES)
    }

    pub fn statuses(&self) -> PathBuf {
        self.folder(&STATUSES)
    }

    pub fn status_sets(&self) -> PathBuf {
        self.folder(&STATUS_SETS)
    }

    pub fn boards(&self) -> PathBuf {
        self.folder(&BOARDS)
    }

    pub fn series(&self) -> PathBuf {
        self.folder(&SERIES)
    }

    pub fn link_types(&self) -> PathBuf {
        self.folder(&LINK_TYPES)
    }

    pub fn fields(&self) -> PathBuf {
        self.folder(&FIELDS)
    }

    pub fn enums(&self) -> PathBuf {
        self.folder(&ENUMS)
    }

    /// Файл сущности под её названием: `исправить-вход-3f2a9c1e.yaml`.
    pub fn entity_file(&self, folder: &EntityFolder, id: &Uuid, name: Option<&str>) -> PathBuf {
        self.folder(folder).join(folder.file_name(name, id))
    }

    /// Старое имя файла сущности — по полному Guid (формат до версии 2 у задач, до версии 5 у остальных).
    pub fn legacy_file(&self, folder: &EntityFolder, id: &Uuid) -> PathBuf {
        self.folder(folder).join(format!("{}{EXTENSION}", guid_d(id)))
    }

    /// Существующие файлы сущности по id — под старым или новым именем. Ищет по имени (старое — Guid, новое — суффикс из id), не
    /// заглядывая в индекс, поэтому видит и файлы, которых индекс ещё не знает. В файл не заглядывает: совпадение id проверяет
    /// читающий (суффикс из 8 знаков может, хотя и крайне редко, совпасть).
    pub fn find_files(&self, folder: &EntityFolder, id: &Uuid) -> Vec<PathBuf> {
        let mut found = Vec::new();
        let legacy = self.legacy_file(folder, id);
        if legacy.exists() {
            found.push(legacy);
        }
        let Ok(entries) = std::fs::read_dir(self.folder(folder)) else {
            return found;
        };
        let suffix = format!("-{}{EXTENSION}", names::id_prefix(id));
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().into_owned();
            // Directory.EnumerateFiles("*-<id8>.yaml"): маска по имени (звёздочка может быть и пустой), только файлы.
            if name.ends_with(&suffix) && !path.is_dir() {
                found.push(path);
            }
        }
        found
    }

    /// Файл задачи с именем по заголовку.
    pub fn task_file(&self, id: &Uuid, title: Option<&str>) -> PathBuf {
        self.entity_file(&TASKS, id, title)
    }

    /// Существующие файлы задачи по id — под старым или новым именем.
    pub fn find_task_files(&self, id: &Uuid) -> Vec<PathBuf> {
        self.find_files(&TASKS, id)
    }

    /// Первый найденный файл задачи; None — нет.
    pub fn find_task_file(&self, id: &Uuid) -> Option<PathBuf> {
        self.find_task_files(id).into_iter().next()
    }
}

/// Длина путей файлов `.tasker` относительно предела Windows в 260 знаков (MAX_PATH) — `PathBudget` в .NET. Сам Tasker открывает
/// и длинные пути, а вот `git` без `core.longpaths`, проводник и многие программы — нет, поэтому рабочая папка, в которой самый
/// длинный файл не уместится в 260 знаков, на Windows создаёт проблемы вокруг Tasker. Чистые функции — проверяются на любой
/// платформе.
pub mod path_budget {
    use super::{ALL_FOLDERS, EXTENSION, NAME};
    use crate::names::{ID_LENGTH, MAX_SLUG_LENGTH};
    use tasker_core::validate::utf16_len;

    /// MAX_PATH Windows: 259 знаков и завершающий нуль.
    pub const WINDOWS_MAX_PATH: usize = 260;

    /// Самый длинный путь файла внутри рабочей папки (без неё самой):
    /// `.tasker\projects\{guid}\status-sets\{название}-{id8}.yaml.tmp` — с самой длинной папкой сущностей, названием предельной
    /// длины и временным файлом записи.
    pub fn longest_relative_path() -> usize {
        let longest_folder = ALL_FOLDERS.iter().map(|x| x.name.len()).max().unwrap_or(0);
        NAME.len()
            + 1
            + "projects".len()
            + 1
            + 36
            + 1
            + longest_folder
            + 1
            + MAX_SLUG_LENGTH
            + 1
            + ID_LENGTH
            + EXTENSION.len()
            + ".tmp".len()
    }

    /// Самая длинная рабочая папка, при которой все файлы ещё укладываются в MAX_PATH: путь файла = папка + `\` + относительный путь.
    pub fn max_workspace_root_length() -> usize {
        WINDOWS_MAX_PATH - 1 - 1 - longest_relative_path()
    }

    /// Не уместится ли в 260 знаков самый длинный возможный файл `.tasker` этой рабочей папки (путь — как есть, без обращения к диску).
    pub fn exceeds_legacy_limit(workspace_root: &str) -> bool {
        utf16_len(workspace_root.trim_end_matches(['\\', '/'])) > max_workspace_root_length()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::temp_dir;

    #[test]
    fn classify_recognises_layout_files_only() {
        let p = "11111111-1111-4111-8111-111111111111";
        let user = classify("users/00000080-0000-4000-8000-000000000080.yaml").unwrap();
        assert_eq!(user.kind, EntityKind::User);
        assert_eq!(user.id.map(|x| guid_d(&x)).as_deref(), Some("00000080-0000-4000-8000-000000000080"));
        assert_eq!(user.stem, "00000080-0000-4000-8000-000000000080");

        let project = classify(&format!("projects/{p}/project.yaml")).unwrap();
        assert_eq!((project.kind, project.project_id), (EntityKind::Project, None));
        assert_eq!(project.id.map(|x| guid_d(&x)).as_deref(), Some(p));
        assert_eq!(project.stem, "project");

        let task = classify(&format!("projects/{p}/tasks/fix-login-3f2a9c1e.yaml")).unwrap();
        assert_eq!(task.kind, EntityKind::Task);
        assert_eq!(task.project_id.map(|x| guid_d(&x)).as_deref(), Some(p));
        assert_eq!((task.id, task.id_prefix.as_deref()), (None, Some("3f2a9c1e")));
        assert_eq!(task.stem, "fix-login-3f2a9c1e");

        let legacy = classify(&format!("projects/{p}/statuses/00000075-0000-4000-8000-000000000075.yaml")).unwrap();
        assert_eq!(legacy.kind, EntityKind::Status);
        assert!(legacy.id.is_some() && legacy.id_prefix.is_none());

        for other in [
            ".gitignore",
            ".cache/index.db",
            ".cache/edit-locks/Task-x.json",
            &format!("projects/{p}/tasks/fix-login-3f2a9c1e.yaml.tmp"),
            &format!("projects/{p}/notes/a-00000001.yaml"),
            &format!("projects/{p}/tasks/notes.yaml"),
            &format!("projects/{p}/tasks/sub/a-00000001.yaml"),
            "projects/not-a-guid/project.yaml",
            "users/bob.yaml",
            &format!("projects/{p}/readme.yaml"),
        ] {
            assert_eq!(classify(other), None, "{other}");
        }
    }

    #[test]
    fn folders_have_the_dotnet_names_and_empty_slugs() {
        let names: Vec<(&str, &str)> = ALL_FOLDERS.iter().map(|x| (x.name, x.empty_slug)).collect();
        assert_eq!(
            names,
            vec![
                ("tasks", "task"),
                ("task-types", "type"),
                ("statuses", "status"),
                ("status-sets", "set"),
                ("boards", "board"),
                ("series", "series"),
                ("link-types", "link"),
                ("fields", "field"),
                ("enums", "enum"),
            ]
        );
        assert_eq!(folder_named("link-types").map(|x| x.kind), Some(EntityKind::LinkType));
        assert_eq!(folder_named("users"), None);
        assert_eq!(folder_of(EntityKind::Project), None);
        assert_eq!(folder_of(EntityKind::FieldEnum).map(|x| x.name), Some("enums"));
    }

    #[test]
    fn directory_paths_follow_the_layout() {
        let dir = TaskerDirectory::new("/ws");
        let root = full_path("/ws").join(".tasker");
        assert_eq!(dir.root(), root);
        assert_eq!(dir.write_lock(), root.join(".cache").join("write.lock"));
        assert_eq!(dir.index_lock(), root.join(".cache").join("index.lock"));
        assert_eq!(dir.index_file(), root.join(".cache").join("index.db"));
        assert_eq!(dir.edit_locks(), root.join(".cache").join("edit-locks"));
        assert_eq!(dir.edit_locks_lock(), root.join(".cache").join("edit-locks.lock"));
        assert_eq!(dir.git_ignore(), root.join(".gitignore"));
        let id = Uuid::parse_str("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f").unwrap();
        assert_eq!(
            dir.user_file(&id),
            root.join("users").join("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f.yaml")
        );
        assert_eq!(
            dir.deleted_project_marker(&id),
            root.join(".cache")
                .join("deleted-projects")
                .join("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f")
        );
        let project = dir.project(&id);
        assert_eq!(project.root(), root.join("projects").join("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f"));
        assert_eq!(project.project_file(), project.root().join("project.yaml"));
        assert_eq!(
            project.task_file(&id, Some("Fix login")),
            project.root().join("tasks").join("fix-login-3f2a9c1e.yaml")
        );
        assert_eq!(
            project.legacy_file(&STATUSES, &id),
            project.root().join("statuses").join("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f.yaml")
        );
        assert_eq!(project.status_sets(), project.root().join("status-sets"));
    }

    #[test]
    fn ensure_created_appends_the_gitignore_rules_once_and_keeps_user_rules() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        dir.ensure_created().unwrap();
        assert!(dir.projects().is_dir() && dir.users().is_dir());
        let expected = "# Tasker: local index and locks (rebuilt, not for git) and temporary files of atomic writes\n/.cache/\n*.tmp\n";
        assert_eq!(std::fs::read_to_string(dir.git_ignore()).unwrap(), expected);
        dir.ensure_created().unwrap();
        assert_eq!(std::fs::read_to_string(dir.git_ignore()).unwrap(), expected);

        std::fs::write(dir.git_ignore(), "*.log\n  /.cache/  ").unwrap();
        dir.ensure_created().unwrap();
        assert_eq!(
            std::fs::read_to_string(dir.git_ignore()).unwrap(),
            "*.log\n  /.cache/  \n# Tasker: local index and locks (rebuilt, not for git) and temporary files of atomic writes\n*.tmp\n"
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn find_files_sees_legacy_and_current_names() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let project_id = Uuid::new_v4();
        let id = Uuid::parse_str("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f").unwrap();
        let project = dir.project(&project_id);
        assert!(project.find_task_files(&id).is_empty());
        std::fs::create_dir_all(project.tasks()).unwrap();
        std::fs::write(project.legacy_file(&TASKS, &id), b"").unwrap();
        std::fs::write(project.task_file(&id, Some("Fix login")), b"").unwrap();
        std::fs::write(project.tasks().join("other-00000001.yaml"), b"").unwrap();
        std::fs::write(project.tasks().join("-3f2a9c1e.yaml"), b"").unwrap();
        let mut found: Vec<String> = project
            .find_task_files(&id)
            .iter()
            .map(|p| p.file_name().unwrap().to_string_lossy().into_owned())
            .collect();
        found.sort();
        assert_eq!(
            found,
            vec![
                "-3f2a9c1e.yaml",
                "3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f.yaml",
                "fix-login-3f2a9c1e.yaml"
            ]
        );
        assert_eq!(project.find_task_file(&id), Some(project.legacy_file(&TASKS, &id)));
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn path_budget_matches_dotnet_numbers() {
        // ".tasker/projects/<36>/status-sets/<60>-<8>.yaml.tmp"
        assert_eq!(
            path_budget::longest_relative_path(),
            7 + 1 + 8 + 1 + 36 + 1 + 11 + 1 + 60 + 1 + 8 + 5 + 4
        );
        assert_eq!(
            path_budget::max_workspace_root_length(),
            260 - 2 - path_budget::longest_relative_path()
        );
        let ok = "C:\\".to_string() + &"x".repeat(path_budget::max_workspace_root_length() - 3);
        assert!(!path_budget::exceeds_legacy_limit(&ok));
        assert!(!path_budget::exceeds_legacy_limit(&format!("{ok}\\")));
        assert!(path_budget::exceeds_legacy_limit(&format!("{ok}y")));
    }
}
