//! Иерархические связи проекта в памяти (`TaskHierarchy` в .NET): родитель → дети и обратно (граф без циклов, у задачи может быть
//! несколько родителей). Строится из связей иерархических типов; задачи для показа загружаются отдельно.
use std::collections::{HashMap, HashSet};
use tasker_files::index::LinkEdge;
use uuid::Uuid;

/// Сколько уровней дерева показывает список (верхний — первый).
pub const MAX_DEPTH: usize = 10;

/// Сколько строк страницы допускает показ повторов с поддеревом; после — повторы остаются строкой без поддерева.
pub const MAX_ROWS: usize = 5000;

#[derive(Debug, Clone, Default)]
pub struct TaskHierarchy {
    children: HashMap<Uuid, Vec<Uuid>>,
    parents: HashMap<Uuid, Vec<Uuid>>,
}

/// Строка дерева: задача, глубина, повтор.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Row {
    pub id: Uuid,
    pub depth: usize,
    pub repeated: bool,
}

impl TaskHierarchy {
    pub fn new(edges: &[LinkEdge]) -> TaskHierarchy {
        let mut distinct: Vec<LinkEdge> = Vec::new();
        for edge in edges {
            if edge.source_id != edge.target_id && !distinct.contains(edge) {
                distinct.push(*edge);
            }
        }
        let mut children: HashMap<Uuid, Vec<Uuid>> = HashMap::new();
        let mut parents: HashMap<Uuid, Vec<Uuid>> = HashMap::new();
        for edge in &distinct {
            let kids = children.entry(edge.source_id).or_default();
            if !kids.contains(&edge.target_id) {
                kids.push(edge.target_id);
            }
            let folks = parents.entry(edge.target_id).or_default();
            if !folks.contains(&edge.source_id) {
                folks.push(edge.source_id);
            }
        }
        TaskHierarchy { children, parents }
    }

    pub fn is_empty(&self) -> bool {
        self.children.is_empty()
    }

    pub fn parents_of(&self, id: &Uuid) -> Vec<Uuid> {
        self.parents.get(id).cloned().unwrap_or_default()
    }

    pub fn child_count(&self, id: &Uuid) -> usize {
        self.children.get(id).map(Vec::len).unwrap_or(0)
    }

    /// Верхний уровень результата `ordered` (id найденных задач в порядке списка): задачи без родителя среди найденных; задачи,
    /// замкнутые в цикл родителей, добираются по порядку списка.
    pub fn top_level(&self, ordered: &[Uuid]) -> Vec<Uuid> {
        let found: HashSet<Uuid> = ordered.iter().copied().collect();
        let mut top: HashSet<Uuid> = ordered
            .iter()
            .filter(|id| !self.parents_of(id).iter().any(|p| found.contains(p)))
            .copied()
            .collect();

        let mut reached: HashSet<Uuid> = HashSet::new();
        for id in ordered.iter().filter(|id| top.contains(id)) {
            self.reach(*id, &found, &mut reached);
        }
        for id in ordered {
            if reached.contains(id) {
                continue;
            }
            top.insert(*id);
            self.reach(*id, &found, &mut reached);
        }
        ordered.iter().filter(|id| top.contains(id)).copied().collect()
    }

    fn reach(&self, start: Uuid, found: &HashSet<Uuid>, reached: &mut HashSet<Uuid>) {
        let mut stack = vec![start];
        while let Some(id) = stack.pop() {
            if !reached.insert(id) {
                continue;
            }
            for child in self.children.get(&id).map(|v| v.as_slice()).unwrap_or(&[]) {
                if found.contains(child) && !reached.contains(child) {
                    stack.push(*child);
                }
            }
        }
    }

    /// Строки деревьев верхнего уровня `roots`: задача, под ней её дочерние среди найденных (по `position`), до [`MAX_DEPTH`] уровней;
    /// повторы помечены, задача не повторяется внутри своей ветки, повторы после [`MAX_ROWS`] строк идут без поддерева.
    pub fn rows(&self, roots: &[Uuid], position: &HashMap<Uuid, usize>) -> Vec<Row> {
        let mut rows: Vec<Row> = Vec::new();
        let mut seen: HashSet<Uuid> = HashSet::new();
        let mut path: HashSet<Uuid> = HashSet::new();
        for root in roots {
            self.walk(*root, 0, position, &mut rows, &mut seen, &mut path);
        }
        rows
    }

    #[allow(clippy::too_many_arguments)]
    fn walk(
        &self,
        id: Uuid,
        depth: usize,
        position: &HashMap<Uuid, usize>,
        rows: &mut Vec<Row>,
        seen: &mut HashSet<Uuid>,
        path: &mut HashSet<Uuid>,
    ) {
        let repeated = !seen.insert(id);
        rows.push(Row { id, depth, repeated });
        if depth + 1 >= MAX_DEPTH || (repeated && rows.len() >= MAX_ROWS) {
            return;
        }
        let mut kids: Vec<Uuid> = self
            .children
            .get(&id)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
            .iter()
            .filter(|x| position.contains_key(x) && !path.contains(x) && **x != id)
            .copied()
            .collect();
        if kids.is_empty() {
            return;
        }
        kids.sort_by_key(|x| position[x]);
        path.insert(id);
        for kid in kids {
            self.walk(kid, depth + 1, position, rows, seen, path);
        }
        path.remove(&id);
    }
}
