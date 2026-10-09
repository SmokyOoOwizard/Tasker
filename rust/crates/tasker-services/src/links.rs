//! Связи между задачами одного проекта (`TaskLinkService`, `TaskLinks` в .NET): связь хранится в задаче-источнике, обратная
//! сторона вычисляется. Добавление и удаление коммутативны, поэтому версия необязательна: без неё операция берёт актуальную
//! задачу и при гонке повторяется.
use crate::Workspace;
use crate::error::{Error, Result};
use crate::link_cycles;
use crate::rewrites;
use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use tasker_core::ShortId;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{LinkType, Series, TaskItem, TaskLink, TaskSeriesNumber};
use tasker_core::validate::to_lower_invariant;
use tasker_core::versioning;
use uuid::Uuid;

/// Связи коммутативны, и версию клиент не передаёт: гонку надо пережить, поэтому попыток много.
const ATTEMPTS: usize = 25;

const INBOUND_ATTEMPTS: usize = 3;

/// Сколько связей показывает представление задачи; остальные — через `get_task_links`.
pub const MAX_VIEWED: usize = 100;

/// С какой стороны связи смотрит задача.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum LinkDirection {
    Outward,
    Inward,
}

impl LinkDirection {
    pub fn name(self) -> &'static str {
        match self {
            Self::Outward => "outward",
            Self::Inward => "inward",
        }
    }
}

/// Задача на другом конце связи.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkedTask {
    pub id: Uuid,
    pub title: String,
    pub status_id: Uuid,
    pub series_numbers: Vec<TaskSeriesNumber>,
}

/// Связь глазами одной задачи: `name` — название именно с её стороны.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskLinkView {
    pub type_id: Uuid,
    pub type_name: String,
    pub direction: LinkDirection,
    pub name: String,
    pub task: LinkedTask,
}

// Очередь изменений связей по задаче-источнику внутри процесса (полосы по хэшу id).
fn stripes() -> &'static [Mutex<()>; 256] {
    static STRIPES: OnceLock<[Mutex<()>; 256]> = OnceLock::new();
    STRIPES.get_or_init(|| std::array::from_fn(|_| Mutex::new(())))
}

/// Убирает у других задач связи, которые указывали на удалённую задачу (`TaskLinks.RemoveInbound`).
pub fn remove_inbound(ws: &Workspace, project_id: &Uuid, target_id: &Uuid) -> Result<()> {
    for source in ws.index().tasks_linked_to(project_id, target_id)? {
        let target = *target_id;
        rewrites::modify(
            ws,
            &source,
            &move |t: &TaskItem| {
                t.links.iter().any(|x| x.target_id == target).then(|| TaskItem {
                    links: t.links.iter().filter(|x| x.target_id != target).copied().collect(),
                    ..t.clone()
                })
            },
            INBOUND_ATTEMPTS,
        )?;
    }
    Ok(())
}

pub struct TaskLinkService<'a> {
    ws: &'a Workspace,
}

fn subject(task: &TaskItem) -> String {
    format!("Task '{}'", task.title)
}

fn linked(task: &TaskItem) -> LinkedTask {
    LinkedTask {
        id: task.id,
        title: task.title.clone(),
        status_id: task.status_id,
        series_numbers: task.series_numbers.clone(),
    }
}

impl<'a> TaskLinkService<'a> {
    pub fn new(ws: &'a Workspace) -> TaskLinkService<'a> {
        TaskLinkService { ws }
    }

    /// Добавляет связь «задача —тип→ цель». Такая связь уже есть — задача как есть; у симметричного типа обратная связь тоже считается
    /// имеющейся. `version` None — без проверки. None — задачи нет.
    pub fn add(
        &self,
        project_id: &Uuid,
        task_id: &Uuid,
        type_id: &Uuid,
        target_id: &Uuid,
        version: Option<&str>,
    ) -> Result<Option<TaskItem>> {
        let Some(task) = self.ws.get_by_id::<TaskItem>(project_id, task_id)? else {
            return Ok(None);
        };
        // Связь — запись: типы по умолчанию должны быть сохранены.
        let types = self.ws.link_types();
        types.ensure_defaults(project_id)?;
        let link_type = types
            .get_by_id(project_id, type_id)?
            .ok_or_else(|| Error::validation(format!("TypeId: link type not found in the project: {}", guid_d(type_id))))?;
        if target_id == task_id {
            return Err(Error::validation("TargetId: a task cannot be linked to itself"));
        }
        let target = self
            .ws
            .get_by_id::<TaskItem>(project_id, target_id)?
            .ok_or_else(|| Error::validation(format!("TargetId: task not found in the project: {}", guid_d(target_id))))?;

        self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject(&task))?;

        let link = TaskLink {
            type_id: *type_id,
            target_id: *target_id,
        };
        // Симметричная связь уже есть с другой стороны — второй раз не нужна.
        if link_type.is_symmetric()
            && target.links.contains(&TaskLink {
                type_id: *type_id,
                target_id: *task_id,
            })
        {
            return Ok(Some(task));
        }

        let add = move |t: &TaskItem| -> Option<TaskItem> {
            (!t.links.contains(&link)).then(|| {
                let mut links = t.links.clone();
                links.push(link);
                TaskItem { links, ..t.clone() }
            })
        };
        if link_type.allow_cycles || link_type.is_symmetric() || task.links.contains(&link) {
            return self.change(&task, version, &add);
        }

        // Тип без циклов: проверка и запись — одна секция записи проекта.
        self.ws.exclusive(project_id, || {
            self.ensure_no_cycle(project_id, &link_type, task_id, &target)?;
            self.change(&task, version, &add)
        })
    }

    /// Id иерархических типов проекта; пусто — иерархии нет.
    pub fn hierarchical_type_ids(&self, project_id: &Uuid) -> Result<Vec<Uuid>> {
        Ok(self
            .ws
            .link_types()
            .get_all(project_id)?
            .into_iter()
            .filter(|x| x.hierarchical && !x.is_symmetric())
            .map(|x| x.id)
            .collect())
    }

    /// Связь «источник → цель» замкнёт цикл, если от цели по связям этого типа можно дойти до источника.
    fn ensure_no_cycle(&self, project_id: &Uuid, link_type: &LinkType, source_id: &Uuid, target: &TaskItem) -> Result<()> {
        // Иерархические типы — один граф эпиков.
        let type_ids = if link_type.hierarchical {
            self.hierarchical_type_ids(project_id)?
        } else {
            vec![link_type.id]
        };
        let Some(path) = link_cycles::find_path(self.ws, project_id, &type_ids, target.id, *source_id)? else {
            return Ok(());
        };
        let prefixes: HashMap<Uuid, String> = self
            .ws
            .get_all::<Series>(project_id)?
            .into_iter()
            .map(|x| (x.id, x.prefix))
            .collect();
        let mut cycle = vec![*source_id];
        cycle.extend(path);
        let mut names = Vec::new();
        for id in &cycle {
            let task = if *id == target.id {
                Some(target.clone())
            } else {
                self.ws.get_by_id::<TaskItem>(project_id, id)?
            };
            names.push(match task {
                None => ShortId::of(id),
                Some(t) => link_cycles::reference(id, &t.series_numbers, &prefixes),
            });
        }
        Err(Error::validation(format!(
            "Cycle: {} (link type '{}' does not allow cycles; remove the opposite link or allow cycles for the type)",
            names.join(" → "),
            link_type.name
        )))
    }

    /// Убирает связь «задача —тип→ цель». Нет такой — задача как есть; у симметричного типа убирается и обратная. None — задачи нет.
    pub fn remove(
        &self,
        project_id: &Uuid,
        task_id: &Uuid,
        type_id: &Uuid,
        target_id: &Uuid,
        version: Option<&str>,
    ) -> Result<Option<TaskItem>> {
        let Some(task) = self.ws.get_by_id::<TaskItem>(project_id, task_id)? else {
            return Ok(None);
        };
        self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject(&task))?;

        let link = TaskLink {
            type_id: *type_id,
            target_id: *target_id,
        };
        let result = self.change(&task, version, &move |t: &TaskItem| {
            t.links.contains(&link).then(|| TaskItem {
                links: t.links.iter().filter(|x| **x != link).copied().collect(),
                ..t.clone()
            })
        })?;

        // Симметричную связь могли создать с другой стороны — убираем её тоже.
        if result.is_some()
            && self
                .ws
                .link_types()
                .get_by_id(project_id, type_id)?
                .is_some_and(|t| t.is_symmetric())
            && let Some(other) = self.ws.get_by_id::<TaskItem>(project_id, target_id)?
        {
            let reverse = TaskLink {
                type_id: *type_id,
                target_id: *task_id,
            };
            if other.links.contains(&reverse) {
                self.ws.locks().ensure_writable(LockedEntity::Task, &other.id, &subject(&other))?;
                rewrites::modify(
                    self.ws,
                    &other,
                    &move |t: &TaskItem| {
                        t.links.contains(&reverse).then(|| TaskItem {
                            links: t.links.iter().filter(|x| **x != reverse).copied().collect(),
                            ..t.clone()
                        })
                    },
                    ATTEMPTS,
                )?;
            }
        }
        Ok(result)
    }

    /// Все связи задачи с обеих сторон; недействительные пропускаются. Порядок: тип, исходящие раньше входящих, заголовок, id. None — задачи нет.
    pub fn get_links(&self, project_id: &Uuid, task_id: &Uuid) -> Result<Option<Vec<TaskLinkView>>> {
        match self.ws.get_by_id::<TaskItem>(project_id, task_id)? {
            None => Ok(None),
            Some(task) => Ok(Some(self.links_of(project_id, &task)?)),
        }
    }

    /// Как [`get_links`](Self::get_links), для уже прочитанной задачи.
    pub fn links_of(&self, project_id: &Uuid, task: &TaskItem) -> Result<Vec<TaskLinkView>> {
        let known: HashMap<Uuid, LinkType> = self.ws.link_types().get_all(project_id)?.into_iter().map(|x| (x.id, x)).collect();
        let mut views: Vec<TaskLinkView> = Vec::new();
        for link in &task.links {
            if let Some(link_type) = known.get(&link.type_id)
                && let Some(other) = self.ws.get_by_id::<TaskItem>(project_id, &link.target_id)?
            {
                views.push(TaskLinkView {
                    type_id: link_type.id,
                    type_name: link_type.name.clone(),
                    direction: LinkDirection::Outward,
                    name: link_type.outward_name.clone(),
                    task: linked(&other),
                });
            }
        }
        for source in self.ws.index().tasks_linked_to(project_id, &task.id)? {
            for link in source.links.iter().filter(|x| x.target_id == task.id) {
                let Some(link_type) = known.get(&link.type_id) else {
                    continue;
                };
                // У симметричной связи, созданной с обеих сторон, обе записи выглядят одинаково — показываем одну.
                if link_type.is_symmetric() && views.iter().any(|x| x.type_id == link_type.id && x.task.id == source.id) {
                    continue;
                }
                views.push(TaskLinkView {
                    type_id: link_type.id,
                    type_name: link_type.name.clone(),
                    direction: LinkDirection::Inward,
                    name: link_type.inward_name.clone(),
                    task: linked(&source),
                });
            }
        }
        views.sort_by(|a, b| {
            to_lower_invariant(&a.type_name)
                .cmp(&to_lower_invariant(&b.type_name))
                .then_with(|| a.direction.cmp(&b.direction))
                .then_with(|| to_lower_invariant(&a.task.title).cmp(&to_lower_invariant(&b.task.title)))
                .then_with(|| a.task.id.cmp(&b.task.id))
        });
        Ok(views)
    }

    fn change(&self, task: &TaskItem, version: Option<&str>, change: &dyn Fn(&TaskItem) -> Option<TaskItem>) -> Result<Option<TaskItem>> {
        if version.is_none_or(|v| v.trim().is_empty()) {
            // Параллельные связи от одной задачи в одном процессе ставим в очередь по задаче-источнику.
            let stripe = &stripes()[(task.id.as_bytes()[15] as usize) % 256];
            let _held = stripe.lock().unwrap_or_else(|e| e.into_inner());
            let Some(current) = self.ws.get_by_id::<TaskItem>(&task.project_id, &task.id)? else {
                return Ok(None);
            };
            return rewrites::modify(self.ws, &current, change, ATTEMPTS);
        }

        let subject = subject(task);
        let expected = versioning::check(&task.version, version, &subject)?;
        let Some(changed) = change(task) else {
            return Ok(Some(task.clone()));
        };
        let mut updated = TaskItem {
            updated_at: self.ws.now(),
            ..changed
        };
        let new_version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
        updated.version = new_version;
        Ok(Some(updated))
    }
}
