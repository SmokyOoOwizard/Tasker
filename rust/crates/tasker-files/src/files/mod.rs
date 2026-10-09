//! Модели файлов `.tasker` и преобразование в сущности `tasker-core` и обратно — по одному модулю на вид файла, ровно с теми
//! ключами, порядком и правилами пропуска, что в `Tasker.Storage.Files/Storages/*File.cs`.
//!
//! Общее для всех видов (`YamlFile` в .NET): файл — UTF-8 (BOM обрезается), первая строка `formatVersion: N`; при чтении текст
//! старого формата приводится к текущему в памяти ([`crate::format::upgrade`]), версия сущности — хэш настоящих байтов файла
//! ([`tasker_core::versioning::version_of`]); запись — всегда текущий формат, LF, без BOM. Guid — форма D, `DateTimeOffset` —
//! формат `O`.
use crate::error::{Error, Result};
use crate::yaml::{self, Node, Value};
use std::path::Path;
use tasker_core::ids::guid_d;
use tasker_core::time::Timestamp;
use tasker_core::versioning::version_of;
use uuid::Uuid;

pub mod board;
pub mod field;
pub mod field_enum;
pub mod link_type;
pub mod project;
pub mod series;
pub mod status;
pub mod status_set;
pub mod task;
pub mod task_type;
pub mod user;

/// Модель и версия файла, из которого она прочитана (`Versioned<T>` в .NET).
#[derive(Debug, Clone, PartialEq)]
pub struct Versioned<T> {
    pub model: T,
    pub version: String,
}

const BOM: &[u8] = &[0xEF, 0xBB, 0xBF];

/// Байты файла → текст текущего формата + версия. Проверки по порядку, как в индексе .NET: маркеры конфликта git, версия формата
/// (не число / новее поддерживаемой), затем YAML.
pub(crate) fn read_document(bytes: &[u8], path: &Path) -> Result<(yaml::Document, String)> {
    let body = bytes.strip_prefix(BOM).unwrap_or(bytes);
    let text = String::from_utf8_lossy(body);
    if let Some(line) = crate::format::merge_conflict_line(&text) {
        return Err(Error::MergeConflict { line });
    }
    let upgraded = crate::format::upgrade(&text, path)?;
    let document = yaml::read(&upgraded, path)?;
    Ok((document, version_of(bytes)))
}

/// Записывает документ: `formatVersion: CURRENT` первой строкой, затем ключи модели.
pub(crate) fn write_document(entries: Vec<(String, Node)>) -> Vec<u8> {
    let mut root = Vec::with_capacity(entries.len() + 1);
    root.push(("formatVersion".to_string(), Node::Int(crate::format::CURRENT as i64)));
    root.extend(entries);
    yaml::emit(&root).into_bytes()
}

/// Строитель списка ключей файла: ключи с None пропускаются (`DefaultValuesHandling.OmitNull`).
#[derive(Default)]
pub(crate) struct Entries(Vec<(String, Node)>);

impl Entries {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push(mut self, key: &str, node: Node) -> Self {
        self.0.push((key.to_string(), node));
        self
    }

    pub fn opt(self, key: &str, node: Option<Node>) -> Self {
        match node {
            Some(node) => self.push(key, node),
            None => self,
        }
    }

    pub fn str(self, key: &str, value: &str) -> Self {
        self.push(key, Node::str(value))
    }

    pub fn str_opt(self, key: &str, value: Option<&str>) -> Self {
        self.opt(key, value.map(Node::str))
    }

    pub fn guid(self, key: &str, id: &Uuid) -> Self {
        self.push(key, Node::str(guid_d(id)))
    }

    pub fn guid_opt(self, key: &str, id: Option<&Uuid>) -> Self {
        self.opt(key, id.map(|id| Node::str(guid_d(id))))
    }

    pub fn guids(self, key: &str, ids: &[Uuid]) -> Self {
        self.push(key, Node::Seq(ids.iter().map(|id| Node::str(guid_d(id))).collect()))
    }

    pub fn timestamp(self, key: &str, value: &Timestamp) -> Self {
        self.push(key, Node::str(value.format_o()))
    }

    /// `bool` — всегда.
    pub fn bool(self, key: &str, value: bool) -> Self {
        self.push(key, Node::Bool(value))
    }

    /// `bool?`, выставленный только когда истина (`x ? true : null`).
    pub fn bool_if(self, key: &str, value: bool) -> Self {
        self.opt(key, value.then_some(Node::Bool(true)))
    }

    /// Список отображений; пустой список — ключ пропускается (`Count == 0 ? null : …`).
    pub fn maps_if_any(self, key: &str, items: Vec<Entries>) -> Self {
        if items.is_empty() {
            self
        } else {
            self.push(key, Node::Seq(items.into_iter().map(|e| Node::Map(e.0)).collect()))
        }
    }

    pub fn into_node(self) -> Node {
        Node::Map(self.0)
    }

    pub fn into_vec(self) -> Vec<(String, Node)> {
        self.0
    }
}

/// Поле модели, которого в файле нет, — пустая строка (`?? ""`).
pub(crate) fn or_empty(value: Option<String>) -> String {
    value.unwrap_or_default()
}

pub(crate) fn text_or_empty(value: &Value) -> &str {
    value.text().unwrap_or("")
}
