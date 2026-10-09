//! `SettingsStore`: чтение и изменение `settings.json`. Изменение — «прочитать, поменять, записать» под короткой блокировкой
//! `settings.lock`, поэтому одновременные правки из десктопа и консоли не теряются; запись атомарная, читатель не увидит половину файла.
use crate::io::{self, FileLock};
use crate::settings::app_directories::data_dir;
use crate::settings::global_settings::{GlobalSettings, MAX_USER_NAME_LENGTH, WorkspaceEntry};
use crate::settings::workspace_location::WorkspaceLocation;
use crate::validate::utf16_len;
use std::fmt;
use std::path::PathBuf;

/// Файл настроек нельзя прочитать или значение не годится (`SettingsException`); либо ошибка файловой системы.
#[derive(Debug)]
pub enum SettingsError {
    Settings(String),
    Io(std::io::Error),
}

impl fmt::Display for SettingsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Settings(m) => f.write_str(m),
            Self::Io(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for SettingsError {}

impl From<std::io::Error> for SettingsError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

pub struct SettingsStore {
    directory: PathBuf,
}

impl SettingsStore {
    /// `directory` — `None`: каталог данных приложения ([`data_dir`]).
    pub fn new(directory: Option<PathBuf>) -> SettingsStore {
        SettingsStore {
            directory: directory.unwrap_or_else(data_dir),
        }
    }

    pub fn file_path(&self) -> PathBuf {
        self.directory.join("settings.json")
    }

    fn lock_path(&self) -> PathBuf {
        self.directory.join("settings.lock")
    }

    /// Текущие настройки; файла нет — по умолчанию. Ошибка: файл есть, но в нём не JSON настроек.
    pub fn load(&self) -> Result<GlobalSettings, SettingsError> {
        let path = self.file_path();
        let text = match io::read_all_text(&path) {
            Ok(text) => text,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(GlobalSettings::default()),
            Err(e) => return Err(e.into()),
        };
        if text.trim().is_empty() {
            return Ok(GlobalSettings::default());
        }
        let unreadable =
            |reason: String| SettingsError::Settings(format!("Cannot read {}: {reason}. Fix or delete the file", path.display()));
        let value: serde_json::Value = serde_json::from_str(&text).map_err(|e| unreadable(e.to_string()))?;
        GlobalSettings::from_json(&value).map_err(unreadable)
    }

    /// Меняет настройки под блокировкой и возвращает записанные. Не изменилось — файл не трогаем.
    pub fn update(&self, change: impl FnOnce(GlobalSettings) -> GlobalSettings) -> Result<GlobalSettings, SettingsError> {
        FileLock::run(&self.lock_path(), None, || {
            let current = self.load()?;
            let before = serialize(&current);
            let updated = change(current);
            let text = serialize(&updated);
            if text == before && self.file_path().exists() {
                return Ok(updated);
            }
            io::write_all_text(&self.file_path(), &text)?;
            Ok(updated)
        })?
    }

    /// Добавляет рабочую область (та же папка, открытая иначе, — та же запись). Возвращает запись и признак «добавлена».
    pub fn add_workspace(&self, location: &WorkspaceLocation) -> Result<(WorkspaceEntry, bool), SettingsError> {
        let entry = WorkspaceEntry::of(location);
        let mut added = false;
        self.update(|mut settings| {
            if settings.mcp.workspaces.iter().any(|x| x.location().id() == location.id()) {
                return settings;
            }
            added = true;
            settings.mcp.workspaces.push(entry.clone());
            settings
        })?;
        Ok((entry, added))
    }

    /// `false` — такой области в настройках не было.
    pub fn remove_workspace(&self, location: &WorkspaceLocation) -> Result<bool, SettingsError> {
        let mut removed = false;
        self.update(|mut settings| {
            let before = settings.mcp.workspaces.len();
            settings.mcp.workspaces.retain(|x| x.location().id() != location.id());
            removed = settings.mcp.workspaces.len() != before;
            settings
        })?;
        Ok(removed)
    }

    pub fn set_port(&self, port: i32) -> Result<(), SettingsError> {
        if !(1..=65535).contains(&port) {
            return Err(SettingsError::Settings(format!("Port must be from 1 to 65535, got {port}")));
        }
        self.update(|mut settings| {
            settings.mcp.port = port;
            settings
        })?;
        Ok(())
    }

    /// Пустое имя или `None` — сбросить на имя пользователя операционной системы.
    pub fn set_user_name(&self, name: Option<&str>) -> Result<(), SettingsError> {
        let value = name.map(str::trim).filter(|s| !s.is_empty()).map(str::to_string);
        if let Some(v) = &value
            && utf16_len(v) > MAX_USER_NAME_LENGTH
        {
            return Err(SettingsError::Settings(format!(
                "The name must be at most {MAX_USER_NAME_LENGTH} characters, got {}",
                utf16_len(v)
            )));
        }
        self.update(|mut settings| {
            settings.user_name = value;
            settings
        })?;
        Ok(())
    }
}

/// `JsonSerializer.Serialize(settings, WriteIndented)` + `\n`.
fn serialize(settings: &GlobalSettings) -> String {
    let mut text = crate::json::to_string_pretty(&settings.to_json());
    text.push('\n');
    text
}

impl fmt::Debug for SettingsStore {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "SettingsStore({})", self.directory.display())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    struct Home(PathBuf);

    impl Home {
        fn new() -> Home {
            let dir = crate::test_support::temp_dir();
            std::fs::create_dir_all(&dir).unwrap();
            Home(dir)
        }

        fn store(&self) -> SettingsStore {
            SettingsStore::new(Some(self.0.join("home")))
        }

        fn folder(&self, name: &str) -> String {
            let dir = self.0.join(name);
            std::fs::create_dir_all(&dir).unwrap();
            dir.to_string_lossy().into_owned()
        }
    }

    impl Drop for Home {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn missing_file_gives_defaults_and_is_not_created_by_reading() {
        let home = Home::new();
        let settings = home.store().load().unwrap();
        assert_eq!(settings.mcp.port, 5719);
        assert!(settings.mcp.workspaces.is_empty());
        assert!(!home.store().file_path().exists());
    }

    #[test]
    fn settings_are_stored_as_readable_json() {
        let home = Home::new();
        let store = home.store();
        let location = WorkspaceLocation::files(&home.folder("a"));
        store.add_workspace(&location).unwrap();
        store.set_port(6000).unwrap();
        store.set_user_name(Some("  Иван  ")).unwrap();

        let text = std::fs::read_to_string(store.file_path()).unwrap();
        let expected = format!(
            "{{\n  \"userName\": \"Иван\",\n  \"mcp\": {{\n    \"port\": 6000,\n    \"workspaces\": [\n      {{\n        \"kind\": \"files\",\n        \"path\": \"{}\"\n      }}\n    ]\n  }}\n}}\n",
            location.path()
        );
        assert_eq!(text, expected);
        let loaded = store.load().unwrap();
        assert_eq!(loaded.mcp.port, 6000);
        assert_eq!(loaded.user_name.as_deref(), Some("Иван"));
        store.set_user_name(Some("   ")).unwrap();
        assert_eq!(store.load().unwrap().user_name, None);
    }

    #[cfg(unix)]
    #[test]
    fn same_folder_reached_another_way_is_one_workspace() {
        let home = Home::new();
        let store = home.store();
        let real = home.folder("real");
        let link = home.0.join("link");
        std::os::unix::fs::symlink(&real, &link).unwrap();
        std::fs::create_dir_all(std::path::Path::new(&real).join(".tasker")).unwrap();

        assert!(store.add_workspace(&WorkspaceLocation::files(&real)).unwrap().1);
        assert!(!store.add_workspace(&WorkspaceLocation::files(link.to_str().unwrap())).unwrap().1);
        assert!(
            !store
                .add_workspace(&WorkspaceLocation::files(&format!("{real}/.tasker")))
                .unwrap()
                .1
        );
        assert_eq!(store.load().unwrap().mcp.workspaces.len(), 1);
        assert!(store.remove_workspace(&WorkspaceLocation::files(link.to_str().unwrap())).unwrap());
        assert!(!store.remove_workspace(&WorkspaceLocation::files(link.to_str().unwrap())).unwrap());
        assert!(store.load().unwrap().mcp.workspaces.is_empty());
    }

    #[test]
    fn update_that_changes_nothing_does_not_rewrite_the_file() {
        let home = Home::new();
        let store = home.store();
        store.set_port(6000).unwrap();
        let before = std::fs::metadata(store.file_path()).unwrap().modified().unwrap();
        std::thread::sleep(std::time::Duration::from_millis(30));
        store.set_port(6000).unwrap();
        assert_eq!(before, std::fs::metadata(store.file_path()).unwrap().modified().unwrap());
    }

    #[test]
    fn port_and_name_are_validated() {
        let home = Home::new();
        for port in [0, 65536, -1] {
            let error = home.store().set_port(port).unwrap_err();
            assert_eq!(error.to_string(), format!("Port must be from 1 to 65535, got {port}"));
        }
        let long = "ё".repeat(101);
        assert_eq!(
            home.store().set_user_name(Some(&long)).unwrap_err().to_string(),
            "The name must be at most 100 characters, got 101"
        );
        assert!(!home.store().file_path().exists());
    }

    #[test]
    fn broken_file_is_reported_and_never_overwritten() {
        let home = Home::new();
        let store = home.store();
        std::fs::create_dir_all(store.file_path().parent().unwrap()).unwrap();
        std::fs::write(store.file_path(), "{ not json").unwrap();
        let error = store.load().unwrap_err().to_string();
        assert!(
            error.starts_with(&format!("Cannot read {}: ", store.file_path().display())),
            "{error}"
        );
        assert!(error.ends_with(". Fix or delete the file"), "{error}");
        assert!(matches!(store.set_port(6000), Err(SettingsError::Settings(_))));
        assert_eq!(std::fs::read_to_string(store.file_path()).unwrap(), "{ not json");
    }

    #[test]
    fn unknown_fields_and_empty_file_are_tolerated() {
        let home = Home::new();
        let store = home.store();
        std::fs::create_dir_all(store.file_path().parent().unwrap()).unwrap();
        std::fs::write(store.file_path(), r#"{"mcp":{"port":6001,"future":true},"other":1}"#).unwrap();
        assert_eq!(store.load().unwrap().mcp.port, 6001);
        std::fs::write(store.file_path(), "\u{FEFF}  \n").unwrap();
        assert_eq!(store.load().unwrap(), GlobalSettings::default());
    }

    #[test]
    fn parallel_updates_are_not_lost() {
        let home = Arc::new(Home::new());
        let handles: Vec<_> = (0..20)
            .map(|i| {
                let home = home.clone();
                std::thread::spawn(move || {
                    home.store()
                        .add_workspace(&WorkspaceLocation::files(&home.folder(&format!("w{i}"))))
                        .unwrap()
                })
            })
            .collect();
        for h in handles {
            h.join().unwrap();
        }
        assert_eq!(home.store().load().unwrap().mcp.workspaces.len(), 20);
    }
}
