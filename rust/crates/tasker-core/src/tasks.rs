//! Отбор и порядок списков задач (`Tasker.Core.Tasks`: `TaskFilter`, `FieldCondition`, `TaskSortKey` и таблицы мест) и страница
//! (`Page`). Хранилище (индекс) только сравнивает по этим структурам запросом; «места» статусов, типов, серий и значений
//! перечислений раскладывает заранее ядро — здесь же лежат эти чистые функции ([`ranks`], [`status_ranks`]).
use crate::model::{FieldOperator, FieldType, Status, StatusSet, TaskField, TaskItem, TaskType};
use crate::validate::to_lower_invariant;
use std::collections::HashMap;
use uuid::Uuid;

/// Страница списка (`Page` в .NET): пропустить `offset`, взять `limit`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Page {
    pub offset: usize,
    pub limit: usize,
}

impl Page {
    pub const DEFAULT_LIMIT: usize = 50;

    pub fn new(offset: usize, limit: usize) -> Page {
        Page { offset, limit }
    }

    /// Первые `limit` строк.
    pub fn first(limit: usize) -> Page {
        Page { offset: 0, limit }
    }
}

/// Страница с общим числом строк (`ListDto<T>`).
#[derive(Debug, Clone, PartialEq)]
pub struct ListPage<T> {
    pub total_count: usize,
    pub offset: usize,
    pub limit: usize,
    pub data: Vec<T>,
}

/// Имена полей в хранилищах и условиях — без учёта регистра (`FieldNames.Key`).
pub fn field_name_key(name: &str) -> String {
    to_lower_invariant(name)
}

/// `FieldNumbers.Parse`: каноническое значение как число (`double.TryParse(NumberStyles.Float, InvariantCulture)`, конечное);
/// None — не число. Значения int и float в каноническом виде всегда числа.
pub fn field_number(canonical: &str) -> Option<f64> {
    crate::fields::parse_double(canonical.trim()).filter(|n| n.is_finite())
}

/// Условие по полю (`FieldCondition`): полю каталога (`field_id`) и собственным полям задач с тем же именем (ключ
/// [`field_name_key`]) и тем же типом. Какие значения нужны оператору, зависит от него: `=`/`!=` и сравнения — `value`
/// (канонический текст), у int и float ещё `number`; `attached`/`detached` — `type_ids`: типы задач, в которых есть поле каталога.
#[derive(Debug, Clone, PartialEq)]
pub struct FieldCondition {
    /// Имя поля, как его ввёл клиент (регистр не важен).
    pub name: String,
    pub field_type: FieldType,
    pub operator: FieldOperator,
    pub value: Option<String>,
    pub number: Option<f64>,
    /// Поле каталога с этим именем; None — только собственные поля задач.
    pub field_id: Option<Uuid>,
    pub type_ids: Option<Vec<Uuid>>,
}

impl FieldCondition {
    pub fn key(&self) -> String {
        field_name_key(&self.name)
    }

    /// Относится ли запись поля в задаче к условию: то же поле каталога или собственное поле с тем же именем и типом.
    pub fn covers(&self, entry: &TaskField) -> bool {
        match &entry.own {
            None => self.field_id.is_some_and(|id| entry.field_id == id),
            Some(own) => own.field_type == self.field_type && field_name_key(&own.name) == self.key(),
        }
    }

    /// Подходит ли задача (проверка в памяти; индекс выполняет то же запросом).
    pub fn matches(&self, task: &TaskItem) -> bool {
        let entries: Vec<&TaskField> = task.fields.iter().filter(|f| self.covers(f)).collect();
        let values: Vec<&str> = entries.iter().flat_map(|f| f.values.iter().map(String::as_str)).collect();
        let value = self.value.as_deref().unwrap_or("");
        let attached = !entries.is_empty() || self.type_ids.as_ref().is_some_and(|t| t.contains(&task.type_id));
        match self.operator {
            FieldOperator::Equal => values.contains(&value),
            FieldOperator::NotEqual => !values.contains(&value),
            FieldOperator::Greater => values.iter().any(|v| self.holds(v, |c| c > 0)),
            FieldOperator::GreaterOrEqual => values.iter().any(|v| self.holds(v, |c| c >= 0)),
            FieldOperator::Less => values.iter().any(|v| self.holds(v, |c| c < 0)),
            FieldOperator::LessOrEqual => values.iter().any(|v| self.holds(v, |c| c <= 0)),
            FieldOperator::Set => !values.is_empty(),
            FieldOperator::Unset => values.is_empty(),
            FieldOperator::Attached => attached,
            FieldOperator::Detached => !attached,
        }
    }

    // По числу (int, float) или по тексту yyyy-MM-dd (date); значение, которое не число, ничему не больше и не меньше.
    fn holds(&self, value: &str, sign: impl Fn(i32) -> bool) -> bool {
        match self.number {
            None => sign(match value.cmp(self.value.as_deref().unwrap_or("")) {
                std::cmp::Ordering::Less => -1,
                std::cmp::Ordering::Equal => 0,
                std::cmp::Ordering::Greater => 1,
            }),
            Some(number) => field_number(value).is_some_and(|own| {
                sign(match own.partial_cmp(&number) {
                    Some(std::cmp::Ordering::Less) => -1,
                    Some(std::cmp::Ordering::Equal) => 0,
                    _ => 1,
                })
            }),
        }
    }
}

/// По чему упорядочиваются задачи (`SortTarget`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SortTarget {
    /// Позиция статуса в наборе статусов типа задачи ([`TaskSortKey::status_ranks`]).
    Status,
    /// Название типа задачи без учёта регистра ([`TaskSortKey::type_ranks`]).
    Type,
    /// Заголовок без учёта регистра (Unicode).
    Title,
    Created,
    Updated,
    /// Номер в серии: серия с меньшим префиксом, внутри неё — по числу ([`TaskSortKey::series_ranks`]).
    Series,
    /// Поле каталога или собственное поле задач ([`TaskSortKey::field`]).
    Field,
}

/// Встроенные ключи в порядке показа (`TaskSortNames.BuiltinNames`).
pub const BUILTIN_SORT_NAMES: [&str; 6] = ["status", "type", "title", "created", "updated", "series"];

/// Встроенный ключ по имени без учёта регистра (`TaskSortNames.Builtin`); второй элемент — каноническое имя.
pub fn builtin_sort(name: &str) -> Option<(SortTarget, &'static str)> {
    let targets = [
        SortTarget::Status,
        SortTarget::Type,
        SortTarget::Title,
        SortTarget::Created,
        SortTarget::Updated,
        SortTarget::Series,
    ];
    BUILTIN_SORT_NAMES
        .iter()
        .zip(targets)
        .find(|(n, _)| crate::validate::eq_ignore_case(n, name))
        .map(|(n, t)| (t, *n))
}

/// Место значения в порядке: чем меньше `rank`, тем раньше; равные значения (у разных id) делят место (`IdRank`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct IdRank {
    pub id: Uuid,
    pub rank: i64,
}

/// Место статуса у задач типа `type_id` (`StatusRank`): порядок статуса зависит от набора типа.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StatusRank {
    pub type_id: Uuid,
    pub status_id: Uuid,
    pub rank: i64,
}

/// Поле, по которому упорядочиваются задачи (`SortField`): поле каталога и собственные поля с тем же именем и типом.
#[derive(Debug, Clone, PartialEq)]
pub struct SortField {
    pub name: String,
    pub field_type: FieldType,
    pub field_id: Option<Uuid>,
    /// У enum — значения перечисления (канонический текст, Guid в форме D) в порядке перечисления; иначе None.
    pub enum_values: Option<Vec<String>>,
}

impl SortField {
    pub fn key(&self) -> String {
        field_name_key(&self.name)
    }
}

/// Ключ упорядочивания списка задач (`TaskSortKey`). Задачи без значения ключа — всегда в конце, и при возрастании, и при убывании.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskSortKey {
    pub target: SortTarget,
    pub descending: bool,
    pub name: String,
    pub field: Option<SortField>,
    pub status_ranks: Option<Vec<StatusRank>>,
    pub type_ranks: Option<Vec<IdRank>>,
    pub series_ranks: Option<Vec<IdRank>>,
}

impl TaskSortKey {
    pub fn new(target: SortTarget, descending: bool, name: &str) -> TaskSortKey {
        TaskSortKey {
            target,
            descending,
            name: name.to_string(),
            field: None,
            status_ranks: None,
            type_ranks: None,
            series_ranks: None,
        }
    }
}

/// Места (0, 1, 2…) по возрастанию значений (`string.CompareOrdinal` по единицам UTF-16), при равных значениях — одно место
/// (`TaskService.Ranks`). Для типов значение — название в нижнем регистре ([`to_lower_invariant`]), для серий — префикс как есть.
pub fn ranks(items: &[(Uuid, String)]) -> Vec<IdRank> {
    let mut ordered: Vec<&(Uuid, String)> = items.iter().collect();
    ordered.sort_by(|a, b| ordinal(&a.1, &b.1).then_with(|| a.0.cmp(&b.0)));
    let mut result = Vec::with_capacity(ordered.len());
    let mut rank = -1;
    let mut previous: Option<&str> = None;
    for (id, value) in ordered {
        if previous != Some(value.as_str()) {
            rank += 1;
        }
        previous = Some(value);
        result.push(IdRank { id: *id, rank });
    }
    result
}

/// `string.CompareOrdinal`: по единицам UTF-16, не по байтам UTF-8 (знаки вне BMP идут раньше U+E000–U+FFFF).
pub fn ordinal(a: &str, b: &str) -> std::cmp::Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

/// Место статуса у задач каждого типа (`TaskService.StatusRanks`): позиция статуса в наборе типа, затем название набора и
/// статуса в нижнем регистре, затем id набора, статуса и типа. Типы с одним набором получают одинаковые места.
pub fn status_ranks(types: &[TaskType], sets: &[StatusSet], statuses: &[Status]) -> Vec<StatusRank> {
    let names: HashMap<Uuid, &str> = statuses.iter().map(|s| (s.id, s.name.as_str())).collect();
    struct Entry {
        type_id: Uuid,
        status_id: Uuid,
        position: usize,
        set_name: String,
        status_name: String,
        set_id: Uuid,
    }
    let mut entries = Vec::new();
    for task_type in types {
        let Some(set) = sets.iter().find(|s| s.id == task_type.status_set_id) else {
            continue;
        };
        for (position, status_id) in set.status_ids.iter().enumerate() {
            entries.push(Entry {
                type_id: task_type.id,
                status_id: *status_id,
                position,
                set_name: to_lower_invariant(&set.name),
                status_name: to_lower_invariant(names.get(status_id).copied().unwrap_or("")),
                set_id: set.id,
            });
        }
    }
    entries.sort_by(|a, b| {
        a.position
            .cmp(&b.position)
            .then_with(|| ordinal(&a.set_name, &b.set_name))
            .then_with(|| ordinal(&a.status_name, &b.status_name))
            .then_with(|| a.set_id.cmp(&b.set_id))
            .then_with(|| a.status_id.cmp(&b.status_id))
            .then_with(|| a.type_id.cmp(&b.type_id))
    });
    let mut result = Vec::with_capacity(entries.len());
    let mut rank = -1;
    let mut previous: Option<(Uuid, Uuid)> = None;
    for e in entries {
        // Одно место у статуса одного набора; типы на этом же наборе делят его.
        if previous != Some((e.set_id, e.status_id)) {
            rank += 1;
        }
        previous = Some((e.set_id, e.status_id));
        result.push(StatusRank {
            type_id: e.type_id,
            status_id: e.status_id,
            rank,
        });
    }
    result
}

/// Фильтр задач (`TaskFilter`): None — без ограничения, пустой список — ни одна задача не подходит.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct TaskFilter {
    pub type_ids: Option<Vec<Uuid>>,
    pub status_ids: Option<Vec<Uuid>>,
    /// Задачи, у которых есть номер хотя бы в одной из этих серий.
    pub series_ids: Option<Vec<Uuid>>,
    /// Только задачи с этими id (пересечение с остальными условиями).
    pub ids: Option<Vec<Uuid>>,
    /// Задачи с исходящей связью хотя бы одного из этих типов.
    pub link_type_ids: Option<Vec<Uuid>>,
    /// Задачи, у которых записано хотя бы одно из этих полей.
    pub field_ids: Option<Vec<Uuid>>,
    /// Задачи с собственным полем с одним из этих перечислений.
    pub enum_ids: Option<Vec<Uuid>>,
    /// Все условия по полям (И).
    pub field_values: Option<Vec<FieldCondition>>,
    /// Порядок списка; None — по времени создания, затем по id. Условием отбора не является.
    pub sort: Option<Vec<TaskSortKey>>,
}

impl TaskFilter {
    /// `TaskFilter.Of`: значения одного параметра — «ИЛИ», разные — «И»; пустой параметр не ограничивает; повторы убираются;
    /// ничего не задано — None.
    pub fn of(type_ids: Option<&[Uuid]>, status_ids: Option<&[Uuid]>, series_ids: Option<&[Uuid]>) -> Option<TaskFilter> {
        fn distinct(ids: Option<&[Uuid]>) -> Option<Vec<Uuid>> {
            let mut result: Vec<Uuid> = Vec::new();
            for id in ids? {
                if !result.contains(id) {
                    result.push(*id);
                }
            }
            (!result.is_empty()).then_some(result)
        }
        let (types, statuses, series) = (distinct(type_ids), distinct(status_ids), distinct(series_ids));
        if types.is_none() && statuses.is_none() && series.is_none() {
            return None;
        }
        Some(TaskFilter {
            type_ids: types,
            status_ids: statuses,
            series_ids: series,
            ..TaskFilter::default()
        })
    }

    pub fn matches(&self, task: &TaskItem) -> bool {
        self.ids.as_ref().is_none_or(|ids| ids.contains(&task.id))
            && self.type_ids.as_ref().is_none_or(|ids| ids.contains(&task.type_id))
            && self.status_ids.as_ref().is_none_or(|ids| ids.contains(&task.status_id))
            && self
                .series_ids
                .as_ref()
                .is_none_or(|ids| task.series_numbers.iter().any(|s| ids.contains(&s.series_id)))
            && self
                .link_type_ids
                .as_ref()
                .is_none_or(|ids| task.links.iter().any(|l| ids.contains(&l.type_id)))
            && self
                .field_ids
                .as_ref()
                .is_none_or(|ids| task.fields.iter().any(|f| ids.contains(&f.field_id)))
            && self.enum_ids.as_ref().is_none_or(|ids| {
                task.fields
                    .iter()
                    .any(|f| f.own.as_ref().and_then(|o| o.enum_id).is_some_and(|e| ids.contains(&e)))
            })
            && self.field_values.as_ref().is_none_or(|c| c.iter().all(|x| x.matches(task)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn id(n: u8) -> Uuid {
        Uuid::from_bytes([n; 16])
    }

    #[test]
    fn ranks_share_a_place_for_equal_values_and_compare_in_utf16() {
        let r = ranks(&[
            (id(1), "b".into()),
            (id(2), "a".into()),
            (id(3), "a".into()),
            (id(4), "\u{10000}".into()),
            (id(5), "\u{FFFD}".into()),
        ]);
        let places: Vec<(u8, i64)> = r.iter().map(|x| (x.id.as_bytes()[0], x.rank)).collect();
        assert_eq!(places, vec![(2, 0), (3, 0), (1, 1), (4, 2), (5, 3)]);
    }

    #[test]
    fn status_ranks_follow_position_then_set_and_status_names() {
        let (s1, s2, s3) = (id(1), id(2), id(3));
        let statuses = vec![
            Status {
                id: s1,
                project_id: id(9),
                name: "Backlog".into(),
                color: String::new(),
                description: String::new(),
                version: String::new(),
            },
            Status {
                id: s2,
                project_id: id(9),
                name: "Done".into(),
                color: String::new(),
                description: String::new(),
                version: String::new(),
            },
            Status {
                id: s3,
                project_id: id(9),
                name: "Wait".into(),
                color: String::new(),
                description: String::new(),
                version: String::new(),
            },
        ];
        let sets = vec![
            StatusSet {
                id: id(10),
                project_id: id(9),
                name: "Main".into(),
                status_ids: vec![s1, s2],
                version: String::new(),
            },
            StatusSet {
                id: id(11),
                project_id: id(9),
                name: "Extended".into(),
                status_ids: vec![s1, s3, s2],
                version: String::new(),
            },
        ];
        let types = vec![
            TaskType {
                id: id(20),
                project_id: id(9),
                name: "Bug".into(),
                description: String::new(),
                status_set_id: id(10),
                fields: vec![],
                version: String::new(),
            },
            TaskType {
                id: id(21),
                project_id: id(9),
                name: "Feature".into(),
                description: String::new(),
                status_set_id: id(11),
                fields: vec![],
                version: String::new(),
            },
            TaskType {
                id: id(22),
                project_id: id(9),
                name: "Task".into(),
                description: String::new(),
                status_set_id: id(10),
                fields: vec![],
                version: String::new(),
            },
            TaskType {
                id: id(23),
                project_id: id(9),
                name: "Orphan".into(),
                description: String::new(),
                status_set_id: id(12),
                fields: vec![],
                version: String::new(),
            },
        ];
        let r = status_ranks(&types, &sets, &statuses);
        let find = |t: u8, s: u8| r.iter().find(|x| x.type_id == id(t) && x.status_id == id(s)).unwrap().rank;
        // Позиция 0: extended/backlog (0), main/backlog (1); позиция 1: extended/wait (2), main/done (3); позиция 2: extended/done (4).
        assert_eq!((find(21, 1), find(20, 1), find(22, 1)), (0, 1, 1));
        assert_eq!((find(21, 3), find(20, 2)), (2, 3));
        assert_eq!(find(21, 2), 4);
        assert!(!r.iter().any(|x| x.type_id == id(23)));
    }

    #[test]
    fn field_numbers_and_name_keys() {
        assert_eq!(field_number("7"), Some(7.0));
        assert_eq!(field_number("-0.25"), Some(-0.25));
        assert_eq!(field_number("1E+15"), Some(1e15));
        assert_eq!(field_number("alpha"), None);
        assert_eq!(field_number("2026-01-01"), None);
        assert_eq!(field_name_key("Ожидание"), "ожидание");
        assert_eq!(builtin_sort("UPDATED"), Some((SortTarget::Updated, "updated")));
        assert_eq!(builtin_sort("estimate"), None);
    }

    #[test]
    fn filter_of_drops_empty_and_repeated_ids() {
        assert_eq!(TaskFilter::of(None, Some(&[]), None), None);
        let f = TaskFilter::of(Some(&[id(1), id(1), id(2)]), None, None).unwrap();
        assert_eq!(f.type_ids, Some(vec![id(1), id(2)]));
        assert_eq!(f.status_ids, None);
    }
}
