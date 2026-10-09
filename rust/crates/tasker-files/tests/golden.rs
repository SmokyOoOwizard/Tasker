//! Проверки `tasker-files` на golden-корпусе (`rust/tests/golden`, TSK-124): каждый файл области разбирается в модель своего
//! вида и записывается обратно байт в байт; файлы старых версий, с CRLF и BOM после апгрейда в памяти совпадают с результатом
//! `tasker migrate`; файл с конфликтом слияния и файл формата 99 дают те же ошибки; стилевые пробы YamlDotNet.
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use tasker_core::ids::default_link_type_id;
use tasker_core::model::{FieldType, UserKind};
use tasker_files::yaml::write_scalar;
use tasker_files::{Error, files, format};
use uuid::Uuid;

const BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
}

fn workspace() -> PathBuf {
    golden().join("workspace/.tasker")
}

fn migrated() -> PathBuf {
    golden().join("expected/migrate/tasker")
}

fn yaml_files(dir: &Path, out: &mut Vec<PathBuf>) {
    for entry in std::fs::read_dir(dir).unwrap() {
        let path = entry.unwrap().path();
        if path.is_dir() {
            yaml_files(&path, out);
        } else if path.extension().is_some_and(|e| e == "yaml") {
            out.push(path);
        }
    }
}

fn all_yaml_files(root: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    yaml_files(root, &mut files);
    files.sort();
    files
}

fn relative(root: &Path, file: &Path) -> String {
    file.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/")
}

/// Вид файла по пути внутри `.tasker` (как `WorkspaceLayout.Classify`): `users/*`, `projects/<id>/project.yaml`,
/// `projects/<id>/<папка>/*`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    Project,
    User,
    Status,
    StatusSet,
    TaskType,
    Series,
    LinkType,
    Field,
    Enum,
    Board,
    Task,
}

fn classify(relative: &str) -> (Kind, Option<Uuid>) {
    let parts: Vec<&str> = relative.split('/').collect();
    match parts.as_slice() {
        ["users", _] => (Kind::User, None),
        ["projects", id, "project.yaml"] => (Kind::Project, Some(Uuid::parse_str(id).unwrap())),
        ["projects", id, folder, _] => {
            let kind = match *folder {
                "statuses" => Kind::Status,
                "status-sets" => Kind::StatusSet,
                "task-types" => Kind::TaskType,
                "series" => Kind::Series,
                "link-types" => Kind::LinkType,
                "fields" => Kind::Field,
                "enums" => Kind::Enum,
                "boards" => Kind::Board,
                "tasks" => Kind::Task,
                other => panic!("unknown folder {other}"),
            };
            (kind, Some(Uuid::parse_str(id).unwrap()))
        }
        other => panic!("unexpected path {other:?}"),
    }
}

/// Разбор и обратная запись файла своего вида: (id сущности, версия, байты после serialize).
fn round_trip(kind: Kind, project: Option<Uuid>, bytes: &[u8], path: &Path) -> Result<(Uuid, String, Vec<u8>), Error> {
    let p = project.unwrap_or_default();
    Ok(match kind {
        Kind::Project => {
            let v = files::project::parse(bytes, path)?;
            (v.model.id, v.version, files::project::serialize(&v.model))
        }
        Kind::User => {
            let v = files::user::parse(bytes, path)?;
            (v.model.id, v.version, files::user::serialize(&v.model))
        }
        Kind::Status => {
            let v = files::status::parse(p, bytes, path)?;
            (v.model.id, v.version, files::status::serialize(&v.model))
        }
        Kind::StatusSet => {
            let v = files::status_set::parse(p, bytes, path)?;
            (v.model.id, v.version, files::status_set::serialize(&v.model))
        }
        Kind::TaskType => {
            let v = files::task_type::parse(p, bytes, path)?;
            (v.model.id, v.version, files::task_type::serialize(&v.model))
        }
        Kind::Series => {
            let v = files::series::parse(p, bytes, path)?;
            (v.model.id, v.version, files::series::serialize(&v.model))
        }
        Kind::LinkType => {
            let v = files::link_type::parse(p, bytes, path)?;
            (v.model.id, v.version, files::link_type::serialize(&v.model))
        }
        Kind::Field => {
            let v = files::field::parse(p, bytes, path)?;
            (v.model.id, v.version, files::field::serialize(&v.model))
        }
        Kind::Enum => {
            let v = files::field_enum::parse(p, bytes, path)?;
            (v.model.id, v.version, files::field_enum::serialize(&v.model))
        }
        Kind::Board => {
            let v = files::board::parse(p, bytes, path)?;
            (v.model.id, v.version, files::board::serialize(&v.model))
        }
        Kind::Task => {
            let v = files::task::parse(p, bytes, path)?;
            (v.model.id, v.version, files::task::serialize(&v.model))
        }
    })
}

fn versions(path: &Path) -> BTreeMap<String, String> {
    serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap()
}

/// Файл области после `tasker migrate`: в той же папке, с тем же id внутри (имя могло измениться).
fn migrated_file(relative: &str, id: &Uuid) -> (PathBuf, Vec<u8>) {
    let folder = migrated().join(Path::new(relative).parent().unwrap());
    let needle = format!("id: {id}");
    all_yaml_files(&folder)
        .into_iter()
        .map(|f| (f.clone(), std::fs::read(&f).unwrap()))
        .find(|(_, bytes)| String::from_utf8_lossy(bytes).contains(&needle))
        .unwrap_or_else(|| panic!("no migrated file for {relative}"))
}

fn is_current_lf(bytes: &[u8], text: &str) -> bool {
    !bytes.starts_with(BOM) && !bytes.contains(&b'\r') && text.starts_with(&format!("formatVersion: {}\n", format::CURRENT))
}

/// Каждый файл области: parse → serialize даёт те же байты (текущий формат, LF, без BOM) либо, для старых, CRLF и BOM файлов,
/// совпадает с файлом после `tasker migrate`; версия до записи — из `versions.json`.
#[test]
fn every_workspace_file_round_trips_byte_for_byte() {
    let root = workspace();
    let expected_versions = versions(&golden().join("versions.json"));
    let migrated_versions = versions(&golden().join("expected/migrate/versions.json"));
    let (mut exact, mut via_migrate, mut failures) = (0, 0, Vec::new());
    for file in all_yaml_files(&root) {
        let rel = relative(&root, &file);
        let (kind, project) = classify(&rel);
        let bytes = std::fs::read(&file).unwrap();
        let text = String::from_utf8_lossy(bytes.strip_prefix(BOM).unwrap_or(&bytes)).into_owned();
        let result = round_trip(kind, project, &bytes, &file);
        if rel.ends_with("conflicted-title-ours-cccccccc.yaml") {
            assert_eq!(result.unwrap_err(), Error::MergeConflict { line: 3 }, "{rel}");
            continue;
        }
        if rel.ends_with("from-the-future-ffffffff.yaml") {
            let error = result.unwrap_err();
            assert_eq!(
                error.message(),
                "from-the-future-ffffffff.yaml: format version 99 is newer than this Tasker supports (9): update Tasker"
            );
            assert!(matches!(error, Error::UnsupportedFormat(_)));
            continue;
        }
        let (id, version, serialized) = result.unwrap_or_else(|e| panic!("{rel}: {e}"));
        assert_eq!(Some(&version), expected_versions.get(&rel), "{rel}: version");
        if is_current_lf(&bytes, &text) {
            exact += 1;
            if serialized != bytes {
                failures.push(format!(
                    "{rel}\n--- expected\n{text}--- actual\n{}",
                    String::from_utf8_lossy(&serialized)
                ));
            }
            continue;
        }
        // Старый формат, CRLF или BOM: tasker migrate меняет только номер версии (и убирает BOM), сохраняя окончания строк.
        via_migrate += 1;
        let (migrated_path, migrated_bytes) = migrated_file(&rel, &id);
        let upgraded = format::upgrade(&text, &file).unwrap();
        assert_eq!(
            upgraded.as_bytes(),
            migrated_bytes.as_slice(),
            "{rel}: upgrade in memory vs tasker migrate"
        );
        let migrated_rel = relative(&migrated(), &migrated_path);
        assert_eq!(
            Some(&tasker_core::versioning::version_of(&migrated_bytes)),
            migrated_versions.get(&migrated_rel),
            "{migrated_rel}: version after migrate"
        );
        // Запись из модели: те же байты, что и у мигрированного файла в LF, — кроме типов связей до формата 9, где запись
        // добавляет выведенные по умолчанию `allowCycles`/`hierarchical` (так же пишет и .NET при первой правке).
        let (_, _, from_migrated) = round_trip(kind, project, &migrated_bytes, &migrated_path).unwrap();
        assert_eq!(serialized, from_migrated, "{rel}: serialize(legacy) vs serialize(migrated)");
        if kind != Kind::LinkType {
            let migrated_lf = String::from_utf8_lossy(&migrated_bytes).replace("\r\n", "\n");
            assert_eq!(
                String::from_utf8_lossy(&serialized),
                migrated_lf,
                "{rel}: serialize vs migrated file"
            );
        }
    }
    assert!(failures.is_empty(), "{} files differ:\n{}", failures.len(), failures.join("\n"));
    assert!(exact >= 100, "only {exact} current-format files checked");
    assert_eq!(via_migrate, 16, "legacy files checked via tasker migrate");
}

/// Файлы после `tasker migrate` (все в текущем формате) тоже читаются и пишутся байт в байт (кроме CRLF-файла и типов связей
/// без признаков — см. выше).
#[test]
fn migrated_workspace_files_round_trip() {
    let root = migrated();
    let mut checked = 0;
    for file in all_yaml_files(&root) {
        let rel = relative(&root, &file);
        let bytes = std::fs::read(&file).unwrap();
        if bytes.contains(&b'\r') || rel.contains("cccccccc") || rel.contains("ffffffff") {
            continue;
        }
        let (kind, project) = classify(&rel);
        let (_, _, serialized) = round_trip(kind, project, &bytes, &file).unwrap_or_else(|e| panic!("{rel}: {e}"));
        let text = String::from_utf8_lossy(&bytes);
        if kind == Kind::LinkType && !text.contains("hierarchical:") {
            assert!(String::from_utf8_lossy(&serialized).contains("\nhierarchical: false\n"), "{rel}");
            continue;
        }
        assert_eq!(String::from_utf8_lossy(&serialized), text, "{rel}");
        checked += 1;
    }
    assert!(checked >= 110, "only {checked} files checked");
}

/// Значения по умолчанию при чтении старых файлов и особые правила моделей.
#[test]
fn legacy_defaults_and_model_rules_match_dotnet() {
    let legacy = Uuid::parse_str("22222222-2222-4222-8222-222222222222").unwrap();
    let folder = workspace().join("projects/22222222-2222-4222-8222-222222222222");
    let read_link = |name: &str| {
        let path = folder.join("link-types").join(name);
        files::link_type::parse(legacy, &std::fs::read(&path).unwrap(), &path)
            .unwrap()
            .model
    };
    // Формат 6, без allowCycles: Blocks циклы запрещает, остальные допускают; hierarchical — false.
    let blocks = read_link("blocks-6d62a7a2.yaml");
    assert_eq!(blocks.id, default_link_type_id(&legacy, "blocks"));
    assert!(!blocks.allow_cycles && !blocks.hierarchical);
    let relates = read_link("relates-6cad151c.yaml");
    assert!(relates.allow_cycles && !relates.hierarchical);
    assert_eq!(relates.inward_name, "relates to");
    // Формат 8: allowCycles есть, hierarchical нет.
    let parent = read_link("parent-child-c8c8fe73.yaml");
    assert!(!parent.allow_cycles && !parent.hierarchical);

    // Без inwardName — равен outwardName; без формата — версия 0 читается.
    let path = Path::new("link-types/x.yaml");
    let bare = files::link_type::parse(
        legacy,
        b"id: 6d62a7a2-ae30-a532-8d8c-cfd3591b38ee\nname: X\noutwardName: out\n",
        path,
    )
    .unwrap();
    assert_eq!(bare.model.inward_name, "out");
    assert!(!bare.model.allow_cycles);

    // Повтор одной связи у задачи схлопывается; неизвестные ключи игнорируются; описание без значения — None.
    let golden = Uuid::parse_str("11111111-1111-4111-8111-111111111111").unwrap();
    let task = files::task::parse(
        golden,
        b"formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\ntitle: T\ntypeId: 00000002-0000-4000-8000-000000000002\nstatusId: 00000003-0000-4000-8000-000000000003\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\nlinks:\n- typeId: 00000004-0000-4000-8000-000000000004\n  taskId: 00000005-0000-4000-8000-000000000005\n- typeId: 00000004-0000-4000-8000-000000000004\n  taskId: 00000005-0000-4000-8000-000000000005\nfutureKey: ignored\n",
        Path::new("tasks/t.yaml"),
    )
    .unwrap();
    assert_eq!(task.model.links.len(), 1);
    assert_eq!(task.model.description, None);
    assert_eq!(task.model.project_id, golden);
    assert!(
        String::from_utf8_lossy(&files::task::serialize(&task.model))
            .matches("taskId:")
            .count()
            == 1
    );

    // Неизвестный тип поля и оператор фильтра — «обновите Tasker».
    let field = files::field::parse(
        golden,
        b"formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\nname: F\ntype: money\n",
        Path::new("fields/f.yaml"),
    );
    assert_eq!(
        field.unwrap_err(),
        Error::UnsupportedFormat("f.yaml: unknown field type 'money': update Tasker".into())
    );
    let own = files::task::parse(
        golden,
        b"formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\ntitle: T\nfields:\n- id: 00000002-0000-4000-8000-000000000002\n  name: Own\n  type: Money\n",
        Path::new("tasks/t.yaml"),
    );
    assert_eq!(
        own.unwrap_err(),
        Error::UnsupportedFormat("t.yaml: unknown field type 'Money': update Tasker".into())
    );
    let board = files::board::parse(
        golden,
        b"formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\nname: B\ncolumns:\n- id: 00000002-0000-4000-8000-000000000002\n  name: C\n  fieldFilters:\n  - field: 00000003-0000-4000-8000-000000000003\n    op: between\n",
        Path::new("boards/b.yaml"),
    );
    assert_eq!(
        board.unwrap_err(),
        Error::UnsupportedFormat("b.yaml: unknown field filter operator 'between': update Tasker".into())
    );
    // Тип поля и оператор — без учёта регистра.
    let field = files::field::parse(
        golden,
        b"formatVersion: 9\nid: 00000001-0000-4000-8000-000000000001\nname: F\ntype: Int\n",
        Path::new("fields/f.yaml"),
    )
    .unwrap();
    assert_eq!(field.model.field_type, FieldType::Int);

    // Пользователь: kind без учёта регистра; человек — без ключа.
    let users = workspace().join("users");
    let agent = files::user::parse(
        &std::fs::read(users.join("00000083-0000-4000-8000-000000000083.yaml")).unwrap(),
        Path::new("u.yaml"),
    )
    .unwrap();
    assert_eq!(agent.model.kind, UserKind::Agent);
    let human = files::user::parse(
        b"id: 00000083-0000-4000-8000-000000000083\nusername: x\nkind: Human\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\n",
        Path::new("u.yaml"),
    )
    .unwrap();
    assert_eq!(human.model.kind, UserKind::Human);
    assert!(!String::from_utf8_lossy(&files::user::serialize(&human.model)).contains("kind:"));
}

#[derive(serde::Deserialize)]
struct Probe {
    value: String,
    yaml: String,
    file: String,
}

/// Каждая строка-проба корпуса (`yaml-style/*.json`): `key:` + скаляр = строка файла, записанная YamlDotNet 18.1.
#[test]
fn scalars_match_yamldotnet_style_probes() {
    let mut failures = Vec::new();
    let mut checked = 0;
    for (name, key) in [("titles.json", "title"), ("descriptions.json", "description")] {
        let probes: Vec<Probe> = serde_json::from_str(&std::fs::read_to_string(golden().join("yaml-style").join(name)).unwrap()).unwrap();
        for probe in probes {
            let mut out = String::from(key);
            out.push(':');
            write_scalar(&mut out, &probe.value, 2);
            // Эталон снят без завершающих переводов строки.
            let out = out.trim_end_matches('\n').to_string();
            checked += 1;
            if out != probe.yaml {
                failures.push(format!(
                    "{} {:?}\n  expected: {:?}\n  actual:   {:?}",
                    probe.file, probe.value, probe.yaml, out
                ));
            }
        }
    }
    assert!(checked >= 70, "only {checked} probes");
    assert!(failures.is_empty(), "{} mismatches:\n{}", failures.len(), failures.join("\n"));
}
