//! Индекс `index-rs.db` на golden-корпусе (`rust/tests/golden`): порядок списков всех сущностей равен снапшотам `*-list-json`,
//! порядок, фильтры, сортировки и страницы `task list` — снапшотам `*task-list*` (параметры берутся из `.args`), проблемы
//! области — блоку `tasker sync`; Sync без изменений ничего не перечитывает, изменение файла подхватывается, удаление убирает
//! запись, повреждённая база пересобирается.
use regex::Regex;
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use tasker_core::ShortId;
use tasker_core::fields;
use tasker_core::ids::guid_d;
use tasker_core::model::{
    FieldDefinition, FieldEnum, FieldOperator, FieldType, Project, Series, Status, StatusSet, TaskItem, TaskType, User, UserKind,
};
use tasker_core::tasks::{
    FieldCondition, Page, SortField, SortTarget, TaskFilter, TaskSortKey, builtin_sort, field_name_key, field_number, ranks, status_ranks,
};
use tasker_core::validate::{eq_ignore_case, to_lower_invariant};
use tasker_files::index::{IndexQuery, WorkspaceIndex};
use tasker_files::layout::{EntityKind, TaskerDirectory};
use uuid::Uuid;

fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
}

/// Копия области в `target/tmp` (без `.cache`: индекс строится заново).
fn fresh_copy() -> PathBuf {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&root).unwrap();
    let root = canonical(&root);
    let workspace = root.join("golden");
    copy_dir(&golden().join("workspace"), &workspace);
    workspace
}

fn copy_dir(from: &Path, to: &Path) {
    std::fs::create_dir_all(to).unwrap();
    for entry in std::fs::read_dir(from).unwrap() {
        let entry = entry.unwrap();
        let target = to.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&entry.path(), &target);
        } else {
            std::fs::copy(entry.path(), target).unwrap();
        }
    }
}

fn snapshot(name: &str) -> (Vec<String>, String) {
    let base = golden().join("expected/cli").join(name);
    let read = |ext: &str| std::fs::read_to_string(format!("{}.{ext}", base.display())).unwrap();
    let args = read("args").lines().map(str::to_string).collect();
    (args, read("out"))
}

fn snapshots_matching(pattern: &str) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(golden().join("expected/cli"))
        .unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
        .filter(|n| n.ends_with(".args") && n.contains(pattern) && !n.contains("help"))
        .map(|n| n.trim_end_matches(".args").to_string())
        .collect();
    names.sort();
    names
}

/// Область корпуса с открытым индексом и справочниками проекта Golden.
struct Corpus {
    workspace: PathBuf,
    index: WorkspaceIndex,
}

impl Corpus {
    fn open() -> Corpus {
        let workspace = fresh_copy();
        let index = WorkspaceIndex::open(&TaskerDirectory::new(&workspace)).unwrap();
        Corpus { workspace, index }
    }

    fn project(&self, name: &str) -> Project {
        self.index
            .all::<Project>(&IndexQuery::default())
            .unwrap()
            .into_iter()
            .find(|p| p.name == name)
            .unwrap_or_else(|| panic!("project {name}"))
    }

    fn all<T: tasker_files::index::IndexEntity>(&self, project: &Uuid) -> Vec<T> {
        self.index.all::<T>(&IndexQuery::project(project)).unwrap()
    }

    /// Ссылка задачи, как в первом столбце консоли: первая серия (`PREFIX-N`) или короткий id.
    fn reference(&self, project: &Uuid, task: &TaskItem) -> String {
        let series: Vec<Series> = self.all(project);
        match task.series_numbers.first() {
            Some(n) => {
                let prefix = series
                    .iter()
                    .find(|s| s.id == n.series_id)
                    .map(|s| s.prefix.as_str())
                    .unwrap_or("?");
                format!("{prefix}-{}", n.number)
            }
            None => ShortId::of(&task.id),
        }
    }
}

/// Параметры `task list` из `.args` (как их разбирает консоль .NET): проект, фильтры, порядок, страница, `--flat`, `--json`.
#[derive(Debug, Default)]
struct ListArgs {
    project: String,
    types: Vec<String>,
    statuses: Vec<String>,
    series: Vec<String>,
    fields: Vec<String>,
    sort: Option<String>,
    offset: Option<usize>,
    limit: Option<usize>,
    flat: bool,
    json: bool,
}

fn parse_args(args: &[String]) -> ListArgs {
    let mut result = ListArgs::default();
    let mut i = 0;
    let values = |i: &mut usize| {
        let mut found = Vec::new();
        while *i + 1 < args.len() && !args[*i + 1].starts_with('-') {
            *i += 1;
            found.push(args[*i].clone());
        }
        found
    };
    while i < args.len() {
        match args[i].as_str() {
            "-p" => result.project = values(&mut i).remove(0),
            "--type" => result.types.extend(values(&mut i)),
            "--status" => result.statuses.extend(values(&mut i)),
            "--series" => result.series.extend(values(&mut i)),
            "--field" => result.fields.extend(values(&mut i)),
            // Значение --sort может начинаться с «-» (убывание): берётся следующий аргумент как есть.
            "--sort" => {
                i += 1;
                result.sort = Some(args[i].clone());
            }
            "--offset" => result.offset = values(&mut i).pop().map(|v| v.parse().unwrap()),
            "--limit" => result.limit = values(&mut i).pop().map(|v| v.parse().unwrap()),
            "--flat" => result.flat = true,
            "--json" => result.json = true,
            "-w" | "--width" | "--description-length" => {
                values(&mut i);
            }
            _ => {}
        }
        i += 1;
    }
    result
}

/// Фильтр и порядок, как их построил бы `TaskService` (`ParseFieldFilters`, `ParseSort`) по справочникам проекта.
fn build_filter(corpus: &Corpus, project: &Uuid, args: &ListArgs) -> TaskFilter {
    let types: Vec<TaskType> = corpus.all(project);
    let statuses: Vec<Status> = corpus.all(project);
    let sets: Vec<StatusSet> = corpus.all(project);
    let series: Vec<Series> = corpus.all(project);
    let catalog: Vec<FieldDefinition> = corpus.all(project);
    let enums: Vec<FieldEnum> = corpus.all(project);

    let by_name = |names: &[String], pick: &dyn Fn(&str) -> Vec<Uuid>| -> Option<Vec<Uuid>> {
        if names.is_empty() {
            return None;
        }
        let mut ids: Vec<Uuid> = names.iter().flat_map(|n| pick(n)).collect();
        ids.dedup();
        Some(ids)
    };
    let type_ids = by_name(&args.types, &|n| {
        types.iter().filter(|t| eq_ignore_case(&t.name, n)).map(|t| t.id).collect()
    });
    let status_ids = by_name(&args.statuses, &|n| {
        statuses.iter().filter(|s| eq_ignore_case(&s.name, n)).map(|s| s.id).collect()
    });
    let series_ids = by_name(&args.series, &|n| series.iter().filter(|s| s.prefix == n).map(|s| s.id).collect());

    let resolve_field = |name: &str| -> (String, FieldType, Option<Uuid>, Option<Uuid>) {
        if let Some(f) = catalog.iter().find(|f| eq_ignore_case(&f.name, name)) {
            return (f.name.clone(), f.field_type, f.enum_id, Some(f.id));
        }
        let kinds = corpus.index.own_field_kinds(project, &field_name_key(name)).unwrap();
        assert_eq!(kinds.len(), 1, "own field '{name}' of one type");
        (name.to_string(), kinds[0].field_type, kinds[0].enum_id, None)
    };

    let mut conditions = Vec::new();
    for expression in &args.fields {
        let at = expression.find(['=', '!', '<', '>', ':']).unwrap();
        let (name, field_type, enum_id, field_id) = resolve_field(expression[..at].trim());
        let enumeration = enum_id.and_then(|e| enums.iter().find(|x| x.id == e));
        if &expression[at..at + 1] == ":" {
            let operator = match to_lower_invariant(expression[at + 1..].trim()).as_str() {
                "set" => FieldOperator::Set,
                "unset" => FieldOperator::Unset,
                "attached" => FieldOperator::Attached,
                "detached" => FieldOperator::Detached,
                other => panic!("{other}"),
            };
            let type_ids = match (operator, field_id) {
                (FieldOperator::Attached | FieldOperator::Detached, Some(id)) => Some(
                    types
                        .iter()
                        .filter(|t| t.fields.iter().any(|f| f.field_id == id))
                        .map(|t| t.id)
                        .collect(),
                ),
                _ => None,
            };
            conditions.push(FieldCondition {
                name,
                field_type,
                operator,
                value: None,
                number: None,
                field_id,
                type_ids,
            });
            continue;
        }
        let (text, operator) = [
            ("!=", FieldOperator::NotEqual),
            (">=", FieldOperator::GreaterOrEqual),
            ("<=", FieldOperator::LessOrEqual),
            ("=", FieldOperator::Equal),
            (">", FieldOperator::Greater),
            ("<", FieldOperator::Less),
        ]
        .into_iter()
        .find(|(t, _)| expression[at..].starts_with(t))
        .unwrap();
        let ordered = matches!(
            operator,
            FieldOperator::Greater | FieldOperator::GreaterOrEqual | FieldOperator::Less | FieldOperator::LessOrEqual
        );
        let raw = expression[at + text.len()..].to_string();
        let value = fields::normalize(&name, field_type, false, enumeration, &[Some(raw)])
            .unwrap()
            .remove(0);
        let number = if ordered && matches!(field_type, FieldType::Int | FieldType::Float) {
            field_number(&value)
        } else {
            None
        };
        conditions.push(FieldCondition {
            name,
            field_type,
            operator,
            value: Some(value),
            number,
            field_id,
            type_ids: None,
        });
    }

    let mut keys = Vec::new();
    for raw in args.sort.as_deref().unwrap_or("").split(',').filter(|s| !s.trim().is_empty()) {
        let text = raw.trim();
        let descending = text.starts_with('-');
        let name = text.trim_start_matches('-').trim();
        if let Some((target, canonical)) = builtin_sort(name) {
            let mut key = TaskSortKey::new(target, descending, canonical);
            match target {
                SortTarget::Type => {
                    key.type_ranks = Some(ranks(
                        &types.iter().map(|t| (t.id, to_lower_invariant(&t.name))).collect::<Vec<_>>(),
                    ))
                }
                SortTarget::Series => key.series_ranks = Some(ranks(&series.iter().map(|s| (s.id, s.prefix.clone())).collect::<Vec<_>>())),
                SortTarget::Status => key.status_ranks = Some(status_ranks(&types, &sets, &statuses)),
                _ => {}
            }
            keys.push(key);
            continue;
        }
        let (name, field_type, enum_id, field_id) = resolve_field(name);
        let enum_values = (field_type == FieldType::Enum).then(|| {
            enum_id
                .and_then(|e| enums.iter().find(|x| x.id == e))
                .map(|e| e.values.iter().map(|v| guid_d(&v.id)).collect())
                .unwrap_or_default()
        });
        let mut key = TaskSortKey::new(SortTarget::Field, descending, &name);
        key.field = Some(SortField {
            name,
            field_type,
            field_id,
            enum_values,
        });
        keys.push(key);
    }

    TaskFilter {
        type_ids,
        status_ids,
        series_ids,
        field_values: (!conditions.is_empty()).then_some(conditions),
        sort: (!keys.is_empty()).then_some(keys),
        ..TaskFilter::default()
    }
}

/// Строки текстового списка: (глубина > 0, повтор `(+)`, ссылка — первая из перечисленных через запятую).
fn parse_text_rows(out: &str) -> Vec<(bool, bool, String)> {
    let row = Regex::new(r"^( *)(\S+?)( \(\+\))?  +\S").unwrap();
    out.lines()
        .skip(usize::from(out.starts_with("Found ")))
        .filter_map(|line| {
            let m = row.captures(line)?;
            let reference = m[2].split(',').next().unwrap().to_string();
            Some((!m[1].is_empty(), m.get(3).is_some(), reference))
        })
        .collect()
}

fn json_ids(out: &str) -> Vec<Uuid> {
    let value: serde_json::Value = serde_json::from_str(out).unwrap();
    let items = value
        .get("data")
        .and_then(|d| d.as_array())
        .cloned()
        .unwrap_or_else(|| value.as_array().unwrap().clone());
    items.iter().map(|x| Uuid::parse_str(x["id"].as_str().unwrap()).unwrap()).collect()
}

#[test]
fn task_lists_follow_the_snapshots() {
    let corpus = Corpus::open();
    let mut checked = 0;
    for name in snapshots_matching("task-list") {
        let (args, out) = snapshot(&name);
        let args = parse_args(&args);
        let project = corpus.project(&args.project).id;
        let filter = build_filter(&corpus, &project, &args);
        let query = IndexQuery::tasks(&project, Some(&filter));
        let ids = corpus.index.ids(EntityKind::Task, &query).unwrap();
        let total = corpus.index.count(EntityKind::Task, &query).unwrap();
        assert_eq!(total, ids.len(), "{name}");

        if args.json {
            let expected = json_ids(&out);
            // Список --json — плоский, в порядке запроса (иерархия только в тексте).
            assert_eq!(ids, expected, "{name}: ids in order");
            checked += 1;
            continue;
        }

        let tasks: Vec<TaskItem> = corpus.index.all(&query).unwrap();
        assert_eq!(tasks.iter().map(|t| t.id).collect::<Vec<_>>(), ids, "{name}: all() and ids() agree");
        let references: Vec<String> = tasks.iter().map(|t| corpus.reference(&project, t)).collect();
        let rows = parse_text_rows(&out);
        let first_line = out.lines().next().unwrap();
        // -q печатает список без заголовка.
        assert!(
            first_line.starts_with(&format!("Found {total}")) || !first_line.starts_with("Found "),
            "{name}: {first_line}"
        );

        if args.offset.is_some() || args.limit.is_some() {
            // Страница дерева — верхний уровень (TSK-132); здесь — только что это префикс нашего порядка.
            assert!(!args.flat, "{name}: page snapshots are tree pages");
            let page = corpus
                .index
                .range::<TaskItem>(
                    &query,
                    Page::new(args.offset.unwrap_or(0), args.limit.unwrap_or(Page::DEFAULT_LIMIT)),
                )
                .unwrap();
            assert_eq!(page.total_count, total, "{name}");
            assert_eq!(
                page.data.iter().map(|t| t.id).collect::<Vec<_>>(),
                ids[page.offset..(page.offset + page.limit).min(ids.len())],
                "{name}: page is a slice of the full order"
            );
            checked += 1;
            continue;
        }
        if args.flat {
            let expected: Vec<String> = rows.iter().map(|r| r.2.clone()).collect();
            assert_eq!(references, expected, "{name}: flat order");
        } else {
            // Дерево: строки верхнего уровня идут в порядке списка; все задачи дерева (без повторов) — это весь список.
            let top: Vec<&String> = rows.iter().filter(|r| !r.0).map(|r| &r.2).collect();
            let mut position = references.iter();
            for reference in &top {
                assert!(position.any(|r| r == *reference), "{name}: top-level {reference} out of order");
            }
            let set: HashSet<&String> = rows.iter().filter(|r| !r.1).map(|r| &r.2).collect();
            assert_eq!(set, references.iter().collect::<HashSet<_>>(), "{name}: tree covers the list");
        }
        checked += 1;
    }
    assert!(checked >= 20, "{checked} snapshots checked");
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

#[test]
fn entity_lists_follow_the_snapshots() {
    let corpus = Corpus::open();
    let golden = corpus.project("Golden").id;
    let names = [
        ("011-project-list-json", EntityKind::Project),
        ("015-status-list-json", EntityKind::Status),
        ("017-status-set-list-json", EntityKind::StatusSet),
        ("019-task-type-list-json", EntityKind::TaskType),
        ("021-series-list-json", EntityKind::Series),
        ("023-field-list-json", EntityKind::Field),
        ("025-enum-list-json", EntityKind::FieldEnum),
        ("027-board-list-json", EntityKind::Board),
        ("029-link-type-list-json", EntityKind::LinkType),
    ];
    for (name, kind) in names {
        let (_, out) = snapshot(name);
        let query = if kind == EntityKind::Project {
            IndexQuery::default()
        } else {
            IndexQuery::project(&golden)
        };
        assert_eq!(corpus.index.ids(kind, &query).unwrap(), json_ids(&out), "{name}");
    }
    let (_, users) = snapshot("048-user-list-json");
    assert_eq!(
        corpus
            .index
            .ids(EntityKind::User, &IndexQuery::users(Some(UserKind::Human)))
            .unwrap(),
        json_ids(&users)
    );
    let (_, agents) = snapshot("050-agent-list-json");
    assert_eq!(
        corpus
            .index
            .ids(EntityKind::User, &IndexQuery::users(Some(UserKind::Agent)))
            .unwrap(),
        json_ids(&agents)
    );
    let page = corpus
        .index
        .range::<Status>(&IndexQuery::project(&golden), Page::new(1, 2))
        .unwrap();
    assert_eq!((page.total_count, page.offset, page.limit), (5, 1, 2));
    assert_eq!(
        page.data.iter().map(|s| s.name.as_str()).collect::<Vec<_>>(),
        vec!["Backlog", "Done"]
    );
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

#[test]
fn problems_match_the_sync_report() {
    let corpus = Corpus::open();
    let (_, out) = snapshot("001-sync");
    let expected: Vec<(String, String)> = out
        .lines()
        .skip_while(|l| !l.ends_with("cannot be read and are missing from the lists until fixed:"))
        .skip(1)
        .take_while(|l| l.starts_with("  "))
        .map(|l| {
            let (path, error) = l.trim_start().split_once(": ").unwrap();
            (path.to_string(), error.to_string())
        })
        .collect();
    assert_eq!(expected.len(), 2);
    let problems = corpus.index.problems(Page::first(50)).unwrap();
    assert_eq!(problems.total_count, 2);
    assert_eq!(
        problems.data.iter().map(|p| (p.path.clone(), p.error.clone())).collect::<Vec<_>>(),
        expected
    );
    let legacy = corpus.project("Legacy").id;
    let dir = TaskerDirectory::new(&corpus.workspace);
    assert_eq!(corpus.index.count_problems(&dir.project(&legacy).tasks()).unwrap(), 2);
    assert_eq!(corpus.index.count_problems(&dir.project(&legacy).series()).unwrap(), 0);
    // Нечитаемые файлы не попадают в списки.
    assert_eq!(corpus.index.count(EntityKind::Task, &IndexQuery::project(&legacy)).unwrap(), 4);
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

#[test]
fn sync_rereads_only_changed_files_and_drops_deleted_ones() {
    let corpus = Corpus::open();
    let dir = TaskerDirectory::new(&corpus.workspace);
    let golden = corpus.project("Golden").id;
    let total_files = corpus.index.sync().unwrap();
    assert_eq!(
        (total_files.updated, total_files.removed, total_files.changed.len()),
        (0, 0, 0),
        "nothing changed"
    );

    // Правка заголовка: размер меняется — файл перечитан, список видит новый заголовок, прежнего нет.
    let task = corpus
        .index
        .all::<TaskItem>(&IndexQuery::project(&golden))
        .unwrap()
        .into_iter()
        .find(|t| t.title == "Plain task")
        .unwrap();
    let path = corpus.index.entity_path(EntityKind::Task, &golden, &task.id).unwrap().unwrap();
    assert_eq!(path, dir.project(&golden).task_file(&task.id, Some("Plain task")));
    let text = std::fs::read_to_string(&path)
        .unwrap()
        .replace("title: Plain task", "title: Plain task (edited)");
    std::fs::write(&path, text).unwrap();
    let report = corpus.index.refresh(std::slice::from_ref(&path)).unwrap();
    assert_eq!((report.updated, report.removed), (1, 0));
    assert_eq!(report.changed[0].id, Some(task.id));
    assert!(report.changed[0].path.ends_with("/plain-task-00000060.yaml"));
    let titles: Vec<String> = corpus
        .index
        .all::<TaskItem>(&IndexQuery::project(&golden))
        .unwrap()
        .into_iter()
        .map(|t| t.title)
        .collect();
    assert!(titles.contains(&"Plain task (edited)".to_string()) && !titles.contains(&"Plain task".to_string()));

    // Только время изменения (тот же размер) — тоже перечитывается.
    let later = std::time::SystemTime::now() + std::time::Duration::from_secs(5);
    std::fs::File::options()
        .write(true)
        .open(&path)
        .unwrap()
        .set_modified(later)
        .unwrap();
    assert_eq!(corpus.index.refresh(&[dir.project(&golden).tasks()]).unwrap().updated, 1);
    assert_eq!(corpus.index.sync().unwrap().updated, 0);

    // Удаление файла убирает запись и строки серии.
    let before = corpus.index.max_number(&golden, &task.series_numbers[0].series_id).unwrap();
    std::fs::remove_file(&path).unwrap();
    let report = corpus.index.refresh(std::slice::from_ref(&path)).unwrap();
    assert_eq!((report.updated, report.removed), (0, 1));
    assert_eq!(
        report.changed,
        vec![tasker_files::index::FileChange {
            path: corpus_relative(&dir, &path),
            id: Some(task.id)
        }]
    );
    assert_eq!(corpus.index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 60);
    assert_eq!(corpus.index.entity_path(EntityKind::Task, &golden, &task.id).unwrap(), None);
    assert_eq!(corpus.index.max_number(&golden, &task.series_numbers[0].series_id).unwrap(), before);
    assert_eq!(
        corpus
            .index
            .tasks_by_number(&golden, &task.series_numbers[0].series_id, 1)
            .unwrap()
            .len(),
        0
    );

    // written(): индекс догоняет файл и когда запись не прошла.
    let restored = dir.project(&golden).task_file(&task.id, Some("Plain task"));
    let error = corpus
        .index
        .written(std::slice::from_ref(&restored), || {
            std::fs::write(&restored, tasker_files::files::task::serialize(&task)).unwrap();
            Err::<(), _>(std::io::Error::other("version mismatch"))
        })
        .unwrap_err();
    assert_eq!(error.to_string(), "version mismatch");
    assert_eq!(corpus.index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 61);
    assert_eq!(
        corpus
            .index
            .written(std::slice::from_ref(&restored), || std::fs::remove_file(&restored).map(|_| 5))
            .unwrap(),
        5
    );
    assert_eq!(corpus.index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 60);

    // Новый файл с id, который не совпадает с именем, — проблема с текстом .NET.
    let bad = dir.project(&golden).tasks().join("bad-ffffffff.yaml");
    std::fs::write(&bad, "formatVersion: 9\nid: 0000006b-0000-4000-8000-00000000006b\ntitle: x\ntypeId: 0000002c-0000-4000-8000-00000000002c\nstatusId: 00000005-0000-4000-8000-000000000005\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\n").unwrap();
    let empty = dir.project(&golden).tasks().join("empty-00000001.yaml");
    std::fs::write(&empty, "").unwrap();
    corpus.index.sync().unwrap();
    let problems = corpus.index.problems(Page::first(10)).unwrap().data;
    assert!(problems.iter().any(|p| p.path.ends_with("/bad-ffffffff.yaml")
        && p.error == "Id in the file (0000006b-0000-4000-8000-00000000006b) does not match the end of the file name (ffffffff)"));
    // Пустой файл: после апгрейда в памяти это «formatVersion: 9», модель с пустым id — как у .NET.
    assert!(problems.iter().any(|p| p.path.ends_with("/empty-00000001.yaml")
        && p.error == "Id in the file (00000000-0000-0000-0000-000000000000) does not match the end of the file name (00000001)"));
    assert_eq!(corpus.index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 60);
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

fn corpus_relative(dir: &TaskerDirectory, path: &Path) -> String {
    path.strip_prefix(dir.root()).unwrap().to_string_lossy().replace('\\', "/")
}

#[test]
fn a_corrupt_or_foreign_database_is_rebuilt() {
    let corpus = Corpus::open();
    let golden = corpus.project("Golden").id;
    let layout_files = WorkspaceIndex::attach(&TaskerDirectory::new(&corpus.workspace))
        .unwrap()
        .sync()
        .unwrap();
    assert_eq!(layout_files.updated, 0, "already in sync");
    let total_files = corpus.index.count(EntityKind::Task, &IndexQuery::default()).unwrap()
        + [
            EntityKind::Project,
            EntityKind::User,
            EntityKind::Status,
            EntityKind::StatusSet,
            EntityKind::TaskType,
            EntityKind::Board,
            EntityKind::Series,
            EntityKind::LinkType,
            EntityKind::Field,
            EntityKind::FieldEnum,
        ]
        .iter()
        .map(|k| corpus.index.count(*k, &IndexQuery::default()).unwrap())
        .sum::<usize>()
        + corpus.index.problems(Page::first(10)).unwrap().total_count;
    let file = corpus.index.file();
    assert!(file.ends_with(".cache/index-rs.db"));
    assert!(corpus.index.lock_file().ends_with(".cache/index-rs.lock"));

    // Мусор вместо базы.
    std::fs::write(&file, b"not a database at all, just bytes").unwrap();
    let index = WorkspaceIndex::open(&TaskerDirectory::new(&corpus.workspace)).unwrap();
    assert_eq!(index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 61);
    assert_eq!(index.problems(Page::first(10)).unwrap().total_count, 2);

    // Чужая версия схемы — таблицы пересоздаются, все файлы перечитываются.
    {
        let connection = rusqlite::Connection::open(&file).unwrap();
        connection.execute_batch("PRAGMA user_version = 999").unwrap();
    }
    let index = WorkspaceIndex::attach(&TaskerDirectory::new(&corpus.workspace)).unwrap();
    assert_eq!(index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 0);
    let report = index.sync().unwrap();
    assert_eq!(report.removed, 0);
    assert_eq!(report.updated, total_files);
    assert_eq!(index.count(EntityKind::Task, &IndexQuery::project(&golden)).unwrap(), 61);
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

#[test]
fn lookups_series_links_and_fields() {
    let corpus = Corpus::open();
    let golden = corpus.project("Golden").id;
    let series: Vec<Series> = corpus.all(&golden);
    let bugs = series.iter().find(|s| s.prefix == "BUG").unwrap().id;
    let gld = series.iter().find(|s| s.prefix == "GLD").unwrap().id;
    assert_eq!(corpus.index.max_number(&golden, &bugs).unwrap(), 4);
    assert_eq!(corpus.index.max_number(&golden, &gld).unwrap(), 58);
    assert_eq!(corpus.index.max_number(&golden, &Uuid::nil()).unwrap(), 0);

    let by_number = corpus.index.tasks_by_number(&golden, &bugs, 2).unwrap();
    assert_eq!(
        by_number.iter().map(|t| t.title.as_str()).collect::<Vec<_>>(),
        vec!["Bug with own fields"]
    );
    let own = &by_number[0];

    // Поиск по префиксу id и по полному id.
    let found = corpus
        .index
        .all::<TaskItem>(&IndexQuery::project(&golden).with_id_prefix(&ShortId::try_key(Some("00000037")).unwrap()))
        .unwrap();
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].id, own.id);
    let filter = TaskFilter {
        ids: Some(vec![own.id, Uuid::nil()]),
        ..TaskFilter::default()
    };
    assert_eq!(
        corpus
            .index
            .ids(EntityKind::Task, &IndexQuery::tasks(&golden, Some(&filter)))
            .unwrap(),
        vec![own.id]
    );
    let empty = TaskFilter {
        ids: Some(vec![]),
        ..TaskFilter::default()
    };
    assert_eq!(
        corpus
            .index
            .count(EntityKind::Task, &IndexQuery::tasks(&golden, Some(&empty)))
            .unwrap(),
        0
    );

    // Пользователь по нормализованному имени; сущность по имени без учёта регистра — через sort_key.
    let user = corpus
        .index
        .first::<User>(&IndexQuery::default().with_sort_key(&tasker_files::index::normalize_username("  ALICE ")))
        .unwrap()
        .unwrap();
    assert_eq!(user.username, "alice");
    assert!(
        corpus
            .index
            .first::<User>(&IndexQuery::default().with_sort_key("nobody"))
            .unwrap()
            .is_none()
    );

    // Входящие связи: Bug with all fields (BUG-1) блокирует Feature with values и Bug with own fields.
    let incoming = corpus.index.tasks_linked_to(&golden, &own.id).unwrap();
    assert_eq!(
        incoming.iter().map(|t| t.title.as_str()).collect::<Vec<_>>(),
        vec!["Bug with all fields (renamed)"]
    );
    let bug1 = &incoming[0];
    let counts = corpus.index.count_linked_to(&golden, &[own.id, bug1.id, Uuid::nil()]).unwrap();
    assert_eq!(counts.get(&own.id), Some(&1));
    assert_eq!(counts.get(&bug1.id), Some(&1), "Bug done is caused by it");
    assert_eq!(counts.get(&Uuid::nil()), None);
    let blocks = bug1.links[0].type_id;
    let targets = corpus.index.link_targets(&golden, &blocks, &[bug1.id]).unwrap();
    assert_eq!(targets[&bug1.id].len(), 2);
    assert_eq!(corpus.index.link_edges(&golden, &blocks).unwrap().len(), 2);
    assert!(corpus.index.tasks_with_invalid_links(&golden, &[]).unwrap().len() >= 3);
    let known: Vec<Uuid> = corpus.all::<tasker_core::model::LinkType>(&golden).iter().map(|l| l.id).collect();
    assert!(corpus.index.tasks_with_invalid_links(&golden, &known).unwrap().is_empty());
    assert!(corpus.index.number_conflicts(&golden).unwrap().is_empty());
    assert!(corpus.index.tasks_with_series_not_in(&golden, &[bugs, gld]).unwrap().is_empty());
    assert_eq!(corpus.index.tasks_with_series_not_in(&golden, &[gld]).unwrap().len(), 4);

    // Собственные поля: тип и перечисление по имени без учёта регистра.
    let kinds = corpus.index.own_field_kinds(&golden, "severity").unwrap();
    assert_eq!(kinds.len(), 1);
    assert_eq!(kinds[0].field_type, FieldType::Enum);
    assert!(kinds[0].enum_id.is_some());
    assert!(corpus.index.own_field_kinds(&golden, "Severity").unwrap().is_empty());

    // Фильтры по спискам: серии, типы связей, поля, перечисления.
    let by_link = TaskFilter {
        link_type_ids: Some(vec![blocks]),
        ..TaskFilter::default()
    };
    assert_eq!(
        corpus
            .index
            .count(EntityKind::Task, &IndexQuery::tasks(&golden, Some(&by_link)))
            .unwrap(),
        1
    );
    let by_enum = TaskFilter {
        enum_ids: Some(vec![kinds[0].enum_id.unwrap()]),
        ..TaskFilter::default()
    };
    assert_eq!(
        corpus
            .index
            .ids(EntityKind::Task, &IndexQuery::tasks(&golden, Some(&by_enum)))
            .unwrap(),
        vec![own.id]
    );

    // Номер серии под write.lock: сверка папки задач перед действием.
    let dir = TaskerDirectory::new(&corpus.workspace);
    let next = corpus
        .index
        .exclusive(&golden, || {
            let path = dir.project(&golden).task_file(&Uuid::nil(), Some("Late"));
            std::fs::write(&path, "formatVersion: 9\nid: 00000000-0000-0000-0000-000000000000\ntitle: Late\ntypeId: 0000002c-0000-4000-8000-00000000002c\nstatusId: 00000005-0000-4000-8000-000000000005\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\nseries:\n- seriesId: 00000028-0000-4000-8000-000000000028\n  number: 9\n").unwrap();
            corpus.index.exclusive(&golden, || corpus.index.max_number(&golden, &bugs))
        })
        .unwrap();
    assert_eq!(next, 9);
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

#[test]
fn sorting_by_fields_and_ranks_in_memory_agrees_with_sql() {
    let corpus = Corpus::open();
    let golden = corpus.project("Golden").id;
    // Строка без учёта регистра: Reporters (собственное, multiple) — по первому значению; Priority (enum) — по порядку значений,
    // при убывании тоже NULL в конце.
    let args = ListArgs {
        project: "Golden".into(),
        sort: Some("-Priority,title".into()),
        ..ListArgs::default()
    };
    let filter = build_filter(&corpus, &golden, &args);
    let tasks: Vec<TaskItem> = corpus.index.all(&IndexQuery::tasks(&golden, Some(&filter))).unwrap();
    let priority = corpus
        .all::<FieldDefinition>(&golden)
        .into_iter()
        .find(|f| f.name == "Priority")
        .unwrap();
    let enumeration = corpus
        .all::<FieldEnum>(&golden)
        .into_iter()
        .find(|e| Some(e.id) == priority.enum_id)
        .unwrap();
    let place = |t: &TaskItem| -> Option<usize> {
        let value = t.fields.iter().find(|f| f.field_id == priority.id)?.values.first()?.clone();
        enumeration.values.iter().position(|v| guid_d(&v.id) == value)
    };
    let with_priority: Vec<&TaskItem> = tasks.iter().filter(|t| place(t).is_some()).collect();
    assert_eq!(with_priority.len(), 3);
    let places: Vec<usize> = with_priority.iter().map(|t| place(t).unwrap()).collect();
    let mut descending = places.clone();
    descending.sort_by(|a, b| b.cmp(a));
    assert_eq!(places, descending, "High before Medium before Low");
    assert!(tasks[..3].iter().all(|t| with_priority.iter().any(|p| p.id == t.id)));
    let rest: Vec<String> = tasks[3..].iter().map(|t| to_lower_invariant(&t.title)).collect();
    let mut sorted = rest.clone();
    sorted.sort_by(|a, b| a.as_bytes().cmp(b.as_bytes()));
    assert_eq!(rest, sorted, "then by title, binary on lower-case");

    // Фильтр в памяти (TaskFilter::matches) и SQL согласны по всем условиям снапшотов.
    for expression in [
        "Priority=High",
        "Priority!=High",
        "Estimate>=3",
        "Tags:set",
        "Ожидание:attached",
        "Urgent=false",
        "Platforms=Linux",
        "Score<2",
        "Due<=2026-12-31",
        "Notes:detached",
    ] {
        let args = ListArgs {
            project: "Golden".into(),
            fields: vec![expression.into()],
            ..ListArgs::default()
        };
        let filter = build_filter(&corpus, &golden, &args);
        let all: Vec<TaskItem> = corpus.index.all(&IndexQuery::project(&golden)).unwrap();
        let expected: Vec<Uuid> = all.iter().filter(|t| filter.matches(t)).map(|t| t.id).collect();
        let actual = corpus
            .index
            .ids(EntityKind::Task, &IndexQuery::tasks(&golden, Some(&filter)))
            .unwrap();
        assert_eq!(actual, expected, "{expression}");
    }
    std::fs::remove_dir_all(&corpus.workspace).unwrap();
}

/// `canonicalize` без префикса `\\?\` на Windows: программы печатают пути в обычном виде (`D:\…`), и сравнение идёт с ними.
fn canonical(path: impl AsRef<std::path::Path>) -> std::path::PathBuf {
    let path = std::fs::canonicalize(path).unwrap();
    match path.to_str().and_then(|text| text.strip_prefix(r"\\?\")) {
        Some(plain) if cfg!(windows) => std::path::PathBuf::from(plain),
        _ => path,
    }
}
