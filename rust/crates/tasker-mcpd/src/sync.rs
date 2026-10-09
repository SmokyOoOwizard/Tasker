//! `WorkspaceSync` (.NET `Tasker.Daemon.Host`): держит открытыми ровно те рабочие области, что перечислены в настройках. Добавили
//! область командой или из десктопа — демон открывает её, не перезапускаясь; убрали — закрывает. Область, которую открыть не удалось
//! (папку удалили, диск не подключён), не мешает остальным и пробуется снова при следующем изменении настроек и по таймеру
//! ([`RETRY_EVERY`], 30 с). Статусы областей (`opening/open/failed`) публикуются в [`DaemonState`] на каждом шаге.
//!
//! Домен синхронный: открытие, закрытие и сверка идут в `spawn_blocking`; список областей сериализован асинхронной блокировкой.
use crate::registry::{OpenWorkspace, WorkspaceRegistry};
use crate::state::{DaemonState, PROBLEMS_SHOWN, SyncResult, WorkspaceState, WorkspaceStatus, mcp_url};
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tasker_core::ids::guid_d;
use tasker_core::settings::{WorkspaceEntry, WorkspaceKind, WorkspaceLocation};
use tasker_core::tasks::Page;
use tasker_services::health::{self, PREFIX_HINT, ProjectSeriesHealth};
use tracing::{error, info, warn};

/// Как часто пробуются области, которые не открылись (`McpDaemon.RetryEvery`).
pub const RETRY_EVERY: Duration = Duration::from_secs(30);

/// Тестовый крючок: искусственно медленное открытие области (мс).
pub const OPEN_DELAY_VARIABLE: &str = "TASKER_MCP_OPEN_DELAY_MS";

#[derive(Default)]
struct Inner {
    desired: Vec<WorkspaceEntry>,
    open: HashMap<String, (Arc<OpenWorkspace>, WorkspaceEntry)>,
    failed: HashMap<String, String>,
    disposed: bool,
}

pub struct WorkspaceSync {
    registry: Arc<WorkspaceRegistry>,
    state: Arc<DaemonState>,
    port: i32,
    /// Сериализует `apply`/`retry`/`dispose`: список областей меняется по одному.
    gate: tokio::sync::Mutex<()>,
    inner: Mutex<Inner>,
    /// Последнее сообщённое состояние серий по областям: в лог пишем, только когда оно изменилось.
    reported: Mutex<HashMap<String, String>>,
}

/// Желаемая область с её адресом; одна папка в настройках дважды — одна область.
struct Wanted {
    entry: WorkspaceEntry,
    location: WorkspaceLocation,
}

impl WorkspaceSync {
    pub fn new(registry: Arc<WorkspaceRegistry>, state: Arc<DaemonState>, port: i32) -> WorkspaceSync {
        WorkspaceSync {
            registry,
            state,
            port,
            gate: tokio::sync::Mutex::new(()),
            inner: Mutex::new(Inner::default()),
            reported: Mutex::new(HashMap::new()),
        }
    }

    pub fn registry(&self) -> &Arc<WorkspaceRegistry> {
        &self.registry
    }

    /// Сразу помечает области из настроек как открывающиеся — до того, как хост начнёт отвечать: иначе статус на миг показал бы
    /// «областей нет», и запуск счёл бы демон готовым раньше времени.
    pub fn prepare(&self, desired: Vec<WorkspaceEntry>) {
        let wanted = distinct(&desired);
        let opening: HashSet<String> = wanted.iter().map(|w| w.location.id().to_string()).collect();
        self.lock().desired = desired;
        self.publish(&opening);
    }

    /// Приводит открытые области к списку из настроек.
    pub async fn apply(&self, desired: Vec<WorkspaceEntry>) {
        let _gate = self.gate.lock().await;
        let disposed = {
            let mut inner = self.lock();
            inner.desired = desired;
            inner.disposed
        };
        if !disposed {
            self.reconcile().await;
        }
    }

    /// Повторяет открытие областей, которые не открылись.
    pub async fn retry(&self) {
        let _gate = self.gate.lock().await;
        let needed = {
            let inner = self.lock();
            !inner.failed.is_empty() && !inner.disposed
        };
        if needed {
            self.reconcile().await;
        }
    }

    /// Сверяет индекс открытой области с файлами сейчас (по просьбе `tasker sync` или git-хука). Сверка идёт вне блокировки списка
    /// областей: долгая сверка не задерживает их открытие и закрытие. None — эта область не открыта в демоне; ошибка сверки не
    /// прячется (в .NET это исключение запроса).
    pub async fn sync(&self, location: WorkspaceLocation) -> Option<std::io::Result<SyncResult>> {
        let opened = {
            let inner = self.lock();
            if inner.disposed {
                return None;
            }
            inner.open.get(location.id())?.0.clone()
        };
        let result = tokio::task::spawn_blocking(move || -> std::io::Result<SyncResult> {
            let ws = opened.workspace();
            ws.index().sync()?;
            let problems = ws.index().problems(Page::first(PROBLEMS_SHOWN))?;
            let series = health::compute(ws);
            Ok(SyncResult {
                path: opened.location().path().to_string(),
                problem_count: problems.total_count,
                problems: problems.data,
                series,
            })
        })
        .await
        .unwrap_or_else(|e| Err(std::io::Error::other(e.to_string())));
        if let Ok(result) = &result {
            self.report(location.id(), location.path(), &result.series);
        }
        Some(result)
    }

    /// Закрывает все области; дальше список не меняется.
    pub async fn dispose(&self) {
        let _gate = self.gate.lock().await;
        let open: Vec<Arc<OpenWorkspace>> = {
            let mut inner = self.lock();
            if inner.disposed {
                return;
            }
            inner.disposed = true;
            inner.open.drain().map(|(_, (opened, _))| opened).collect()
        };
        let registry = self.registry.clone();
        let _ = tokio::task::spawn_blocking(move || {
            for opened in open {
                let removed = registry.close(opened.location().id());
                drop(opened);
                drop(removed);
            }
        })
        .await;
    }

    async fn reconcile(&self) {
        let wanted = distinct(&self.lock().desired);
        let ids: HashSet<&str> = wanted.iter().map(|w| w.location.id()).collect();

        // Убранные из настроек — закрыть (вне блокировки данных: закрытие ждёт наблюдатель).
        let removed: Vec<(Arc<OpenWorkspace>, WorkspaceEntry)> = {
            let mut inner = self.lock();
            let gone: Vec<String> = inner.open.keys().filter(|id| !ids.contains(id.as_str())).cloned().collect();
            let removed = gone.iter().filter_map(|id| inner.open.remove(id)).collect();
            inner.failed.retain(|id, _| ids.contains(id.as_str()));
            removed
        };
        for (opened, entry) in removed {
            info!("Workspace removed from MCP: {}", entry.path);
            let id = opened.location().id().to_string();
            self.reported.lock().unwrap_or_else(|e| e.into_inner()).remove(&id);
            let registry = self.registry.clone();
            let _ = tokio::task::spawn_blocking(move || {
                let removed = registry.close(&id);
                drop(opened);
                drop(removed);
            })
            .await;
        }

        let not_open = |inner: &Inner| -> HashSet<String> {
            wanted
                .iter()
                .filter(|w| !inner.open.contains_key(w.location.id()))
                .map(|w| w.location.id().to_string())
                .collect()
        };
        let opening = not_open(&self.lock());
        self.publish(&opening);

        let mut opened_now: Vec<WorkspaceLocation> = Vec::new();
        for wanted in wanted.iter().filter(|w| opening.contains(w.location.id())) {
            match self.open(&wanted.location).await {
                Ok(opened) => {
                    info!(
                        "Workspace available through MCP: {}, argument workspace=\"{}\" (address {})",
                        wanted.location.path(),
                        opened.key(),
                        mcp_url(self.port)
                    );
                    let mut inner = self.lock();
                    inner.open.insert(wanted.location.id().to_string(), (opened, wanted.entry.clone()));
                    inner.failed.remove(wanted.location.id());
                    opened_now.push(wanted.location.clone());
                }
                Err(error) => {
                    warn!("Cannot open workspace {}: {error}", wanted.entry.path);
                    self.lock().failed.insert(wanted.location.id().to_string(), error);
                }
            }
            let still_opening: HashSet<String> = {
                let inner = self.lock();
                not_open(&inner).into_iter().filter(|id| !inner.failed.contains_key(id)).collect()
            };
            self.publish(&still_opening);
        }
        self.publish(&HashSet::new());

        // Состояние серий сообщаем после открытия, когда статус уже опубликован: сверка серий не задерживает готовность.
        for location in opened_now {
            let Some(opened) = self.lock().open.get(location.id()).map(|(o, _)| o.clone()) else {
                continue;
            };
            let series = tokio::task::spawn_blocking(move || health::compute(opened.workspace())).await;
            match series {
                Ok(series) => self.report(location.id(), location.path(), &series),
                Err(e) => warn!("Workspace {}: cannot check the series: {e}", location.path()),
            }
        }
    }

    /// Открытие одной области: проверка папки, тестовая задержка, реестр. Ошибка — текст для статуса.
    async fn open(&self, location: &WorkspaceLocation) -> Result<Arc<OpenWorkspace>, String> {
        let path = std::path::Path::new(location.path());
        // Не создаём .tasker в папке, которой уже нет: открытие делает недостающие каталоги.
        let exists = match location.kind() {
            WorkspaceKind::Files => path.is_dir(),
            WorkspaceKind::Sqlite => path.is_file(),
        };
        if !exists {
            return Err(format!("{} does not exist", location.path()));
        }
        if let Some(delay) = std::env::var(OPEN_DELAY_VARIABLE)
            .ok()
            .and_then(|v| v.trim().parse::<u64>().ok())
            .filter(|d| *d > 0)
        {
            tokio::time::sleep(Duration::from_millis(delay)).await;
        }
        let registry = self.registry.clone();
        let location = location.clone();
        match tokio::task::spawn_blocking(move || registry.open(&location)).await {
            Ok(Ok(opened)) => Ok(opened),
            Ok(Err(e)) => Err(e.to_string()),
            Err(e) => Err(format!("cannot open the workspace: {e}")),
        }
    }

    fn publish(&self, opening: &HashSet<String>) {
        let inner = self.lock();
        let statuses = distinct(&inner.desired)
            .into_iter()
            .map(|w| {
                let id = w.location.id();
                let (state, key, error) = if let Some((opened, _)) = inner.open.get(id) {
                    (WorkspaceState::Open, Some(opened.key().to_string()), None)
                } else if let Some(error) = inner.failed.get(id) {
                    (WorkspaceState::Failed, None, Some(error.clone()))
                } else if opening.contains(id) {
                    (WorkspaceState::Opening, None, None)
                } else {
                    (WorkspaceState::Failed, None, None)
                };
                WorkspaceStatus {
                    kind: w.entry.kind,
                    path: w.entry.path.clone(),
                    state,
                    key,
                    error,
                }
            })
            .collect();
        self.state.set_workspaces(statuses);
    }

    /// Пишет в лог, что не так с сериями: дубликат префикса — Error («требуется переименование»), остальное — Warning. Повторяет
    /// сообщение, только когда состояние области изменилось.
    fn report(&self, id: &str, path: &str, series: &[ProjectSeriesHealth]) {
        let mut errors: Vec<String> = Vec::new();
        let mut warnings: Vec<String> = Vec::new();
        let mut link_warnings: Vec<String> = Vec::new();
        for project in series {
            let name = &project.project_name;
            for conflict in &project.prefix_conflicts {
                errors.push(format!(
                    "project '{name}': series prefix '{}' is used by several series: {}: {PREFIX_HINT}",
                    conflict.prefix,
                    conflict.series_ids.iter().map(guid_d).collect::<Vec<_>>().join(", ")
                ));
            }
            for conflict in &project.number_conflicts {
                warnings.push(format!(
                    "project '{name}': {} is used by tasks {}",
                    conflict.reference,
                    conflict.task_ids.iter().map(guid_d).collect::<Vec<_>>().join(", ")
                ));
            }
            if project.tasks_with_invalid_series > 0 {
                warnings.push(format!(
                    "project '{name}': {} task(s) refer to a missing series (run 'tasker cleanup')",
                    project.tasks_with_invalid_series
                ));
            }
            if project.unreadable_series_files > 0 {
                warnings.push(format!(
                    "project '{name}': {} series file(s) cannot be read",
                    project.unreadable_series_files
                ));
            }
            for line in health::describe_links(project) {
                link_warnings.push(format!("project '{name}': {line}"));
            }
            if let Some(error) = &project.error {
                warnings.push(format!("project '{name}': {error}"));
            }
        }

        let signature = errors
            .iter()
            .chain(warnings.iter())
            .chain(link_warnings.iter())
            .cloned()
            .collect::<Vec<_>>()
            .join("\n");
        {
            let mut reported = self.reported.lock().unwrap_or_else(|e| e.into_inner());
            if reported.get(id).map(String::as_str).unwrap_or("") == signature {
                return;
            }
            reported.insert(id.to_string(), signature);
        }

        if !errors.is_empty() {
            error!("Workspace {path}: series rename required: {}", errors.join("; "));
        }
        if !warnings.is_empty() {
            warn!("Workspace {path}: series need attention: {}", warnings.join("; "));
        }
        if !link_warnings.is_empty() {
            warn!("Workspace {path}: links need attention: {}", link_warnings.join("; "));
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }
}

/// Записи настроек с адресами, без повторов одной области (`DistinctBy(Location.Id)`, первая запись остаётся).
fn distinct(desired: &[WorkspaceEntry]) -> Vec<Wanted> {
    let mut seen = HashSet::new();
    desired
        .iter()
        .map(|entry| Wanted {
            entry: entry.clone(),
            location: entry.location(),
        })
        .filter(|w| seen.insert(w.location.id().to_string()))
        .collect()
}
