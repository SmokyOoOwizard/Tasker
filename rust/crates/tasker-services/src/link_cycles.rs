//! Поиск циклов в связях (`LinkCycles` в .NET): перед добавлением связи (BFS по уровням от цели, один запрос индекса на уровень) и по
//! всему проекту после слияния git (компоненты сильной связности Тарьяна без рекурсии, по одному кратчайшему циклу на компоненту).
use crate::Workspace;
use crate::error::{Error, Result};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};
use tasker_core::ShortId;
use tasker_core::model::{LinkType, TaskItem, TaskSeriesNumber};
use tasker_files::index::LinkEdge;
use uuid::Uuid;

/// Сколько задач обход готов посетить: защита от огромных графов.
pub const MAX_VISITED: usize = 20_000;

/// Сколько циклов проект показывает (по одному на связный кусок графа).
pub const MAX_REPORTED: usize = 50;

/// Задача в цикле — достаточно, чтобы показать её человеку.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkCycleTask {
    pub id: Uuid,
    pub title: String,
    pub series_numbers: Vec<TaskSeriesNumber>,
}

/// Цикл из связей одного типа: путь замкнут — первая и последняя задача одна и та же.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkCycle {
    pub type_id: Uuid,
    pub type_name: String,
    pub path: Vec<LinkCycleTask>,
}

impl LinkCycle {
    /// `TSK-1 → TSK-2 → TSK-1`; у задачи без номера — короткий id.
    pub fn format(&self, prefixes: &HashMap<Uuid, String>) -> String {
        self.path
            .iter()
            .map(|x| reference(&x.id, &x.series_numbers, prefixes))
            .collect::<Vec<_>>()
            .join(" → ")
    }
}

/// Задача для показа: первая действительная ссылка (по префиксу, затем по id серии), иначе короткий id.
pub fn reference(id: &Uuid, numbers: &[TaskSeriesNumber], prefixes: &HashMap<Uuid, String>) -> String {
    let mut valid: Vec<&TaskSeriesNumber> = numbers.iter().filter(|x| prefixes.contains_key(&x.series_id)).collect();
    valid.sort_by(|a, b| {
        tasker_core::tasks::ordinal(&prefixes[&a.series_id], &prefixes[&b.series_id]).then_with(|| a.series_id.cmp(&b.series_id))
    });
    match valid.first() {
        None => ShortId::of(id),
        Some(x) => format!("{}-{}", prefixes[&x.series_id], x.number),
    }
}

/// Путь по связям типов `type_ids` от `from` до `to` (оба конца включены), кратчайший; None — пути нет. Больше [`MAX_VISITED`]
/// достижимых задач — ошибка.
pub fn find_path(ws: &Workspace, project_id: &Uuid, type_ids: &[Uuid], from: Uuid, to: Uuid) -> Result<Option<Vec<Uuid>>> {
    let mut parent: HashMap<Uuid, Uuid> = HashMap::from([(from, from)]);
    let mut level = vec![from];
    while !level.is_empty() {
        let mut next = Vec::new();
        let mut targets: HashMap<Uuid, Vec<Uuid>> = HashMap::new();
        for type_id in type_ids {
            for (source, list) in ws.index().link_targets(project_id, type_id, &level)? {
                targets.entry(source).or_default().extend(list);
            }
        }
        for source in &level {
            let Some(list) = targets.get(source) else {
                continue;
            };
            for target in list {
                if parent.contains_key(target) {
                    continue;
                }
                parent.insert(*target, *source);
                if *target == to {
                    return Ok(Some(unwind(&parent, from, to)));
                }
                next.push(*target);
            }
        }
        if parent.len() > MAX_VISITED {
            return Err(Error::validation(format!(
                "Cycle check: more than {MAX_VISITED} tasks are reachable through links of this type, cannot verify that the link does not close a cycle"
            )));
        }
        level = next;
    }
    Ok(None)
}

fn unwind(parent: &HashMap<Uuid, Uuid>, from: Uuid, to: Uuid) -> Vec<Uuid> {
    let mut path = vec![to];
    let mut current = to;
    while current != from {
        current = parent[&current];
        path.push(current);
    }
    path.reverse();
    path
}

/// Циклы проекта по типам, которые циклов не допускают: по одному кратчайшему на компоненту, не больше [`MAX_REPORTED`]. Только чтение.
pub fn find(ws: &Workspace, project_id: &Uuid, types: &[LinkType]) -> Result<Vec<LinkCycle>> {
    let mut found: Vec<LinkCycle> = Vec::new();
    let mut ordered: Vec<&LinkType> = types.iter().filter(|x| !x.allow_cycles && !x.is_symmetric()).collect();
    ordered.sort_by(|a, b| tasker_core::tasks::ordinal(&a.name, &b.name).then_with(|| a.id.cmp(&b.id)));
    for link_type in ordered {
        let edges = ws.index().link_edges(project_id, &link_type.id)?;
        for path in cycles_of(&edges) {
            if found.len() >= MAX_REPORTED {
                return Ok(found);
            }
            let mut loaded: Vec<Option<TaskItem>> = Vec::new();
            for id in &path {
                loaded.push(ws.get_by_id::<TaskItem>(project_id, id)?);
            }
            // Цикл не имеет начала; показываем с самой ранней задачи (по созданию, затем по наименьшему номеру в серии, затем по id).
            let nodes = path.len() - 1;
            let mut order: Vec<usize> = (0..nodes).collect();
            order.sort_by(|&i, &j| {
                let created = |k: usize| loaded[k].as_ref().map(|t| t.created_at.unix_ticks()).unwrap_or(i64::MAX);
                let number = |k: usize| {
                    loaded[k]
                        .as_ref()
                        .and_then(|t| t.series_numbers.iter().map(|x| x.number).min())
                        .unwrap_or(i32::MAX)
                };
                created(i)
                    .cmp(&created(j))
                    .then_with(|| number(i).cmp(&number(j)))
                    .then_with(|| path[i].cmp(&path[j]))
            });
            let first = order[0];
            let mut shown = Vec::new();
            for i in 0..=nodes {
                let index = (first + i) % nodes;
                shown.push(match &loaded[index] {
                    None => LinkCycleTask {
                        id: path[index],
                        title: String::new(),
                        series_numbers: vec![],
                    },
                    Some(task) => LinkCycleTask {
                        id: task.id,
                        title: task.title.clone(),
                        series_numbers: task.series_numbers.clone(),
                    },
                });
            }
            found.push(LinkCycle {
                type_id: link_type.id,
                type_name: link_type.name.clone(),
                path: shown,
            });
        }
    }
    Ok(found)
}

/// Циклы графа (замкнутые пути): по одному на компоненту сильной связности (и на петлю); детерминированно.
pub fn cycles_of(edges: &[LinkEdge]) -> Vec<Vec<Uuid>> {
    let mut graph: BTreeMap<Uuid, BTreeSet<Uuid>> = BTreeMap::new();
    for edge in edges {
        graph.entry(edge.source_id).or_default().insert(edge.target_id);
    }
    // Цикл целиком состоит из задач-источников: цели без исходящих связей из рассмотрения выпадают.
    let sources: HashSet<Uuid> = graph.keys().copied().collect();
    for set in graph.values_mut() {
        set.retain(|x| sources.contains(x));
    }

    let mut result: Vec<Vec<Uuid>> = Vec::new();
    for component in strongly_connected(&graph) {
        if component.len() == 1 && !graph[&component[0]].contains(&component[0]) {
            continue;
        }
        let inside: HashSet<Uuid> = component.iter().copied().collect();
        let start = *component.iter().min().unwrap();
        let mut parent: HashMap<Uuid, Uuid> = HashMap::new();
        let mut queue: VecDeque<Uuid> = VecDeque::from([start]);
        let mut cycle: Option<Vec<Uuid>> = None;
        while let Some(current) = queue.pop_front() {
            if cycle.is_some() {
                break;
            }
            for target in graph[&current].iter().filter(|x| inside.contains(x)) {
                if *target == start {
                    let mut chain = vec![current];
                    let mut x = current;
                    while x != start {
                        x = parent[&x];
                        chain.push(x);
                    }
                    chain.reverse();
                    chain.push(start);
                    cycle = Some(chain);
                    break;
                }
                if let std::collections::hash_map::Entry::Vacant(e) = parent.entry(*target) {
                    e.insert(current);
                    queue.push_back(*target);
                }
            }
        }
        if let Some(cycle) = cycle {
            result.push(cycle);
        }
    }
    result.sort_by(|a, b| a[0].cmp(&b[0]));
    result
}

/// Компоненты сильной связности (Tarjan, без рекурсии).
fn strongly_connected(graph: &BTreeMap<Uuid, BTreeSet<Uuid>>) -> Vec<Vec<Uuid>> {
    let mut index: HashMap<Uuid, usize> = HashMap::new();
    let mut low: HashMap<Uuid, usize> = HashMap::new();
    let mut on_stack: HashSet<Uuid> = HashSet::new();
    let mut stack: Vec<Uuid> = Vec::new();
    let mut result: Vec<Vec<Uuid>> = Vec::new();
    let mut counter = 0;

    for root in graph.keys() {
        if index.contains_key(root) {
            continue;
        }
        let mut work: Vec<(Uuid, Vec<Uuid>, usize)> = Vec::new();
        index.insert(*root, counter);
        low.insert(*root, counter);
        counter += 1;
        stack.push(*root);
        on_stack.insert(*root);
        work.push((*root, graph[root].iter().copied().collect(), 0));

        while let Some((node, targets, position)) = work.last_mut() {
            if *position < targets.len() {
                let target = targets[*position];
                *position += 1;
                if let std::collections::hash_map::Entry::Vacant(e) = index.entry(target) {
                    e.insert(counter);
                    low.insert(target, counter);
                    counter += 1;
                    stack.push(target);
                    on_stack.insert(target);
                    work.push((target, graph[&target].iter().copied().collect(), 0));
                } else if on_stack.contains(&target) {
                    let value = low[node].min(index[&target]);
                    low.insert(*node, value);
                }
                continue;
            }
            let node = *node;
            work.pop();
            if let Some((parent_node, _, _)) = work.last() {
                let value = low[parent_node].min(low[&node]);
                low.insert(*parent_node, value);
            }
            if low[&node] == index[&node] {
                let mut component = Vec::new();
                loop {
                    let member = stack.pop().unwrap();
                    on_stack.remove(&member);
                    component.push(member);
                    if member == node {
                        break;
                    }
                }
                result.push(component);
            }
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn id(n: u8) -> Uuid {
        Uuid::from_bytes([n; 16])
    }

    fn edge(a: u8, b: u8) -> LinkEdge {
        LinkEdge {
            source_id: id(a),
            target_id: id(b),
        }
    }

    #[test]
    fn cycles_are_one_per_component_and_shortest() {
        let cycles = cycles_of(&[edge(1, 2), edge(2, 1), edge(2, 3), edge(3, 4), edge(4, 2), edge(5, 6), edge(7, 7)]);
        assert_eq!(cycles, vec![vec![id(1), id(2), id(1)], vec![id(7), id(7)]]);
        assert!(cycles_of(&[edge(1, 2), edge(2, 3)]).is_empty());
    }
}
