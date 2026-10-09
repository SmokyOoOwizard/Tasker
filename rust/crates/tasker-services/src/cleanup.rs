//! Чистка после слияния git (`CleanupService`): единственное место, где сверка что-то пишет. Убирает недействительные ссылки на серии,
//! недействительные связи (на задачу или тип, которых нет) и с `resolve_conflicts` решает дубликаты номеров. Нечитаемые файлы
//! серий — чистка пропущена; нечитаемые задачи или типы связей — связи не проверяются.
use crate::Workspace;
use crate::error::Result;
use crate::health::{self, PrefixConflict};
use crate::link_cycles::{self, LinkCycle};
use crate::rewrites;
use std::collections::{HashMap, HashSet};
use tasker_core::ids::guid_d;
use tasker_core::model::{Series, TaskItem, TaskLink, TaskSeriesNumber};
use tasker_files::index::NumberConflict;
use uuid::Uuid;

const ATTEMPTS: usize = 2;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CleanupOptions {
    /// Ещё и решить дубликаты номеров: сохраняет номер самая ранняя задача, остальные получают максимум + 1.
    pub resolve_conflicts: bool,
    /// Ничего не менять, только рассказать, что изменится.
    pub dry_run: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CleanupChangeKind {
    RemovedInvalidSeries,
    Renumbered,
    RemovedInvalidLink,
}

impl CleanupChangeKind {
    /// Как в JSON .NET (enum строкой в camelCase).
    pub fn json_name(self) -> &'static str {
        match self {
            Self::RemovedInvalidSeries => "removedInvalidSeries",
            Self::Renumbered => "renumbered",
            Self::RemovedInvalidLink => "removedInvalidLink",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CleanupChange {
    pub task_id: Uuid,
    pub task_title: String,
    pub kind: CleanupChangeKind,
    pub series_id: Option<Uuid>,
    pub old_number: Option<i32>,
    pub new_number: Option<i32>,
    pub description: String,
    pub link_type_id: Option<Uuid>,
    pub link_target_id: Option<Uuid>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CleanupReport {
    pub changes: Vec<CleanupChange>,
    /// Дубликаты, которые остались: без `resolve_conflicts` — все, иначе пусто.
    pub remaining_number_conflicts: Vec<NumberConflict>,
    pub prefix_conflicts: Vec<PrefixConflict>,
    pub skipped: bool,
    pub skip_reason: Option<String>,
    pub links_skip_reason: Option<String>,
    pub link_cycles: Vec<LinkCycle>,
}

pub struct CleanupService<'a> {
    ws: &'a Workspace,
}

impl<'a> CleanupService<'a> {
    pub fn new(ws: &'a Workspace) -> CleanupService<'a> {
        CleanupService { ws }
    }

    pub fn run(&self, project_id: &Uuid, options: CleanupOptions) -> Result<CleanupReport> {
        // Задачи, которые чистка изменит, не должны быть заняты другим: ждём до секции записи и до первой записи.
        if !options.dry_run && self.ws.count_unreadable::<Series>(project_id)? == 0 {
            let known: HashSet<Uuid> = self.ws.get_all::<Series>(project_id)?.into_iter().map(|x| x.id).collect();
            self.wait_for_locks(project_id, &known, options.resolve_conflicts)?;
        }
        self.ws.exclusive(project_id, || self.run_locked(project_id, options))
    }

    fn run_locked(&self, project_id: &Uuid, options: CleanupOptions) -> Result<CleanupReport> {
        let all = self.ws.get_all::<Series>(project_id)?;
        let prefix_conflicts = health::find_prefix_conflicts(&all);

        if self.ws.count_unreadable::<Series>(project_id)? > 0 {
            return Ok(CleanupReport {
                changes: vec![],
                remaining_number_conflicts: self.ws.index().number_conflicts(project_id)?,
                prefix_conflicts,
                skipped: true,
                skip_reason: Some(
                    "Some series files cannot be read (merge conflict or broken YAML): a series that cannot be read would look missing and references to it would be wiped. Nothing was changed; fix the series files and run cleanup again.".to_string(),
                ),
                links_skip_reason: None,
                link_cycles: vec![],
            });
        }

        let known: HashSet<Uuid> = all.iter().map(|x| x.id).collect();
        let mut changes: Vec<CleanupChange> = Vec::new();
        self.remove_invalid_references(project_id, &known, options.dry_run, &mut changes)?;

        // Дубликаты читаем после первого шага. В dry run ссылки ещё на месте, поэтому дубликаты в несуществующих сериях отбрасываем сами.
        let mut conflicts: Vec<NumberConflict> = self
            .ws
            .index()
            .number_conflicts(project_id)?
            .into_iter()
            .filter(|x| known.contains(&x.series_id))
            .collect();
        if options.resolve_conflicts {
            self.resolve_conflicts(project_id, &all, &conflicts, options.dry_run, &mut changes)?;
            conflicts = vec![];
        }

        let links_skip_reason = self.remove_invalid_links(project_id, options.dry_run, &mut changes)?;

        // Циклы связей — после правок связей, только чтение.
        let cycles = link_cycles::find(self.ws, project_id, &self.ws.link_types().get_all(project_id)?)?;

        Ok(CleanupReport {
            changes,
            remaining_number_conflicts: conflicts,
            prefix_conflicts,
            skipped: false,
            skip_reason: None,
            links_skip_reason,
            link_cycles: cycles,
        })
    }

    /// Задачи, которые изменит чистка, — ждём их блокировки.
    fn wait_for_locks(&self, project_id: &Uuid, known: &HashSet<Uuid>, resolve_conflicts: bool) -> Result<()> {
        let mut affected: HashMap<Uuid, TaskItem> = HashMap::new();
        let known_ids: Vec<Uuid> = known.iter().copied().collect();
        for task in self.ws.index().tasks_with_series_not_in(project_id, &known_ids)? {
            affected.insert(task.id, task);
        }
        if resolve_conflicts {
            for conflict in self
                .ws
                .index()
                .number_conflicts(project_id)?
                .iter()
                .filter(|x| known.contains(&x.series_id))
            {
                let members = self.members(project_id, conflict)?;
                // Самая ранняя сохраняет номер и не меняется.
                for task in members.into_iter().skip(1) {
                    affected.insert(task.id, task);
                }
            }
        }
        if self.ws.count_unreadable::<TaskItem>(project_id)? + self.ws.link_types().count_unreadable(project_id)? == 0 {
            let types: Vec<Uuid> = self.ws.link_types().get_all(project_id)?.into_iter().map(|x| x.id).collect();
            for task in self.ws.index().tasks_with_invalid_links(project_id, &types)? {
                affected.insert(task.id, task);
            }
        }
        rewrites::wait_for_locks(self.ws, affected.values(), None)
    }

    /// Задачи дубликата по createdAt, затем по тексту Guid.
    fn members(&self, project_id: &Uuid, conflict: &NumberConflict) -> Result<Vec<TaskItem>> {
        let mut members = Vec::new();
        for id in &conflict.task_ids {
            if let Some(member) = self.ws.get_by_id::<TaskItem>(project_id, id)? {
                members.push(member);
            }
        }
        members.sort_by(|a, b| {
            a.created_at
                .unix_ticks()
                .cmp(&b.created_at.unix_ticks())
                .then_with(|| guid_d(&a.id).cmp(&guid_d(&b.id)))
        });
        Ok(members)
    }

    /// Убирает связи на задачи и типы связей, которых нет. Возвращает причину, по которой связи не проверялись; None — проверены.
    fn remove_invalid_links(&self, project_id: &Uuid, dry_run: bool, changes: &mut Vec<CleanupChange>) -> Result<Option<String>> {
        let unreadable = self.ws.count_unreadable::<TaskItem>(project_id)? + self.ws.link_types().count_unreadable(project_id)?;
        if unreadable > 0 {
            return Ok(Some(format!(
                "{unreadable} task or link type file(s) cannot be read (merge conflict or broken YAML): a task or a type that cannot be read would look missing and the links to it would be wiped. The links were not checked; fix the files and run cleanup again."
            )));
        }
        let types: HashMap<Uuid, String> = self
            .ws
            .link_types()
            .get_all(project_id)?
            .into_iter()
            .map(|x| (x.id, x.name))
            .collect();
        let known: Vec<Uuid> = types.keys().copied().collect();
        for task in self.ws.index().tasks_with_invalid_links(project_id, &known)? {
            let mut invalid: Vec<TaskLink> = Vec::new();
            for link in &task.links {
                match types.get(&link.type_id) {
                    None => {
                        invalid.push(*link);
                        changes.push(CleanupChange {
                            task_id: task.id,
                            task_title: task.title.clone(),
                            kind: CleanupChangeKind::RemovedInvalidLink,
                            series_id: None,
                            old_number: None,
                            new_number: None,
                            description: format!(
                                "Task '{}': removed a link of a link type that does not exist ({}) to {}",
                                task.title,
                                guid_d(&link.type_id),
                                guid_d(&link.target_id)
                            ),
                            link_type_id: Some(link.type_id),
                            link_target_id: Some(link.target_id),
                        });
                    }
                    Some(type_name) => {
                        if self.ws.get_by_id::<TaskItem>(project_id, &link.target_id)?.is_none() {
                            invalid.push(*link);
                            changes.push(CleanupChange {
                                task_id: task.id,
                                task_title: task.title.clone(),
                                kind: CleanupChangeKind::RemovedInvalidLink,
                                series_id: None,
                                old_number: None,
                                new_number: None,
                                description: format!(
                                    "Task '{}': removed link '{type_name}' to a task that does not exist ({})",
                                    task.title,
                                    guid_d(&link.target_id)
                                ),
                                link_type_id: Some(link.type_id),
                                link_target_id: Some(link.target_id),
                            });
                        }
                    }
                }
            }
            if !invalid.is_empty() && !dry_run {
                let invalid = invalid.clone();
                rewrites::modify_all(
                    self.ws,
                    [&task],
                    &move |t: &TaskItem| {
                        t.links.iter().any(|x| invalid.contains(x)).then(|| TaskItem {
                            links: t.links.iter().filter(|x| !invalid.contains(x)).copied().collect(),
                            ..t.clone()
                        })
                    },
                    ATTEMPTS,
                )?;
            }
        }
        Ok(None)
    }

    fn remove_invalid_references(
        &self,
        project_id: &Uuid,
        known: &HashSet<Uuid>,
        dry_run: bool,
        changes: &mut Vec<CleanupChange>,
    ) -> Result<()> {
        let known_ids: Vec<Uuid> = known.iter().copied().collect();
        for task in self.ws.index().tasks_with_series_not_in(project_id, &known_ids)? {
            let invalid: Vec<TaskSeriesNumber> = task
                .series_numbers
                .iter()
                .filter(|x| !known.contains(&x.series_id))
                .copied()
                .collect();
            if !dry_run {
                let known = known.clone();
                rewrites::modify_all(
                    self.ws,
                    [&task],
                    &move |t: &TaskItem| {
                        t.series_numbers.iter().any(|x| !known.contains(&x.series_id)).then(|| TaskItem {
                            series_numbers: rewrites::without(t, |x| !known.contains(&x.series_id)),
                            ..t.clone()
                        })
                    },
                    ATTEMPTS,
                )?;
            }
            for reference in invalid {
                changes.push(CleanupChange {
                    task_id: task.id,
                    task_title: task.title.clone(),
                    kind: CleanupChangeKind::RemovedInvalidSeries,
                    series_id: Some(reference.series_id),
                    old_number: Some(reference.number),
                    new_number: None,
                    description: format!(
                        "Task '{}': removed invalid series reference (was #{})",
                        task.title, reference.number
                    ),
                    link_type_id: None,
                    link_target_id: None,
                });
            }
        }
        Ok(())
    }

    fn resolve_conflicts(
        &self,
        project_id: &Uuid,
        all: &[Series],
        conflicts: &[NumberConflict],
        dry_run: bool,
        changes: &mut Vec<CleanupChange>,
    ) -> Result<()> {
        // Следующий номер серии считаем сами: в dry run ничего не записано, а номера, выданные раньше в этом же запуске, занимать нельзя.
        let mut next: HashMap<Uuid, i32> = HashMap::new();
        for conflict in conflicts {
            let prefix = all
                .iter()
                .find(|x| x.id == conflict.series_id)
                .map(|x| x.prefix.clone())
                .unwrap_or_default();
            let members = self.members(project_id, conflict)?;
            // Самая ранняя задача сохраняет номер, остальные получают следующие в том же порядке.
            for task in members.into_iter().skip(1) {
                let number = match next.get(&conflict.series_id) {
                    Some(n) => *n,
                    None => self.ws.index().max_number(project_id, &conflict.series_id)? + 1,
                };
                next.insert(conflict.series_id, number + 1);
                if !dry_run {
                    let series_id = conflict.series_id;
                    rewrites::modify_all(
                        self.ws,
                        [&task],
                        &move |t: &TaskItem| {
                            Some(TaskItem {
                                series_numbers: t
                                    .series_numbers
                                    .iter()
                                    .map(|x| {
                                        if x.series_id == series_id {
                                            TaskSeriesNumber { number, ..*x }
                                        } else {
                                            *x
                                        }
                                    })
                                    .collect(),
                                ..t.clone()
                            })
                        },
                        ATTEMPTS,
                    )?;
                }
                changes.push(CleanupChange {
                    task_id: task.id,
                    task_title: task.title.clone(),
                    kind: CleanupChangeKind::Renumbered,
                    series_id: Some(conflict.series_id),
                    old_number: Some(conflict.number),
                    new_number: Some(number),
                    description: format!(
                        "Task '{}': duplicate number {prefix}-{} changed to {prefix}-{number}",
                        task.title, conflict.number
                    ),
                    link_type_id: None,
                    link_target_id: None,
                });
            }
        }
        Ok(())
    }
}
