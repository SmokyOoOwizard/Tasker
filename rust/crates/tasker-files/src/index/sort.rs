//! Порядок списка задач (`TaskFilter.sort`) в SQL индекса (`WorkspaceIndex.Sort.cs`): ключ — выражение по `files` и таблицам
//! значений, в `ORDER BY` без загрузки задач. У каждого ключа задачи без значения (`NULL`) идут в конце — и при возрастании, и
//! при убывании; при равных ключах — порядок по умолчанию (`sort_num, id`). Идентификаторы вставляются литералами: это Guid в
//! форме D и имена в нижнем регистре, они проходят через [`literal`].
use super::sync::field_type_code;
use tasker_core::ids::guid_d;
use tasker_core::model::FieldType;
use tasker_core::tasks::{SortField, SortTarget, TaskSortKey};
use uuid::Uuid;

pub(super) const DEFAULT_ORDER: &str = "sort_num, id";

/// Номер серии в одном ключе: место серии в старшей части, номер — в младшей (чтобы взять наименьший за один `min`).
const SERIES_NUMBER_RANGE: i64 = 4_294_967_296;

/// `ORDER BY` для задач по ключам; None или пусто — порядок по умолчанию.
pub(super) fn order_by(keys: Option<&[TaskSortKey]>) -> String {
    let Some(keys) = keys.filter(|k| !k.is_empty()) else {
        return format!(" ORDER BY {DEFAULT_ORDER}");
    };
    let mut order = String::from(" ORDER BY ");
    for key in keys {
        let expression = sort_expression(key);
        let direction = if key.descending { "DESC" } else { "ASC" };
        // NULL (нет значения) — в конце при любом направлении: сначала «есть ли значение», потом само значение.
        if can_be_null(key) {
            order.push_str(&format!("({expression}) IS NULL, "));
        }
        order.push_str(&format!("({expression}) {direction}, "));
    }
    order.push_str(DEFAULT_ORDER);
    order
}

fn can_be_null(key: &TaskSortKey) -> bool {
    !matches!(key.target, SortTarget::Title | SortTarget::Created | SortTarget::Updated)
}

fn sort_expression(key: &TaskSortKey) -> String {
    match key.target {
        SortTarget::Title => "sort_text".into(),
        SortTarget::Created => "sort_num".into(),
        SortTarget::Updated => "sort_updated".into(),
        SortTarget::Type => case(
            Some("type_id"),
            key.type_ranks.as_deref().unwrap_or(&[]),
            |x| literal_id(&x.id),
            |x| x.rank,
        ),
        SortTarget::Status => case(
            None,
            key.status_ranks.as_deref().unwrap_or(&[]),
            |x| format!("type_id = {} AND status_id = {}", literal_id(&x.type_id), literal_id(&x.status_id)),
            |x| x.rank,
        ),
        SortTarget::Series => format!(
            "SELECT min(({}) * {SERIES_NUMBER_RANGE} + s.number) FROM task_series s WHERE s.path = files.path",
            case(
                Some("s.series_id"),
                key.series_ranks.as_deref().unwrap_or(&[]),
                |x| literal_id(&x.id),
                |x| x.rank
            )
        ),
        SortTarget::Field => field_expression(key.field.as_ref().expect("a field sort key carries its field")),
    }
}

/// Первое значение поля задачи (в порядке записи — `rowid`): число (int, float), текст даты и логического, текст строки без
/// регистра, у enum — место значения в перечислении. Строки поля — поле каталога или собственное поле с тем же именем и типом,
/// как в условии фильтра.
fn field_expression(field: &SortField) -> String {
    let column = match field.field_type {
        FieldType::Int | FieldType::Float => "v.number".to_string(),
        FieldType::String => "lower(v.value)".to_string(),
        FieldType::Enum => {
            let ranks: Vec<(String, i64)> = field
                .enum_values
                .as_deref()
                .unwrap_or(&[])
                .iter()
                .enumerate()
                .map(|(i, v)| (v.clone(), i as i64))
                .collect();
            case(Some("v.value"), &ranks, |x| literal(&x.0), |x| x.1)
        }
        _ => "v.value".to_string(),
    };
    let mut row = format!(
        "(v.own_name = {} AND v.own_type = {})",
        literal(&field.key()),
        field_type_code(field.field_type)
    );
    if let Some(catalog) = &field.field_id {
        row = format!("(v.field_id = {} OR {row})", literal_id(catalog));
    }
    format!("SELECT {column} FROM task_field_values v WHERE v.path = files.path AND {row} ORDER BY v.rowid LIMIT 1")
}

/// `CASE` по таблице мест: `subject` — сравниваемый столбец (`CASE столбец WHEN значение THEN место`) или None — тогда `when`
/// даёт условие целиком. Нет в таблице — `NULL` (последним).
fn case<T>(subject: Option<&str>, ranks: &[T], when: impl Fn(&T) -> String, rank: impl Fn(&T) -> i64) -> String {
    if ranks.is_empty() {
        return "NULL".into();
    }
    let mut text = String::from("CASE");
    if let Some(subject) = subject {
        text.push(' ');
        text.push_str(subject);
    }
    for item in ranks {
        text.push_str(&format!(" WHEN {} THEN {}", when(item), rank(item)));
    }
    text.push_str(" END");
    text
}

pub(super) fn literal_id(id: &Uuid) -> String {
    literal(&guid_d(id))
}

pub(super) fn literal(text: &str) -> String {
    format!("'{}'", text.replace('\'', "''"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use tasker_core::tasks::IdRank;

    #[test]
    fn order_by_matches_dotnet_shape() {
        assert_eq!(order_by(None), " ORDER BY sort_num, id");
        let mut key = TaskSortKey::new(SortTarget::Type, true, "type");
        key.type_ranks = Some(vec![IdRank { id: Uuid::nil(), rank: 0 }]);
        assert_eq!(
            order_by(Some(&[TaskSortKey::new(SortTarget::Title, false, "title"), key])),
            " ORDER BY (sort_text) ASC, (CASE type_id WHEN '00000000-0000-0000-0000-000000000000' THEN 0 END) IS NULL, \
             (CASE type_id WHEN '00000000-0000-0000-0000-000000000000' THEN 0 END) DESC, sort_num, id"
        );
        let mut field = TaskSortKey::new(SortTarget::Field, false, "O'Hara");
        field.field = Some(SortField {
            name: "O'Hara".into(),
            field_type: FieldType::String,
            field_id: None,
            enum_values: None,
        });
        assert_eq!(
            order_by(Some(&[field])),
            " ORDER BY (SELECT lower(v.value) FROM task_field_values v WHERE v.path = files.path AND (v.own_name = 'o''hara' AND v.own_type = 0) \
             ORDER BY v.rowid LIMIT 1) IS NULL, (SELECT lower(v.value) FROM task_field_values v WHERE v.path = files.path AND \
             (v.own_name = 'o''hara' AND v.own_type = 0) ORDER BY v.rowid LIMIT 1) ASC, sort_num, id"
        );
    }
}
