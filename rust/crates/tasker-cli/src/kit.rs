//! Мелочи для описания команд (`Kit` в .NET): карточка `get` («поле: значение» с выравниванием), «Имя (id)», итоги каскадов,
//! `Deleted …`, проверка «хоть что-то меняется», чтение параметров clap (параметр не указан — `None`, как у System.CommandLine),
//! страницы списков (`--offset`/`--limit`/`--all`).
use crate::errors::{CliError, Result};
use clap::ArgMatches;
use clap::parser::ValueSource;
use tasker_core::ids::guid_d;
use tasker_core::tasks::{ListPage, Page};
use tasker_services::preview::{TaskTreeItem, TaskTreeList};
use uuid::Uuid;

/// `Page.MaxLimit` и `Page.DefaultLimit`.
pub const MAX_LIMIT: usize = 200;
pub const DEFAULT_LIMIT: usize = 50;

/// `Page.Of`: отрицательное смещение — 0, предел — в 1..=200.
pub fn page_of(offset: i64, limit: i64) -> Page {
    Page::new(offset.max(0) as usize, limit.clamp(1, MAX_LIMIT as i64) as usize)
}

/// Вывод `get`: строки «поле: значение», пустые значения пропускаются; имя дополняется до 13 знаков.
pub fn fields(fields: &[(&str, Option<String>)]) -> String {
    fields
        .iter()
        .filter_map(|(name, value)| value.as_ref().map(|v| format!("{:<13} {}", format!("{name}:"), v)))
        .collect::<Vec<_>>()
        .join("\n")
}

/// «Имя (id)» для читаемого вывода ссылок; неизвестный id — как есть.
pub fn named<T>(items: &[T], id: &Uuid, id_of: impl Fn(&T) -> Uuid, name_of: impl Fn(&T) -> &str) -> String {
    match items.iter().find(|x| id_of(x) == *id) {
        Some(item) => format!("{} ({})", name_of(item), guid_d(id)),
        None => guid_d(id),
    }
}

/// «1 task» / «12 tasks» — для итога каскадных правок.
pub fn tasks(count: usize) -> String {
    if count == 1 {
        "1 task".to_string()
    } else {
        format!("{count} tasks")
    }
}

pub fn deleted(what: &str, name: &str, id: &Uuid) -> String {
    format!("Deleted {what} '{name}' {}", guid_d(id))
}

/// Описание в выводе `get`: после полей, отдельным абзацем; пустого нет.
pub fn with_description(text: String, description: &str) -> String {
    if description.is_empty() {
        text
    } else {
        format!("{text}\n\n{description}")
    }
}

/// `C# bool.ToString()`: `True`/`False`.
pub fn bool_text(value: bool) -> String {
    if value { "True".to_string() } else { "False".to_string() }
}

/// Указан ли параметр в строке (не значение по умолчанию и не отсутствие).
pub fn given(leaf: &ArgMatches, id: &str) -> bool {
    matches!(leaf.value_source(id), Some(ValueSource::CommandLine))
}

/// Хотя бы один из параметров изменения должен быть указан.
pub fn require_change(leaf: &ArgMatches, options: &[&str]) -> Result<()> {
    if options.iter().any(|id| given(leaf, id)) {
        return Ok(());
    }
    let names: Vec<String> = options.iter().map(|id| format!("--{id}")).collect();
    Err(CliError::new(format!(
        "Nothing to change: pass at least one of {}",
        names.join(", ")
    )))
}

/// Значение параметра с одним значением; None — не указан.
pub fn text(leaf: &ArgMatches, id: &str) -> Option<String> {
    if !given(leaf, id) {
        return None;
    }
    leaf.get_one::<String>(id).cloned()
}

/// Обязательный аргумент или параметр: его наличие уже проверено разбором.
pub fn required(leaf: &ArgMatches, id: &str) -> String {
    leaf.get_one::<String>(id).cloned().unwrap_or_default()
}

/// Значения повторяемого параметра; None — параметр не указан (System.CommandLine в этом случае отдаёт пустой массив).
pub fn values(leaf: &ArgMatches, id: &str) -> Option<Vec<String>> {
    if !given(leaf, id) {
        return None;
    }
    Some(leaf.get_many::<String>(id).map(|v| v.cloned().collect()).unwrap_or_default())
}

/// Значения повторяемого параметра, пусто — не указан (`parse.GetValue(option)` у `Option<string[]>`).
pub fn values_or_empty(leaf: &ArgMatches, id: &str) -> Vec<String> {
    values(leaf, id).unwrap_or_default()
}

pub fn flag(leaf: &ArgMatches, id: &str) -> bool {
    leaf.get_flag(id)
}

/// `Option<bool?>`: `--x`, `--x true`, `--x false`; не указан — None.
pub fn optional_bool(leaf: &ArgMatches, id: &str) -> Option<bool> {
    text(leaf, id).map(|v| v.eq_ignore_ascii_case("true"))
}

/// Число из параметра (проверено разбором); не указан — `default`.
pub fn int(leaf: &ArgMatches, id: &str, default: i64) -> i64 {
    text(leaf, id).and_then(|t| crate::terminal::parse_int(&t)).unwrap_or(default)
}

/// Число из параметра; не указан — None.
pub fn int_opt(leaf: &ArgMatches, id: &str) -> Option<i64> {
    text(leaf, id).and_then(|t| crate::terminal::parse_int(&t))
}

/// `--description-length` (по умолчанию -1, полный текст).
pub fn description_length(leaf: &ArgMatches) -> i32 {
    int(leaf, "description-length", -1) as i32
}

/// Параметры списка: `--offset` и `--limit` (страница) или `--all` (всё без ограничения по количеству).
pub struct Paging;

impl Paging {
    /// Страница из `--offset`/`--limit` или, с `--all`, все элементы одним списком (в `--json` — одной страницей: `offset` 0,
    /// `limit` и `totalCount` равны числу элементов).
    pub fn load<T>(leaf: &ArgMatches, mut load_page: impl FnMut(Page) -> Result<ListPage<T>>) -> Result<ListPage<T>> {
        if !flag(leaf, "all") {
            return load_page(page_of(int(leaf, "offset", 0), int(leaf, "limit", DEFAULT_LIMIT as i64)));
        }
        if given(leaf, "offset") || given(leaf, "limit") {
            return Err(CliError::new("Use either --all or --offset/--limit, not both"));
        }
        let mut items: Vec<T> = Vec::new();
        loop {
            let page = load_page(Page::new(items.len(), MAX_LIMIT))?;
            let got = page.data.len();
            items.extend(page.data);
            if got == 0 || items.len() >= page.total_count {
                break;
            }
        }
        Ok(ListPage {
            total_count: items.len(),
            offset: 0,
            limit: items.len(),
            data: items,
        })
    }

    /// То же для списка деревом: `--offset`/`--limit` считают задачи верхнего уровня, `--all` листает верхний уровень страницами
    /// по 200 и собирает строки в один список.
    pub fn load_tree(leaf: &ArgMatches, mut load_page: impl FnMut(Page) -> Result<TaskTreeList>) -> Result<TaskTreeList> {
        if !flag(leaf, "all") {
            return load_page(page_of(int(leaf, "offset", 0), int(leaf, "limit", DEFAULT_LIMIT as i64)));
        }
        if given(leaf, "offset") || given(leaf, "limit") {
            return Err(CliError::new("Use either --all or --offset/--limit, not both"));
        }
        let mut rows: Vec<TaskTreeItem> = Vec::new();
        let mut top = 0;
        let mut page;
        loop {
            page = load_page(Page::new(top, MAX_LIMIT))?;
            rows.append(&mut page.data);
            top += MAX_LIMIT;
            if top >= page.top_level_count {
                break;
            }
        }
        Ok(TaskTreeList {
            total_count: page.total_count,
            top_level_count: page.top_level_count,
            offset: 0,
            limit: page.top_level_count,
            data: rows,
        })
    }
}

/// Ячейки строки списка: короткий id и остальные колонки (`Kit.Row`).
pub fn row(id: &Uuid, cells: &[&str]) -> Vec<String> {
    std::iter::once(tasker_core::ShortId::of(id))
        .chain(cells.iter().map(|c| c.to_string()))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_card_pads_names_to_13_and_skips_empty_values() {
        let text = fields(&[
            ("id", Some("x".into())),
            ("enum", None),
            ("hierarchical", Some("False".into())),
            ("allowCycles", Some("True".into())),
        ]);
        assert_eq!(text, "id:           x\nhierarchical: False\nallowCycles:  True");
    }

    #[test]
    fn pages_clamp_like_dotnet() {
        assert_eq!(page_of(-3, 500), Page::new(0, 200));
        assert_eq!(page_of(5, 0), Page::new(5, 1));
        assert_eq!(tasks(1), "1 task");
        assert_eq!(tasks(12), "12 tasks");
    }
}
