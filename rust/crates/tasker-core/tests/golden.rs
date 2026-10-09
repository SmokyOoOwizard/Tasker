//! Проверки `tasker-core` на golden-корпусе (`rust/tests/golden`, TSK-124): версии файлов, id типов связей по умолчанию,
//! канонический вид значений полей, формат временных меток. YAML здесь читается построчно — парсер появится в `tasker-files`.
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use tasker_core::fields;
use tasker_core::ids::{DEFAULT_LINK_TYPES, default_link_type_id};
use tasker_core::model::{FieldEnum, FieldEnumValue, FieldType};
use tasker_core::time::Timestamp;
use tasker_core::versioning;
use uuid::Uuid;

const GOLDEN: &str = "11111111-1111-4111-8111-111111111111";
const LEGACY: &str = "22222222-2222-4222-8222-222222222222";

fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
}

fn workspace() -> PathBuf {
    golden().join("workspace/.tasker")
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

fn all_yaml_files() -> Vec<PathBuf> {
    let mut files = Vec::new();
    yaml_files(&workspace(), &mut files);
    files.sort();
    files
}

fn read(path: &Path) -> String {
    let bytes = std::fs::read(path).unwrap();
    let text = String::from_utf8_lossy(&bytes);
    text.replace("\r\n", "\n").trim_start_matches('\u{FEFF}').to_string()
}

/// Значение скаляра YAML так, как его пишет Tasker: без кавычек или в двойных/одинарных без экранирования.
fn scalar(text: &str) -> String {
    let t = text.trim();
    if t.len() >= 2 && ((t.starts_with('"') && t.ends_with('"')) || (t.starts_with('\'') && t.ends_with('\''))) {
        t[1..t.len() - 1].to_string()
    } else {
        t.to_string()
    }
}

/// Верхнеуровневое `key: value`.
fn top_level(text: &str, key: &str) -> Option<String> {
    text.lines()
        .find_map(|l| l.strip_prefix(key).and_then(|r| r.strip_prefix(": ")).map(scalar))
}

/// Элементы списка `key:` с отступом `indent`: каждый элемент — набор `k: v` плюс вложенный список `values:`.
fn list_items(lines: &[&str], start: usize, indent: usize) -> Vec<(BTreeMap<String, String>, Vec<String>)> {
    let pad = " ".repeat(indent);
    let mut items: Vec<(BTreeMap<String, String>, Vec<String>)> = Vec::new();
    let mut in_values = false;
    for line in &lines[start..] {
        if line.is_empty() || (!line.starts_with(&pad) && indent > 0) || (indent == 0 && !line.starts_with("- ") && !line.starts_with("  "))
        {
            break;
        }
        let rest = &line[indent..];
        if let Some(first) = rest.strip_prefix("- ") {
            if in_values && rest.starts_with("- ") && items.last().is_some_and(|(m, _)| m.contains_key("values")) && !first.contains(": ") {
                items.last_mut().unwrap().1.push(scalar(first));
                continue;
            }
            in_values = false;
            let mut map = BTreeMap::new();
            if let Some((k, v)) = first.split_once(": ") {
                map.insert(k.to_string(), scalar(v));
            }
            items.push((map, Vec::new()));
        } else if let Some(inner) = rest.strip_prefix("  ") {
            let Some((map, values)) = items.last_mut() else { break };
            if inner == "values:" {
                map.insert("values".into(), String::new());
                in_values = true;
            } else if let Some(v) = inner.strip_prefix("- ") {
                values.push(scalar(v));
            } else if let Some((k, v)) = inner.split_once(": ") {
                map.insert(k.to_string(), scalar(v));
            }
        } else {
            break;
        }
    }
    items
}

fn section(text: &str, key: &str) -> Vec<(BTreeMap<String, String>, Vec<String>)> {
    let lines: Vec<&str> = text.lines().collect();
    match lines.iter().position(|l| *l == format!("{key}:")) {
        Some(i) => list_items(&lines, i + 1, 0),
        None => Vec::new(),
    }
}

#[test]
fn versions_of_every_file_match_versions_json() {
    let expected: BTreeMap<String, String> =
        serde_json::from_str(&std::fs::read_to_string(golden().join("versions.json")).unwrap()).unwrap();
    let root = workspace();
    let mut seen = 0;
    for file in all_yaml_files() {
        let relative = file.strip_prefix(&root).unwrap().to_string_lossy().replace('\\', "/");
        let version = versioning::version_of(&std::fs::read(&file).unwrap());
        assert_eq!(expected.get(&relative).map(String::as_str), Some(version.as_str()), "{relative}");
        seen += 1;
    }
    assert_eq!(seen, expected.len(), "every entry of versions.json has a file");
    assert!(seen > 100);
}

#[test]
fn default_link_type_ids_follow_the_md5_rule() {
    let mut checked = 0;
    for project in [GOLDEN, LEGACY] {
        let project_id = Uuid::parse_str(project).unwrap();
        let dir = workspace().join("projects").join(project).join("link-types");
        for entry in std::fs::read_dir(&dir).unwrap() {
            let text = read(&entry.unwrap().path());
            let name = top_level(&text, "name").unwrap();
            let id = Uuid::parse_str(&top_level(&text, "id").unwrap()).unwrap();
            if let Some(default) = DEFAULT_LINK_TYPES.iter().find(|t| t.name == name) {
                assert_eq!(default_link_type_id(&project_id, default.key), id, "{project} {name}");
                checked += 1;
            }
        }
    }
    assert_eq!(checked, 12, "six default link types in each of two projects");
}

struct Catalog {
    fields: BTreeMap<Uuid, (String, FieldType, bool, Option<Uuid>)>,
    enums: BTreeMap<Uuid, FieldEnum>,
}

fn catalog(project: &str) -> Catalog {
    let base = workspace().join("projects").join(project);
    let project_id = Uuid::parse_str(project).unwrap();
    let mut enums = BTreeMap::new();
    for entry in std::fs::read_dir(base.join("enums")).unwrap() {
        let text = read(&entry.unwrap().path());
        let id = Uuid::parse_str(&top_level(&text, "id").unwrap()).unwrap();
        let values = section(&text, "values")
            .into_iter()
            .map(|(m, _)| FieldEnumValue {
                id: Uuid::parse_str(&m["id"]).unwrap(),
                name: m["name"].clone(),
            })
            .collect();
        enums.insert(
            id,
            FieldEnum {
                id,
                project_id,
                name: top_level(&text, "name").unwrap(),
                values,
                version: String::new(),
            },
        );
    }
    let mut fields = BTreeMap::new();
    for entry in std::fs::read_dir(base.join("fields")).unwrap() {
        let text = read(&entry.unwrap().path());
        let id = Uuid::parse_str(&top_level(&text, "id").unwrap()).unwrap();
        fields.insert(id, field_of(&text, |k| top_level(&text, k)));
    }
    Catalog { fields, enums }
}

fn field_of(_: &str, get: impl Fn(&str) -> Option<String>) -> (String, FieldType, bool, Option<Uuid>) {
    (
        get("name").unwrap(),
        FieldType::parse(&get("type").unwrap()).unwrap(),
        get("multiple").as_deref() == Some("true"),
        get("enum").map(|e| Uuid::parse_str(&e).unwrap()),
    )
}

#[test]
fn stored_field_values_are_already_canonical() {
    let catalog = catalog(GOLDEN);
    let mut checked = 0;
    for entry in std::fs::read_dir(workspace().join("projects").join(GOLDEN).join("tasks")).unwrap() {
        let text = read(&entry.unwrap().path());
        for (item, values) in section(&text, "fields") {
            let field_id = Uuid::parse_str(&item["id"]).unwrap();
            let (name, field_type, multiple, enum_id) = if item.contains_key("type") {
                field_of(&text, |k| item.get(k).cloned())
            } else {
                catalog
                    .fields
                    .get(&field_id)
                    .cloned()
                    .unwrap_or_else(|| panic!("field {field_id} of {}", top_level(&text, "title").unwrap()))
            };
            let enumeration = enum_id.and_then(|e| catalog.enums.get(&e));
            let raw: Vec<Option<String>> = values.iter().cloned().map(Some).collect();
            let normalized = fields::normalize(&name, field_type, multiple, enumeration, &raw).unwrap_or_else(|e| panic!("{name}: {e}"));
            assert_eq!(normalized, values, "{name}");
            checked += values.len();
        }
    }
    assert!(checked >= 20, "checked {checked} values");
}

#[test]
fn timestamps_round_trip_in_o_format_and_match_json_output() {
    let mut by_task: BTreeMap<String, (String, String)> = BTreeMap::new();
    let mut checked = 0;
    for file in all_yaml_files() {
        let text = read(&file);
        for key in ["createdAt", "updatedAt"] {
            if let Some(value) = top_level(&text, key) {
                let parsed = Timestamp::parse(&value).unwrap_or_else(|| panic!("{}: {value}", file.display()));
                assert_eq!(parsed.format_o(), value, "{}", file.display());
                checked += 1;
            }
        }
        if file.parent().is_some_and(|p| p.ends_with("tasks"))
            && let (Some(id), Some(c), Some(u)) = (top_level(&text, "id"), top_level(&text, "createdAt"), top_level(&text, "updatedAt"))
        {
            by_task.insert(id, (c, u));
        }
    }
    assert!(checked > 100);

    let json: Value =
        serde_json::from_str(&std::fs::read_to_string(golden().join("expected/cli/053-task-list-json.out")).unwrap()).unwrap();
    let tasks = json["data"].as_array().unwrap();
    assert!(!tasks.is_empty());
    for task in tasks {
        let id = task["id"].as_str().unwrap();
        let (created, updated) = &by_task[id];
        assert_eq!(
            Timestamp::parse(created).unwrap().format_json(),
            task["createdAt"].as_str().unwrap(),
            "{id}"
        );
        assert_eq!(
            Timestamp::parse(updated).unwrap().format_json(),
            task["updatedAt"].as_str().unwrap(),
            "{id}"
        );
    }
}
