//! Чтение YAML через saphyr (событийный разбор `saphyr-parser`) в промежуточное дерево: все скаляры — текст, как их видит
//! YamlDotNet перед преобразованием в свойства модели. Типизация (Guid, число, bool, дата) — при переносе в модель, с теми же
//! правилами, что у десериализатора .NET: неизвестные ключи игнорируются (`IgnoreUnmatchedProperties`), отсутствующее значение
//! даёт значение по умолчанию у структур и null у ссылочных типов, плоские `null`/`~`/пустота — null.
use crate::error::{Error, Result};
use saphyr_parser::{Event, Parser, ScalarStyle, Span, SpannedEventReceiver};
use std::collections::HashMap;
use std::path::Path;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::time::Timestamp;
use uuid::Uuid;

/// Узел документа после разбора.
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    /// Скаляр: текст и признак плоского стиля (без кавычек и блока) — только у плоских `null`/`~`/пустых строк значение null.
    Scalar {
        text: String,
        plain: bool,
    },
    Seq(Vec<Value>),
    Map(Vec<(Value, Value)>),
}

impl Value {
    /// Текст скаляра; у коллекций None.
    pub fn text(&self) -> Option<&str> {
        match self {
            Value::Scalar { text, .. } => Some(text),
            _ => None,
        }
    }

    /// YamlDotNet: плоский скаляр `null`, `Null`, `NULL`, `~` или пустой — это null.
    pub fn is_null(&self) -> bool {
        matches!(self, Value::Scalar { text, plain: true } if matches!(text.as_str(), "" | "null" | "Null" | "NULL" | "~"))
    }
}

/// Разобранный документ: отображение верхнего уровня плюс имя файла для текстов ошибок.
#[derive(Debug, Clone, PartialEq)]
pub struct Document {
    pub root: Vec<(Value, Value)>,
    pub file: String,
}

impl Document {
    pub fn mapping(&self) -> Mapping<'_> {
        Mapping {
            entries: &self.root,
            file: &self.file,
        }
    }
}

/// Доступ к отображению по ключам с преобразованием в типы моделей файлов .NET.
#[derive(Clone, Copy)]
pub struct Mapping<'a> {
    entries: &'a [(Value, Value)],
    file: &'a str,
}

impl<'a> Mapping<'a> {
    pub fn file(&self) -> &'a str {
        self.file
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Пары «ключ — значение» в порядке файла (ключи-скаляры как текст).
    pub fn entries(&self) -> impl Iterator<Item = (&'a str, &'a Value)> {
        self.entries.iter().filter_map(|(k, v)| k.text().map(|k| (k, v)))
    }

    /// Значение ключа; повтор ключа — последнее значение. Null-скаляр считается отсутствующим.
    pub fn get(&self, key: &str) -> Option<&'a Value> {
        self.entries()
            .filter(|(k, _)| *k == key)
            .map(|(_, v)| v)
            .last()
            .filter(|v| !v.is_null())
    }

    fn error(&self, what: &str, key: &str, value: &Value) -> Error {
        let shown = value.text().unwrap_or("<collection>");
        Error::Yaml(format!("{}: {key}: {what} expected, got '{shown}'", self.file))
    }

    /// `string?`: текст скаляра, null или отсутствие ключа — None.
    pub fn str_opt(&self, key: &str) -> Result<Option<String>> {
        match self.get(key) {
            None => Ok(None),
            Some(v) => v.text().map(|t| Some(t.to_string())).ok_or_else(|| self.error("a string", key, v)),
        }
    }

    /// `Guid`: отсутствие — `Guid.Empty`, как у свойства-структуры без значения.
    pub fn guid(&self, key: &str) -> Result<Uuid> {
        Ok(self.guid_opt(key)?.unwrap_or(Uuid::nil()))
    }

    /// `Guid?`.
    pub fn guid_opt(&self, key: &str) -> Result<Option<Uuid>> {
        match self.get(key) {
            None => Ok(None),
            Some(v) => guid_of(v).map(Some).ok_or_else(|| self.error("a Guid", key, v)),
        }
    }

    /// `int`: отсутствие — 0.
    pub fn int(&self, key: &str) -> Result<i32> {
        match self.get(key) {
            None => Ok(0),
            Some(v) => v
                .text()
                .and_then(|t| t.trim().parse().ok())
                .ok_or_else(|| self.error("an integer", key, v)),
        }
    }

    /// `bool?`: `true`/`false` без учёта регистра.
    pub fn bool_opt(&self, key: &str) -> Result<Option<bool>> {
        match self.get(key) {
            None => Ok(None),
            Some(v) => match v.text().map(|t| t.trim().to_ascii_lowercase()).as_deref() {
                Some("true") => Ok(Some(true)),
                Some("false") => Ok(Some(false)),
                _ => Err(self.error("a boolean", key, v)),
            },
        }
    }

    /// `DateTimeOffset` (формат `O`): отсутствие — `default(DateTimeOffset)`, 0001-01-01T00:00:00+00:00.
    pub fn timestamp(&self, key: &str) -> Result<Timestamp> {
        match self.get(key) {
            None => Ok(Timestamp {
                year: 1,
                month: 1,
                day: 1,
                hour: 0,
                minute: 0,
                second: 0,
                ticks: 0,
                offset_minutes: 0,
            }),
            Some(v) => v
                .text()
                .and_then(|t| Timestamp::parse(t.trim()))
                .ok_or_else(|| self.error("a date and time", key, v)),
        }
    }

    /// `List<T>?`: элементы списка; None — ключа нет или null.
    pub fn seq(&self, key: &str) -> Result<Option<&'a [Value]>> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::Seq(items)) => Ok(Some(items)),
            Some(v) => Err(self.error("a list", key, v)),
        }
    }

    /// `List<Guid>?`.
    pub fn guids(&self, key: &str) -> Result<Option<Vec<Uuid>>> {
        self.seq(key)?
            .map(|items| {
                items
                    .iter()
                    .map(|v| guid_of(v).ok_or_else(|| self.error("a Guid", key, v)))
                    .collect()
            })
            .transpose()
    }

    /// `List<string?>?`: null-элементы — None.
    pub fn strings(&self, key: &str) -> Result<Option<Vec<Option<String>>>> {
        self.seq(key)?
            .map(|items| {
                items
                    .iter()
                    .map(|v| {
                        if v.is_null() {
                            Ok(None)
                        } else {
                            v.text().map(|t| Some(t.to_string())).ok_or_else(|| self.error("a string", key, v))
                        }
                    })
                    .collect()
            })
            .transpose()
    }

    /// `List<Model>?`: элементы — отображения.
    pub fn maps(&self, key: &str) -> Result<Option<Vec<Mapping<'a>>>> {
        self.seq(key)?
            .map(|items| {
                items
                    .iter()
                    .map(|v| match v {
                        Value::Map(entries) => Ok(Mapping { entries, file: self.file }),
                        other => Err(self.error("a mapping", key, other)),
                    })
                    .collect()
            })
            .transpose()
    }

    /// `Dictionary<Guid, Guid>?` с порядком файла.
    pub fn guid_pairs(&self, key: &str) -> Result<Option<Vec<(Uuid, Uuid)>>> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::Map(entries)) => entries
                .iter()
                .map(|(k, v)| match (guid_of(k), guid_of(v)) {
                    (Some(k), Some(v)) => Ok((k, v)),
                    (None, _) => Err(self.error("a Guid key", key, k)),
                    (_, None) => Err(self.error("a Guid", key, v)),
                })
                .collect::<Result<Vec<_>>>()
                .map(Some),
            Some(v) => Err(self.error("a mapping", key, v)),
        }
    }
}

fn guid_of(value: &Value) -> Option<Uuid> {
    value.text().and_then(parse_guid)
}

/// Guid в форме D (для ключей и значений записываемых файлов).
pub fn guid_text(id: &Uuid) -> String {
    guid_d(id)
}

/// Разбирает текст файла (уже без BOM и в текущем формате) в документ. Берётся первый документ потока; его корень — отображение.
pub fn read(text: &str, path: &Path) -> Result<Document> {
    let file = crate::format::file_name(path);
    let mut builder = Builder::default();
    let mut parser = Parser::new_from_str(text);
    parser.load(&mut builder, false).map_err(|e| {
        Error::Yaml(format!(
            "{file}: {} (line {}, column {})",
            e.info(),
            e.marker().line(),
            e.marker().col()
        ))
    })?;
    if let Some(error) = builder.error.take() {
        return Err(Error::Yaml(format!("{file}: {error}")));
    }
    match builder.document {
        Some(Value::Map(root)) => Ok(Document { root, file }),
        Some(_) => Err(Error::Yaml(format!("{file}: the document is not a mapping"))),
        None => Err(Error::Yaml(format!("{file}: the file is empty"))),
    }
}

enum Frame {
    Seq(Vec<Value>, usize),
    Map(Vec<(Value, Value)>, Option<Value>, usize),
}

#[derive(Default)]
struct Builder {
    stack: Vec<Frame>,
    document: Option<Value>,
    anchors: HashMap<usize, Value>,
    error: Option<String>,
}

impl Builder {
    fn push(&mut self, value: Value, anchor: usize) {
        if anchor != 0 {
            self.anchors.insert(anchor, value.clone());
        }
        match self.stack.last_mut() {
            Some(Frame::Seq(items, _)) => items.push(value),
            Some(Frame::Map(entries, pending, _)) => match pending.take() {
                None => *pending = Some(value),
                Some(key) => entries.push((key, value)),
            },
            None => {
                if self.document.is_none() {
                    self.document = Some(value);
                }
            }
        }
    }
}

impl<'input> SpannedEventReceiver<'input> for Builder {
    fn on_event(&mut self, event: Event<'input>, _span: Span) {
        if self.error.is_some() {
            return;
        }
        match event {
            Event::Scalar(text, style, anchor, _tag) => self.push(
                Value::Scalar {
                    text: text.into_owned(),
                    plain: style == ScalarStyle::Plain,
                },
                anchor,
            ),
            Event::SequenceStart(anchor, _) => self.stack.push(Frame::Seq(Vec::new(), anchor)),
            Event::MappingStart(anchor, _) => self.stack.push(Frame::Map(Vec::new(), None, anchor)),
            Event::SequenceEnd => {
                if let Some(Frame::Seq(items, anchor)) = self.stack.pop() {
                    self.push(Value::Seq(items), anchor);
                }
            }
            Event::MappingEnd => {
                if let Some(Frame::Map(entries, _, anchor)) = self.stack.pop() {
                    self.push(Value::Map(entries), anchor);
                }
            }
            Event::Alias(anchor) => match self.anchors.get(&anchor).cloned() {
                Some(value) => self.push(value, 0),
                None => self.error = Some("alias refers to an unknown anchor".into()),
            },
            Event::Nothing | Event::StreamStart | Event::StreamEnd | Event::DocumentStart(_) | Event::DocumentEnd => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn doc(text: &str) -> Document {
        read(text, Path::new("tasks/x.yaml")).unwrap()
    }

    #[test]
    fn scalars_are_text_and_plain_nulls_are_missing() {
        let d = doc(
            "id: 00000001-0000-4000-8000-000000000001\nname: \"007\"\nnum: 5\nflag: True\nempty:\ntilde: ~\nquoted: 'null'\nunknown: whatever\n",
        );
        let m = d.mapping();
        assert_eq!(
            m.guid("id").unwrap(),
            Uuid::parse_str("00000001-0000-4000-8000-000000000001").unwrap()
        );
        assert_eq!(m.str_opt("name").unwrap().as_deref(), Some("007"));
        assert_eq!(m.int("num").unwrap(), 5);
        assert_eq!(m.int("missing").unwrap(), 0);
        assert_eq!(m.bool_opt("flag").unwrap(), Some(true));
        assert_eq!(m.str_opt("empty").unwrap(), None);
        assert_eq!(m.str_opt("tilde").unwrap(), None);
        assert_eq!(m.str_opt("quoted").unwrap().as_deref(), Some("null"));
        assert_eq!(m.guid("missing").unwrap(), Uuid::nil());
        assert!(m.guid("name").is_err());
    }

    #[test]
    fn collections_keep_order_and_aliases_resolve() {
        let d = doc(
            "list:\n- a\n- \n- 'b'\nmap:\n  00000001-0000-4000-8000-000000000001: 00000002-0000-4000-8000-000000000002\nitems:\n- &x\n  k: v\n- *x\nnone: []\n",
        );
        let m = d.mapping();
        assert_eq!(
            m.strings("list").unwrap().unwrap(),
            vec![Some("a".to_string()), None, Some("b".to_string())]
        );
        assert_eq!(m.guid_pairs("map").unwrap().unwrap().len(), 1);
        let items = m.maps("items").unwrap().unwrap();
        assert_eq!(items.len(), 2);
        assert_eq!(items[1].str_opt("k").unwrap().as_deref(), Some("v"));
        assert_eq!(m.guids("none").unwrap().unwrap(), Vec::<Uuid>::new());
        assert!(m.seq("missing").unwrap().is_none());
    }

    #[test]
    fn broken_yaml_names_the_file() {
        let error = read("a: b\n c: d\n", Path::new("x.yaml")).unwrap_err();
        assert!(matches!(&error, Error::Yaml(m) if m.starts_with("x.yaml: ")), "{error}");
        assert!(matches!(read("", Path::new("x.yaml")).unwrap_err(), Error::Yaml(m) if m == "x.yaml: the file is empty"));
        assert!(matches!(read("- a\n", Path::new("x.yaml")).unwrap_err(), Error::Yaml(m) if m == "x.yaml: the document is not a mapping"));
    }
}
