//! Вывод результата одной команды (`Context` в .NET): текст или `--json` (компактный, `tasker_core::json`), таблицы с
//! выравниванием и обрезкой по ширине ([`crate::table`]), первая строка списка `Found N` / `Found N, shown a-b (use
//! --offset/--limit)` (нет с `-q`), списки деревом с отступами и пометкой `(+)`, код выхода команды.
use crate::errors::Result;
use crate::session::Session;
use crate::table;
use serde_json::Value;
use std::io::Write;
use tasker_core::tasks::ListPage;
use uuid::Uuid;

/// Отступ одного уровня вложенности в списке деревом: четыре пробела (один «таб»).
pub const TREE_INDENT: usize = 4;

/// Пометка повтора: задача входит в несколько эпиков и показана снова под вторым.
pub const REPEAT_MARK: &str = " (+)";

/// Строка списка деревом: глубина, повтор ли это и ячейки (первая — ссылка задачи).
pub struct TreeRow {
    pub depth: usize,
    pub repeated: bool,
    pub cells: Vec<String>,
}

pub struct Context<'a> {
    out: &'a mut dyn Write,
    err: &'a mut dyn Write,
    /// Режим `--json`: результат — JSON, а не текст.
    pub json: bool,
    /// Режим `-q`/`--quiet`: в списках нет первой строки с количеством найденного.
    pub quiet: bool,
    /// Предел ширины строк таблиц в знаках; None — не обрезать.
    pub max_width: Option<usize>,
    /// Можно ли ждать ответа: нет, если ввод перенаправлен (скрипт, CI).
    pub is_interactive: bool,
    /// Код выхода команды, если она завершилась без ошибки, но результат не «успех» (например, демон не запущен).
    pub exit_code: i32,
    session: Option<Session>,
    project_reference: Option<String>,
}

impl<'a> Context<'a> {
    pub fn new(out: &'a mut dyn Write, err: &'a mut dyn Write, session: Option<Session>, project_reference: Option<String>) -> Context<'a> {
        Context {
            out,
            err,
            json: false,
            quiet: false,
            max_width: None,
            is_interactive: false,
            exit_code: 0,
            session,
            project_reference,
        }
    }

    pub fn out(&mut self) -> &mut dyn Write {
        self.out
    }

    pub fn err(&mut self) -> &mut dyn Write {
        self.err
    }

    /// В режиме `--json` stdout занят результатом: пояснения и вопросы идут в stderr.
    pub fn prompts(&mut self) -> &mut dyn Write {
        if self.json { self.err } else { self.out }
    }

    /// Открытая область; у команды без области — ошибка программиста, как `InvalidOperationException` в .NET.
    pub fn session(&self) -> &Session {
        self.session.as_ref().expect("The command has no workspace")
    }

    /// Проект из `--project`/`TASKER_PROJECT` или единственный в области (см. [`Session::project_id`]).
    pub fn project_id(&self) -> Result<Uuid> {
        self.session().project_id(self.project_reference.as_deref())
    }

    pub fn optional_project(&self) -> Result<Option<tasker_core::model::Project>> {
        self.session().optional_project(self.project_reference.as_deref())
    }

    /// Строки таблицы с обрезкой по ширине окна, если она включена.
    pub fn table<S: AsRef<str>>(&self, rows: &[Vec<S>], indent: &str) -> Vec<String> {
        table::format(rows, indent, self.max_width)
    }

    fn line(&mut self, text: &str) {
        let _ = writeln!(self.out, "{text}");
    }

    /// Результат создания или изменения одной сущности: JSON или текст.
    pub fn print(&mut self, value: &Value, text: &str) {
        if self.json {
            let json = tasker_core::json::to_string(value);
            self.line(&json);
        } else {
            self.line(text);
        }
    }

    /// Страница списка: в JSON — как в API, в тексте — первая строка с количеством найденного (нет с `-q`), затем по строке
    /// на элемент, колонки выровнены по странице.
    pub fn print_list<T>(&mut self, list: &ListPage<T>, item_json: impl Fn(&T) -> Value, cells: impl Fn(&T) -> Vec<String>) {
        if self.json {
            let json = tasker_core::json::to_string(&list_json(list, item_json));
            self.line(&json);
            return;
        }
        if !self.quiet {
            let found = found_line(list.total_count, list.offset, list.data.len());
            self.line(&found);
        }
        let rows: Vec<Vec<String>> = list.data.iter().map(cells).collect();
        for line in self.table(&rows, "") {
            self.line(&line);
        }
    }

    /// Список без страниц (всё показано): `value` — результат в `--json`, в тексте — как [`Context::print_list`].
    pub fn print_all<T>(&mut self, value: impl FnOnce() -> Value, items: &[T], cells: impl Fn(&T) -> Vec<String>) {
        if self.json {
            let json = tasker_core::json::to_string(&value());
            self.line(&json);
            return;
        }
        if !self.quiet {
            let found = found_line(items.len(), 0, items.len());
            self.line(&found);
        }
        let rows: Vec<Vec<String>> = items.iter().map(cells).collect();
        for line in self.table(&rows, "") {
            self.line(&line);
        }
    }

    /// Страница списка деревом (`task list`): первая строка с количеством найденного (нет с `-q`), затем строки деревьев —
    /// дочерние задачи с отступом в первой колонке, повтор под вторым родителем — с пометкой после ссылки.
    pub fn print_tree(&mut self, total: usize, top_level: usize, offset: usize, rows: &[TreeRow]) {
        if !self.quiet {
            let shown_top = rows.iter().filter(|r| r.depth == 0).count();
            let found = found_tree_line(total, top_level, offset, shown_top);
            self.line(&found);
        }
        let table_rows: Vec<Vec<String>> = rows
            .iter()
            .map(|row| {
                let mut cells = row.cells.clone();
                if let Some(first) = cells.first_mut() {
                    let mark = if row.repeated { REPEAT_MARK } else { "" };
                    *first = format!("{}{}{}", " ".repeat(TREE_INDENT * row.depth), first, mark);
                }
                cells
            })
            .collect();
        for line in self.table(&table_rows, "") {
            self.line(&line);
        }
    }
}

/// JSON страницы списка (`ListDto<T>`): `totalCount`, `offset`, `limit`, `data`.
pub fn list_json<T>(list: &ListPage<T>, item_json: impl Fn(&T) -> Value) -> Value {
    let mut object = serde_json::Map::new();
    object.insert("totalCount".into(), Value::from(list.total_count));
    object.insert("offset".into(), Value::from(list.offset));
    object.insert("limit".into(), Value::from(list.limit));
    object.insert("data".into(), Value::Array(list.data.iter().map(item_json).collect()));
    Value::Object(object)
}

/// Первая строка списка: «Found 3» — показано всё; «Found 106, shown 1-2 (use --offset/--limit)» — страница; «Found 0» — ничего.
/// Число — всего после фильтров. Строка не обрезается по ширине и не входит в таблицу.
pub fn found_line(total: usize, offset: usize, shown: usize) -> String {
    if shown >= total {
        format!("Found {total}")
    } else {
        let range = if shown == 0 {
            "none".to_string()
        } else {
            format!("{}-{}", offset + 1, offset + shown)
        };
        format!("Found {total}, shown {range} (use --offset/--limit)")
    }
}

/// Первая строка списка деревом. Число — уникальные задачи, а не строки. Постранично листается верхний уровень, поддеревья
/// идут целиком: «Found 106, shown top-level 1-20 of 57 (use --offset/--limit)». Нет вложенности — обычная форма [`found_line`].
pub fn found_tree_line(total: usize, top_level: usize, offset: usize, shown_top: usize) -> String {
    if top_level == total {
        return found_line(total, offset, shown_top);
    }
    if offset == 0 && shown_top >= top_level {
        return format!("Found {total}");
    }
    let range = if shown_top == 0 {
        "none".to_string()
    } else {
        format!("{}-{}", offset + 1, offset + shown_top)
    };
    format!("Found {total}, shown top-level {range} of {top_level} (use --offset/--limit)")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn context<'a>(out: &'a mut Vec<u8>, err: &'a mut Vec<u8>) -> Context<'a> {
        Context::new(out, err, None, None)
    }

    #[test]
    fn found_lines_match_dotnet() {
        assert_eq!(found_line(3, 0, 3), "Found 3");
        assert_eq!(found_line(0, 0, 0), "Found 0");
        assert_eq!(found_line(5, 0, 2), "Found 5, shown 1-2 (use --offset/--limit)");
        assert_eq!(found_line(5, 3, 2), "Found 5, shown 4-5 (use --offset/--limit)");
        assert_eq!(found_line(5, 9, 0), "Found 5, shown none (use --offset/--limit)");
        assert_eq!(
            found_tree_line(61, 58, 5, 7),
            "Found 61, shown top-level 6-12 of 58 (use --offset/--limit)"
        );
        assert_eq!(found_tree_line(61, 58, 0, 58), "Found 61");
        assert_eq!(found_tree_line(8, 8, 0, 8), "Found 8");
        assert_eq!(
            found_tree_line(61, 58, 100, 0),
            "Found 61, shown top-level none of 58 (use --offset/--limit)"
        );
    }

    #[test]
    fn a_list_prints_the_found_line_then_an_aligned_table_and_quiet_drops_the_line() {
        let list = ListPage {
            total_count: 5,
            offset: 0,
            limit: 2,
            data: vec![("TSK-1", "Todo", "Длинный заголовок"), ("TSK-10", "Done", "Короткий")],
        };
        let cells = |x: &(&str, &str, &str)| vec![x.0.to_string(), x.1.to_string(), x.2.to_string()];
        let (mut out, mut err) = (Vec::new(), Vec::new());
        context(&mut out, &mut err).print_list(&list, |_| Value::Null, cells);
        assert_eq!(
            String::from_utf8(out).unwrap(),
            "Found 5, shown 1-2 (use --offset/--limit)\nTSK-1   Todo  Длинный заголовок\nTSK-10  Done  Короткий\n"
        );

        let (mut out, mut err) = (Vec::new(), Vec::new());
        let mut ctx = context(&mut out, &mut err);
        ctx.quiet = true;
        ctx.max_width = Some(20);
        ctx.print_list(&list, |_| Value::Null, cells);
        assert_eq!(String::from_utf8(out).unwrap(), "TSK-1   Todo  Длинн…\nTSK-10  Done  Корот…\n");
    }

    #[test]
    fn json_is_compact_and_has_the_list_dto_shape() {
        let list = ListPage {
            total_count: 1,
            offset: 0,
            limit: 50,
            data: vec!["x"],
        };
        let (mut out, mut err) = (Vec::new(), Vec::new());
        let mut ctx = context(&mut out, &mut err);
        ctx.json = true;
        ctx.print_list(&list, |x| serde_json::json!({"name": x}), |_| vec![]);
        assert_eq!(
            String::from_utf8(out).unwrap(),
            "{\"totalCount\":1,\"offset\":0,\"limit\":50,\"data\":[{\"name\":\"x\"}]}\n"
        );
    }

    #[test]
    fn a_tree_indents_children_and_marks_repeats() {
        let row = |depth, repeated, id: &str, title: &str| TreeRow {
            depth,
            repeated,
            cells: vec![id.into(), "Backlog".into(), title.into()],
        };
        let rows = [
            row(0, false, "GLD-54", "Epic"),
            row(1, false, "GLD-55", "Child one"),
            row(2, false, "GLD-57", "Grandchild"),
            row(0, false, "GLD-58", "Epic two"),
            row(1, true, "GLD-57", "Grandchild"),
        ];
        let (mut out, mut err) = (Vec::new(), Vec::new());
        context(&mut out, &mut err).print_tree(4, 2, 0, &rows);
        assert_eq!(
            String::from_utf8(out).unwrap(),
            "Found 4\nGLD-54          Backlog  Epic\n    GLD-55      Backlog  Child one\n        GLD-57  Backlog  Grandchild\nGLD-58          Backlog  Epic two\n    GLD-57 (+)  Backlog  Grandchild\n"
        );
    }
}
