//! Состояние серий и связей проекта — только чтение (`SeriesHealthService`, `LinkHealthService`, `SeriesReports` в .NET): им пользуются
//! `tasker sync` и `cleanup --check`.
use crate::Workspace;
use crate::error::Result;
use crate::link_cycles::{self, LinkCycle};
use crate::link_type;
use std::collections::HashMap;
use tasker_core::ids::guid_d;
use tasker_core::model::{LinkType, Series};
use tasker_files::index::NumberConflict;
use uuid::Uuid;

/// Несколько серий проекта с одним префиксом — после слияния веток git. Серии по Guid.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PrefixConflict {
    pub prefix: String,
    pub series_ids: Vec<Uuid>,
}

/// Состояние серий проекта.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SeriesHealth {
    pub number_conflicts: Vec<NumberConflict>,
    pub prefix_conflicts: Vec<PrefixConflict>,
    /// Сколько задач ссылаются на серию, которой нет (0 при нечитаемых файлах серий).
    pub tasks_with_invalid_series: usize,
    pub unreadable_series_files: usize,
}

impl SeriesHealth {
    pub fn needs_attention(&self) -> bool {
        !self.number_conflicts.is_empty()
            || !self.prefix_conflicts.is_empty()
            || self.tasks_with_invalid_series > 0
            || self.unreadable_series_files > 0
    }
}

pub struct SeriesHealthService<'a> {
    ws: &'a Workspace,
}

impl<'a> SeriesHealthService<'a> {
    pub fn new(ws: &'a Workspace) -> SeriesHealthService<'a> {
        SeriesHealthService { ws }
    }

    pub fn check(&self, project_id: &Uuid) -> Result<SeriesHealth> {
        let number_conflicts = self.ws.index().number_conflicts(project_id)?;
        let all = self.ws.get_all::<Series>(project_id)?;
        let unreadable = self.ws.count_unreadable::<Series>(project_id)?;
        // Нечитаемая серия выглядела бы несуществующей: тогда ссылки на неё не считаем недействительными.
        let invalid = if unreadable > 0 {
            0
        } else {
            let known: Vec<Uuid> = all.iter().map(|x| x.id).collect();
            self.ws.index().tasks_with_series_not_in(project_id, &known)?.len()
        };
        Ok(SeriesHealth {
            number_conflicts,
            prefix_conflicts: find_prefix_conflicts(&all),
            tasks_with_invalid_series: invalid,
            unreadable_series_files: unreadable,
        })
    }
}

/// Серии с одним префиксом (точное совпадение): группы по префиксу (ordinal), id по возрастанию.
pub fn find_prefix_conflicts(all: &[Series]) -> Vec<PrefixConflict> {
    let mut groups: HashMap<&str, Vec<Uuid>> = HashMap::new();
    for series in all {
        groups.entry(series.prefix.as_str()).or_default().push(series.id);
    }
    let mut result: Vec<PrefixConflict> = groups
        .into_iter()
        .filter(|(_, ids)| ids.len() > 1)
        .map(|(prefix, mut ids)| {
            ids.sort_by_key(guid_d);
            PrefixConflict {
                prefix: prefix.to_string(),
                series_ids: ids,
            }
        })
        .collect();
    result.sort_by(|a, b| tasker_core::tasks::ordinal(&a.prefix, &b.prefix));
    result
}

/// Состояние связей проекта.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkHealth {
    /// Сколько задач имеют связь на задачу или тип, которых нет (0 при нечитаемых файлах).
    pub tasks_with_invalid_links: usize,
    /// Сколько файлов задач и типов связей не удалось прочитать.
    pub unreadable_files: usize,
    /// Циклы из связей типов, которые циклов не допускают.
    pub cycles: Vec<LinkCycle>,
}

impl LinkHealth {
    pub fn needs_attention(&self) -> bool {
        self.tasks_with_invalid_links > 0 || self.unreadable_files > 0 || !self.cycles.is_empty()
    }
}

pub struct LinkHealthService<'a> {
    ws: &'a Workspace,
}

impl<'a> LinkHealthService<'a> {
    pub fn new(ws: &'a Workspace) -> LinkHealthService<'a> {
        LinkHealthService { ws }
    }

    pub fn check(&self, project_id: &Uuid) -> Result<LinkHealth> {
        let unreadable =
            self.ws.count_unreadable::<tasker_core::model::TaskItem>(project_id)? + self.ws.count_unreadable::<LinkType>(project_id)?;
        // Пока типов нет в хранилище, показываются типы по умолчанию: связи на них действительны.
        let stored = self.ws.get_all::<LinkType>(project_id)?;
        let all = if stored.is_empty() {
            link_type::defaults(project_id)
        } else {
            stored
        };
        let cycles = link_cycles::find(self.ws, project_id, &all)?;
        if unreadable > 0 {
            return Ok(LinkHealth {
                tasks_with_invalid_links: 0,
                unreadable_files: unreadable,
                cycles,
            });
        }
        let known: Vec<Uuid> = all.iter().map(|x| x.id).collect();
        Ok(LinkHealth {
            tasks_with_invalid_links: self.ws.index().tasks_with_invalid_links(project_id, &known)?.len(),
            unreadable_files: 0,
            cycles,
        })
    }
}

/// Дубликат номера в серии: `TSK-5` есть у нескольких задач (`SeriesNumberProblem`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SeriesNumberProblem {
    pub reference: String,
    pub series_id: Uuid,
    pub number: i32,
    pub task_ids: Vec<Uuid>,
}

/// Цикл из связей типа, который циклов не допускает (`LinkCycleProblem`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkCycleProblem {
    pub type_id: Uuid,
    pub type_name: String,
    pub path: String,
    pub task_ids: Vec<Uuid>,
}

/// Что не в порядке с сериями и связями одного проекта (`ProjectSeriesHealth`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProjectSeriesHealth {
    pub project_id: Uuid,
    pub project_name: String,
    pub number_conflicts: Vec<SeriesNumberProblem>,
    pub prefix_conflicts: Vec<PrefixConflict>,
    pub tasks_with_invalid_series: usize,
    pub unreadable_series_files: usize,
    /// Не удалось проверить проект: причина; остальные поля тогда пусты.
    pub error: Option<String>,
    pub tasks_with_invalid_links: usize,
    pub unreadable_link_files: usize,
    pub link_cycles: Vec<LinkCycleProblem>,
}

impl ProjectSeriesHealth {
    pub fn has_problems(&self) -> bool {
        self.error.is_some()
            || !self.number_conflicts.is_empty()
            || !self.prefix_conflicts.is_empty()
            || self.tasks_with_invalid_series > 0
            || self.unreadable_series_files > 0
            || self.tasks_with_invalid_links > 0
            || self.unreadable_link_files > 0
            || !self.link_cycles.is_empty()
    }

    pub fn has_link_problems(&self) -> bool {
        self.tasks_with_invalid_links > 0 || self.unreadable_link_files > 0 || !self.link_cycles.is_empty()
    }

    fn failed(project_id: Uuid, project_name: &str, error: String) -> ProjectSeriesHealth {
        ProjectSeriesHealth {
            project_id,
            project_name: project_name.to_string(),
            number_conflicts: vec![],
            prefix_conflicts: vec![],
            tasks_with_invalid_series: 0,
            unreadable_series_files: 0,
            error: Some(error),
            tasks_with_invalid_links: 0,
            unreadable_link_files: 0,
            link_cycles: vec![],
        }
    }
}

pub const PREFIX_HINT: &str = "rename a series with 'tasker series update <id> --prefix ...'";

/// Сводка по сериям и связям всех проектов области (`SeriesReports.Compute`): только проекты, у которых что-то не в порядке.
pub fn compute(ws: &Workspace) -> Vec<ProjectSeriesHealth> {
    let all = match ws.projects().get_all() {
        Ok(all) => all,
        Err(e) => {
            return vec![ProjectSeriesHealth::failed(
                Uuid::nil(),
                "(projects)",
                format!("cannot list projects: {}", e.message()),
            )];
        }
    };
    let mut result = Vec::new();
    for project in all {
        let computed = (|| -> Result<ProjectSeriesHealth> {
            let found = ws.series_health().check(&project.id)?;
            let links = ws.link_health().check(&project.id)?;
            let known: HashMap<Uuid, String> = ws.series().prefixes(&project.id)?;
            let numbers = found
                .number_conflicts
                .iter()
                // Серия, которой нет, — недействительная ссылка; при нечитаемых сериях не понять.
                .filter(|x| known.contains_key(&x.series_id) || found.unreadable_series_files > 0)
                .map(|x| SeriesNumberProblem {
                    reference: format!("{}-{}", label(&known, &x.series_id), x.number),
                    series_id: x.series_id,
                    number: x.number,
                    task_ids: x.task_ids.clone(),
                })
                .collect();
            Ok(ProjectSeriesHealth {
                project_id: project.id,
                project_name: project.name.clone(),
                number_conflicts: numbers,
                prefix_conflicts: found.prefix_conflicts,
                tasks_with_invalid_series: found.tasks_with_invalid_series,
                unreadable_series_files: found.unreadable_series_files,
                error: None,
                tasks_with_invalid_links: links.tasks_with_invalid_links,
                unreadable_link_files: links.unreadable_files,
                link_cycles: links
                    .cycles
                    .iter()
                    .map(|c| LinkCycleProblem {
                        type_id: c.type_id,
                        type_name: c.type_name.clone(),
                        path: c.format(&known),
                        task_ids: c.path.iter().map(|t| t.id).collect(),
                    })
                    .collect(),
            })
        })();
        match computed {
            Ok(item) => {
                if item.has_problems() {
                    result.push(item);
                }
            }
            Err(e) => result.push(ProjectSeriesHealth::failed(
                project.id,
                &project.name,
                format!("cannot check the series: {}", e.message()),
            )),
        }
    }
    result
}

/// Ссылка серии в тексте: префикс или `series <guid>`.
pub fn label(prefixes: &HashMap<Uuid, String>, series_id: &Uuid) -> String {
    prefixes
        .get(series_id)
        .cloned()
        .unwrap_or_else(|| format!("series {}", guid_d(series_id)))
}

/// Строки проблем серий проекта для консоли (без заголовка проекта).
pub fn describe(health: &ProjectSeriesHealth) -> Vec<String> {
    if let Some(error) = &health.error {
        return vec![error.clone()];
    }
    let mut lines = Vec::new();
    for conflict in &health.number_conflicts {
        lines.push(format!(
            "{}: tasks {} (run 'tasker cleanup --resolve-conflicts' or 'tasker series renumber-task')",
            conflict.reference,
            conflict.task_ids.iter().map(guid_d).collect::<Vec<_>>().join(", ")
        ));
    }
    for conflict in &health.prefix_conflicts {
        lines.push(format!(
            "series rename required: series prefix '{}' is used by several series: {}: {PREFIX_HINT}",
            conflict.prefix,
            conflict.series_ids.iter().map(guid_d).collect::<Vec<_>>().join(", ")
        ));
    }
    if health.tasks_with_invalid_series > 0 {
        lines.push(format!(
            "{} task(s) refer to a series that does not exist (run 'tasker cleanup')",
            health.tasks_with_invalid_series
        ));
    }
    if health.unreadable_series_files > 0 {
        lines.push(format!(
            "{} series file(s) cannot be read (merge conflict?): fix them, 'tasker cleanup' skips until then",
            health.unreadable_series_files
        ));
    }
    lines
}

/// Строки проблем связей проекта для консоли.
pub fn describe_links(health: &ProjectSeriesHealth) -> Vec<String> {
    let mut lines = Vec::new();
    if health.tasks_with_invalid_links > 0 {
        lines.push(format!(
            "{} task(s) have a link to a task or link type that does not exist (run 'tasker cleanup')",
            health.tasks_with_invalid_links
        ));
    }
    if health.unreadable_link_files > 0 {
        lines.push(format!(
            "{} task or link type file(s) cannot be read (merge conflict?): fix them, 'tasker cleanup' does not check links until then",
            health.unreadable_link_files
        ));
    }
    for cycle in &health.link_cycles {
        lines.push(cycle_line(cycle));
    }
    lines
}

pub fn cycle_line(cycle: &LinkCycleProblem) -> String {
    format!(
        "Link cycle: {} (link type {}): remove one of the links with 'tasker task unlink' (cleanup does not remove links of a cycle)",
        cycle.path, cycle.type_name
    )
}
