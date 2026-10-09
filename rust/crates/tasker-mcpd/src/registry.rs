//! `WorkspaceRegistry` (.NET `Tasker.Web.Workspaces`): открытые рабочие области процесса. Папка открывается один раз, как бы её ни
//! назвали; у области есть ключ — имя папки, у одновременно открытых одноимённых — с суффиксом (`Tasker-2`): это аргумент
//! `workspace` MCP и часть адреса `/w/{key}`, ключ не меняется между запусками. Открытая область: индекс со сверкой файлов
//! ([`Workspace::open`]) и наблюдатель `.tasker` ([`WorkspaceWatcher`]: `Changed → refresh`, `FullSync → sync`), как
//! `FileStorageModule(watchFiles: true)` + `WorkspaceWatcher` в .NET.
//!
//! Операции блокирующие (открытие сверяет индекс, закрытие ждёт поток наблюдателя): демон зовёт их через `spawn_blocking`.
use std::collections::HashMap;
use std::io;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use tasker_core::settings::{WorkspaceKind, WorkspaceLocation};
use tasker_core::validate::{is_letter_or_digit, to_upper_invariant};
use tasker_files::watch::{WatchEvent, WatcherHandle, WorkspaceWatcher};
use tasker_mcp::LocalAgent;
use tasker_services::Workspace;
use tracing::{info, warn};

/// Открытая область: ключ, сервисы домена, локальный агент MCP и наблюдатель файлов. Закрывается, когда отпущена последняя ссылка.
pub struct OpenWorkspace {
    location: WorkspaceLocation,
    key: String,
    workspace: Workspace,
    agent: Arc<LocalAgent>,
    _watcher: WatcherHandle,
}

impl OpenWorkspace {
    pub fn location(&self) -> &WorkspaceLocation {
        &self.location
    }

    /// Ключ области — имя папки (аргумент `workspace` MCP).
    pub fn key(&self) -> &str {
        &self.key
    }

    pub fn workspace(&self) -> &Workspace {
        &self.workspace
    }

    /// Локальный агент области: от его имени идут вызовы MCP без заголовка `X-Tasker-Agent` (id запоминается, пока область открыта).
    pub fn local_agent(&self) -> &Arc<LocalAgent> {
        &self.agent
    }
}

impl std::fmt::Debug for OpenWorkspace {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OpenWorkspace")
            .field("location", &self.location)
            .field("key", &self.key)
            .finish()
    }
}

impl Drop for OpenWorkspace {
    fn drop(&mut self) {
        info!("Workspace closed: {}", self.location);
    }
}

#[derive(Default)]
struct Inner {
    /// По id (хэш пути — одна папка открывается один раз).
    open: HashMap<String, Arc<OpenWorkspace>>,
    /// Ключи без учёта регистра (`OrdinalIgnoreCase`) → id.
    by_key: HashMap<String, String>,
}

/// Открытые области процесса.
#[derive(Default)]
pub struct WorkspaceRegistry {
    inner: Mutex<Inner>,
}

impl WorkspaceRegistry {
    pub fn new() -> WorkspaceRegistry {
        WorkspaceRegistry::default()
    }

    /// Открывает область или берёт уже открытую. Блокирующая: сверка индекса и запуск наблюдателя.
    pub fn open(&self, location: &WorkspaceLocation) -> io::Result<Arc<OpenWorkspace>> {
        if let Some(existing) = self.lock().open.get(location.id()) {
            return Ok(existing.clone());
        }
        let key = self.unique_key(location);
        let opened = Arc::new(Self::create(location, key)?);
        let mut inner = self.lock();
        // Пока открывали, ту же папку мог открыть другой вызов (в демоне открытие последовательное; это на всякий случай).
        if let Some(existing) = inner.open.get(location.id()) {
            return Ok(existing.clone());
        }
        inner.by_key.insert(key_of(&opened.key), location.id().to_string());
        inner.open.insert(location.id().to_string(), opened.clone());
        info!("Workspace opened: {} (name {}, address /w/{})", location, opened.key, opened.key);
        Ok(opened)
    }

    /// Убирает область из реестра; сама она закрывается, когда отпущена последняя ссылка (возвращаемая — одна из них).
    pub fn close(&self, id: &str) -> Option<Arc<OpenWorkspace>> {
        let mut inner = self.lock();
        let removed = inner.open.remove(id)?;
        inner.by_key.remove(&key_of(&removed.key));
        Some(removed)
    }

    /// Открытая область по имени (без учёта регистра) или по каноническому пути папки (файла SQLite) — так её называет агент MCP.
    /// Относительный путь не принимается: «имя» без разделителей — только ключ.
    pub fn find_by_name_or_path(&self, name_or_path: &str) -> Option<Arc<OpenWorkspace>> {
        let inner = self.lock();
        if let Some(id) = inner.by_key.get(&key_of(name_or_path)) {
            return inner.open.get(id).cloned();
        }
        if !std::path::Path::new(name_or_path).is_absolute() {
            return None;
        }
        inner
            .open
            .get(WorkspaceLocation::files(name_or_path).id())
            .or_else(|| inner.open.get(WorkspaceLocation::sqlite(name_or_path).id()))
            .cloned()
    }

    /// Открытые области с ключами (в порядке открытия не гарантируется).
    pub fn opened(&self) -> Vec<Arc<OpenWorkspace>> {
        self.lock().open.values().cloned().collect()
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Ключ — имя папки (или файла БД) в виде slug: буквы и цифры (по `char.IsLetterOrDigit`, то есть по UTF-16-знакам), `-`, `_`,
    /// `.`; остальное — `-`; `-` и `.` по краям срезаются; пусто — `workspace`. У одновременно открытых одноимённых — суффикс
    /// `-2`, `-3`, … (сравнение ключей без учёта регистра).
    fn unique_key(&self, location: &WorkspaceLocation) -> String {
        let name = slug(location.name());
        let inner = self.lock();
        let mut key = name.clone();
        let mut i = 2;
        while inner.by_key.contains_key(&key_of(&key)) {
            key = format!("{name}-{i}");
            i += 1;
        }
        key
    }

    fn create(location: &WorkspaceLocation, key: String) -> io::Result<OpenWorkspace> {
        if location.kind() == WorkspaceKind::Sqlite {
            return Err(io::Error::other(
                "SQLite workspaces are not supported by this build of tasker-mcpd: use the .NET build",
            ));
        }
        let workspace = Workspace::open(location.path())?;
        let index = workspace.index().clone();
        let root: PathBuf = workspace.directory().root().to_path_buf();
        let path = location.path().to_string();
        let watched = root.clone();
        let watcher = WorkspaceWatcher::start(&watched, move |event: WatchEvent| {
            let result = match event {
                WatchEvent::Changed(paths) => index.refresh(&paths.iter().map(|p| root.join(p)).collect::<Vec<_>>()),
                WatchEvent::FullSync => index.sync(),
            };
            if let Err(e) = result {
                warn!("Workspace {path}: cannot bring the index up to date with the files: {e}");
            }
        })
        .map_err(|e| io::Error::other(e.to_string()))?;
        Ok(OpenWorkspace {
            location: location.clone(),
            key,
            workspace,
            agent: Arc::new(LocalAgent::new()),
            _watcher: watcher,
        })
    }
}

/// Ключ без учёта регистра (`StringComparer.OrdinalIgnoreCase`: по знакам, через `ToUpperInvariant`).
fn key_of(key: &str) -> String {
    to_upper_invariant(key)
}

/// `WorkspaceRegistry.UniqueKey` без суффикса: slug имени.
pub fn slug(name: &str) -> String {
    let mut slug = String::new();
    // Как .NET: по UTF-16-знакам; суррогатная пара — два знака, оба не буквы, — две `-`.
    for unit in name.trim().encode_utf16() {
        let c = char::from_u32(u32::from(unit));
        match c {
            Some(c) if is_letter_or_digit(c) || matches!(c, '-' | '_' | '.') => slug.push(c),
            _ => slug.push('-'),
        }
    }
    let name = slug.trim_matches(['-', '.']);
    if name.is_empty() {
        "workspace".to_string()
    } else {
        name.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slug_keeps_letters_digits_and_a_few_signs() {
        assert_eq!(slug("Tasker"), "Tasker");
        assert_eq!(slug("  my project (2026)  "), "my-project--2026");
        assert_eq!(slug("Проект_1.2"), "Проект_1.2");
        assert_eq!(slug("..."), "workspace");
        assert_eq!(slug(""), "workspace");
        assert_eq!(slug("-.a.-"), "a");
        // Эмодзи — суррогатная пара: два знака UTF-16, оба заменяются.
        assert_eq!(slug("x😀y"), "x--y");
    }

    fn temp_dir(name: &str) -> PathBuf {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string())
            .join(name);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn same_names_get_suffixes_and_the_key_is_freed_on_close() {
        let registry = WorkspaceRegistry::new();
        let a = temp_dir("same");
        let b = temp_dir("same");
        let c = temp_dir("SAME");
        let first = registry.open(&WorkspaceLocation::files(a.to_str().unwrap())).unwrap();
        let second = registry.open(&WorkspaceLocation::files(b.to_str().unwrap())).unwrap();
        let third = registry.open(&WorkspaceLocation::files(c.to_str().unwrap())).unwrap();
        assert_eq!(first.key(), "same");
        assert_eq!(second.key(), "same-2");
        assert_eq!(third.key(), "SAME-3");
        assert!(Arc::ptr_eq(
            &registry.open(&WorkspaceLocation::files(a.to_str().unwrap())).unwrap(),
            &first
        ));
        assert_eq!(registry.find_by_name_or_path("SAME-2").unwrap().key(), "same-2");
        assert_eq!(registry.find_by_name_or_path(first.location().path()).unwrap().key(), "same");
        assert!(registry.find_by_name_or_path("same/x").is_none());
        assert!(registry.find_by_name_or_path("nothing").is_none());
        assert!(a.join(".tasker").is_dir());

        let id = second.location().id().to_string();
        drop(second);
        assert!(registry.close(&id).is_some());
        assert!(registry.close(&id).is_none());
        assert!(registry.find_by_name_or_path("same-2").is_none());
        let again = registry.open(&WorkspaceLocation::files(b.to_str().unwrap())).unwrap();
        assert_eq!(again.key(), "same-2");
        assert_eq!(registry.opened().len(), 3);
        for dir in [a, b, c] {
            let _ = std::fs::remove_dir_all(dir.parent().unwrap());
        }
    }

    #[test]
    fn sqlite_is_not_supported_by_this_build() {
        let registry = WorkspaceRegistry::new();
        let file = temp_dir("db").join("tasker.db");
        std::fs::write(&file, b"").unwrap();
        let error = registry.open(&WorkspaceLocation::sqlite(file.to_str().unwrap())).unwrap_err();
        assert_eq!(
            error.to_string(),
            "SQLite workspaces are not supported by this build of tasker-mcpd: use the .NET build"
        );
        let _ = std::fs::remove_dir_all(file.parent().unwrap().parent().unwrap());
    }
}
