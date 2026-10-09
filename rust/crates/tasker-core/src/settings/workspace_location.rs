//! `WorkspaceLocation`: где лежат данные рабочей области. Путь канонический ([`canonical_path`]): одна и та же папка, открытая
//! через симлинк или в другом регистре, — одна рабочая область.
use crate::settings::canonical_path::canonical_path;
use crate::validate::eq_ignore_case;
use sha2::{Digest as _, Sha256};
use std::fmt;
use std::path::Path;

/// Имя папки данных в рабочей папке (`TaskerDirectory.Name`).
pub const TASKER_DIRECTORY: &str = ".tasker";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum WorkspaceKind {
    /// Папка: данные — YAML-файлы в `<папка>/.tasker`.
    Files,
    /// Файл SQLite (только из аргументов запуска: `--sqlite=...`).
    Sqlite,
}

impl WorkspaceKind {
    /// Имя как в .NET `ToString()` (`Files`, `Sqlite`) — входит в [`WorkspaceLocation::id`].
    pub fn name(self) -> &'static str {
        match self {
            Self::Files => "Files",
            Self::Sqlite => "Sqlite",
        }
    }

    /// Имя в JSON настроек (camelCase).
    pub fn json_name(self) -> &'static str {
        match self {
            Self::Files => "files",
            Self::Sqlite => "sqlite",
        }
    }

    /// `JsonStringEnumConverter`: без учёта регистра; принимает и число.
    pub fn parse(text: &str) -> Option<Self> {
        if eq_ignore_case(text, "files") {
            Some(Self::Files)
        } else if eq_ignore_case(text, "sqlite") {
            Some(Self::Sqlite)
        } else {
            None
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct WorkspaceLocation {
    kind: WorkspaceKind,
    path: String,
    id: String,
}

impl WorkspaceLocation {
    /// Папка с данными в файлах. Выбрали саму `.tasker` — берём папку над ней: иначе появилась бы `.tasker/.tasker`.
    pub fn files(folder: &str) -> WorkspaceLocation {
        let mut path = canonical_path(folder);
        let p = Path::new(&path);
        if p.file_name()
            .is_some_and(|n| eq_ignore_case(&n.to_string_lossy(), TASKER_DIRECTORY))
            && let Some(parent) = p.parent()
        {
            path = parent.to_string_lossy().into_owned();
        }
        Self::new(WorkspaceKind::Files, path)
    }

    pub fn sqlite(file: &str) -> WorkspaceLocation {
        Self::new(WorkspaceKind::Sqlite, canonical_path(file))
    }

    fn new(kind: WorkspaceKind, path: String) -> WorkspaceLocation {
        let id = Self::create_id(kind, &path);
        WorkspaceLocation { kind, path, id }
    }

    pub fn kind(&self) -> WorkspaceKind {
        self.kind
    }

    /// Канонический путь: папка (`Files`) или файл БД (`Sqlite`).
    pub fn path(&self) -> &str {
        &self.path
    }

    /// Идентичность области — 12 hex SHA-256 от `{Kind}:{Path}`: одна папка открывается один раз, как бы её ни открыли.
    pub fn id(&self) -> &str {
        &self.id
    }

    /// Название для вкладки: имя папки или файла БД.
    pub fn name(&self) -> &str {
        match Path::new(&self.path).file_name() {
            Some(name) if !name.is_empty() => self.path.rsplit(['/', '\\']).next().unwrap_or(&self.path),
            _ => &self.path,
        }
    }

    fn create_id(kind: WorkspaceKind, path: &str) -> String {
        let digest = Sha256::digest(format!("{}:{}", kind.name(), path).as_bytes());
        digest[..6].iter().map(|b| format!("{b:02x}")).collect()
    }
}

impl fmt::Display for WorkspaceLocation {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} {}", self.kind.name(), self.path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn id_is_sha256_of_kind_and_path() {
        let id = WorkspaceLocation::create_id(WorkspaceKind::Files, "/x");
        // printf 'Files:/x' | shasum -a 256
        assert_eq!(id, "92295cfce50d");
        assert_ne!(id, WorkspaceLocation::create_id(WorkspaceKind::Sqlite, "/x"));
    }

    #[cfg(unix)]
    #[test]
    fn tasker_folder_means_its_parent() {
        let dir = crate::test_support::temp_dir();
        std::fs::create_dir_all(dir.join(".tasker")).unwrap();
        let root = WorkspaceLocation::files(dir.to_str().unwrap());
        let inner = WorkspaceLocation::files(dir.join(".tasker").to_str().unwrap());
        assert_eq!(root, inner);
        assert_eq!(root.name(), dir.file_name().unwrap().to_str().unwrap());
        assert_eq!(root.to_string(), format!("Files {}", root.path()));
        assert_eq!(WorkspaceLocation::files("/").name(), "/");
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
