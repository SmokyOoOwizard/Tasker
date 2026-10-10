//! Колонка `files.data` — сущность целиком в JSON, как её пишет .NET (`IndexJsonContext`, System.Text.Json с
//! `JsonSerializerDefaults.General`): индекс `.cache/index.db` общий у .NET- и Rust-сборки (TSK-161), поэтому строку, записанную
//! одной, читает другая, и обе пишут её байт в байт одинаково.
//!
//! Правила System.Text.Json, которые здесь повторены: имена свойств — как в C# (PascalCase), порядок — порядок объявления
//! свойств записи (у позиционных записей — параметров), включая вычисляемые свойства только для чтения (`User.IsAgent`,
//! `LinkType.IsSymmetric`); null пишется (`"Description":null`); enum — числом; `DateTimeOffset` — `O` без хвостовых нулей дробной
//! части ([`Timestamp::format_json`]); Guid — `D` строчными; словарь `IReadOnlyDictionary<Guid, Guid>` — объект в порядке
//! вставки; строки экранируются `JavaScriptEncoder.Default` ([`write_string`]). При чтении лишние свойства (вычисляемые)
//! пропускаются, имена — с учётом регистра.
use crate::layout::EntityKind;
use serde::de::{DeserializeOwned, MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use std::fmt::Write as _;
use tasker_core::Timestamp;
use tasker_core::ids::guid_d;
use tasker_core::model::{
    Board, BoardColumn, ColumnFieldFilter, FieldDefinition, FieldEnum, FieldEnumValue, FieldOperator, FieldType, LinkType, OwnField,
    Project, Series, Status, StatusSet, TaskField, TaskItem, TaskLink, TaskSeriesNumber, TaskType, TaskTypeField, User, UserKind,
};
use uuid::Uuid;

/// Сущность, которую индекс хранит в `data`: вид файла и JSON .NET в обе стороны.
pub trait IndexEntity: Sized {
    const KIND: EntityKind;

    /// JSON для колонки `data` — байт в байт как `IndexJsonContext.Serialize` в .NET.
    fn to_data(&self) -> String;

    /// Разбор колонки `data` (`IndexJsonContext.Deserialize`).
    fn from_data(json: &str) -> serde_json::Result<Self>;
}

/// Строка JSON с экранированием `JavaScriptEncoder.Default`: всё вне ASCII — `\uXXXX` (вне BMP — суррогатной парой), из ASCII —
/// управляющие знаки и DEL, `"`, `&`, `'`, `+`, `<`, `>`, `` ` `` — тоже `\uXXXX` (hex заглавными); короткие формы — только
/// `\b \t \n \f \r` и `\\`.
pub fn write_string(out: &mut String, text: &str) {
    out.push('"');
    for c in text.chars() {
        match c {
            '\u{8}' => out.push_str("\\b"),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\u{c}' => out.push_str("\\f"),
            '\r' => out.push_str("\\r"),
            '\\' => out.push_str("\\\\"),
            '"' | '&' | '\'' | '+' | '<' | '>' | '`' | '\u{7f}' => escape(out, c as u32),
            c if (c as u32) < 0x20 => escape(out, c as u32),
            c if c.is_ascii() => out.push(c),
            c => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    escape(out, *unit as u32);
                }
            }
        }
    }
    out.push('"');
}

fn escape(out: &mut String, unit: u32) {
    let _ = write!(out, "\\u{unit:04X}");
}

/// Объект JSON: свойства через запятую в порядке вызовов.
struct Obj<'a> {
    out: &'a mut String,
    first: bool,
}

impl<'a> Obj<'a> {
    fn new(out: &'a mut String) -> Obj<'a> {
        out.push('{');
        Obj { out, first: true }
    }

    fn key(&mut self, name: &str) -> &mut String {
        if !self.first {
            self.out.push(',');
        }
        self.first = false;
        self.out.push('"');
        self.out.push_str(name);
        self.out.push_str("\":");
        self.out
    }

    fn str(&mut self, name: &str, value: &str) -> &mut Self {
        write_string(self.key(name), value);
        self
    }

    fn opt_str(&mut self, name: &str, value: Option<&str>) -> &mut Self {
        match value {
            Some(v) => self.str(name, v),
            None => self.raw(name, "null"),
        }
    }

    fn guid(&mut self, name: &str, id: &Uuid) -> &mut Self {
        let out = self.key(name);
        push_guid(out, id);
        self
    }

    fn opt_guid(&mut self, name: &str, id: Option<&Uuid>) -> &mut Self {
        match id {
            Some(id) => self.guid(name, id),
            None => self.raw(name, "null"),
        }
    }

    fn guids(&mut self, name: &str, ids: &[Uuid]) -> &mut Self {
        let out = self.key(name);
        out.push('[');
        for (i, id) in ids.iter().enumerate() {
            if i > 0 {
                out.push(',');
            }
            push_guid(out, id);
        }
        out.push(']');
        self
    }

    fn bool(&mut self, name: &str, value: bool) -> &mut Self {
        self.raw(name, if value { "true" } else { "false" })
    }

    fn int(&mut self, name: &str, value: i64) -> &mut Self {
        let out = self.key(name);
        let _ = write!(out, "{value}");
        self
    }

    fn time(&mut self, name: &str, value: &Timestamp) -> &mut Self {
        let text = value.format_json();
        let out = self.key(name);
        out.push('"');
        out.push_str(&text);
        out.push('"');
        self
    }

    fn raw(&mut self, name: &str, json: &str) -> &mut Self {
        self.key(name).push_str(json);
        self
    }

    /// Массив объектов: каждый пишет `item`.
    fn list<T>(&mut self, name: &str, items: &[T], item: impl Fn(&mut String, &T)) -> &mut Self {
        let out = self.key(name);
        out.push('[');
        for (i, x) in items.iter().enumerate() {
            if i > 0 {
                out.push(',');
            }
            item(out, x);
        }
        out.push(']');
        self
    }

    fn end(&mut self) {
        self.out.push('}');
    }
}

fn push_guid(out: &mut String, id: &Uuid) {
    out.push('"');
    out.push_str(&guid_d(id));
    out.push('"');
}

fn object(write: impl FnOnce(&mut Obj<'_>)) -> String {
    let mut out = String::with_capacity(256);
    let mut obj = Obj::new(&mut out);
    write(&mut obj);
    obj.end();
    out
}

fn nested(out: &mut String, write: impl FnOnce(&mut Obj<'_>)) {
    let mut obj = Obj::new(out);
    write(&mut obj);
    obj.end();
}

// Числа enum — порядок членов перечисления в C#.

fn field_type_code(t: FieldType) -> i64 {
    super::sync::field_type_code(t)
}

fn user_kind_code(kind: UserKind) -> i64 {
    match kind {
        UserKind::Human => 0,
        UserKind::Agent => 1,
    }
}

fn operator_code(op: FieldOperator) -> i64 {
    FieldOperator::ALL.iter().position(|x| *x == op).expect("all operators are listed") as i64
}

fn de_field_type<'de, D: Deserializer<'de>>(d: D) -> Result<FieldType, D::Error> {
    let code = i64::deserialize(d)?;
    super::sync::field_type_of_code(code).ok_or_else(|| serde::de::Error::custom(format!("unknown FieldType {code}")))
}

fn de_user_kind<'de, D: Deserializer<'de>>(d: D) -> Result<UserKind, D::Error> {
    match i64::deserialize(d)? {
        0 => Ok(UserKind::Human),
        1 => Ok(UserKind::Agent),
        code => Err(serde::de::Error::custom(format!("unknown UserKind {code}"))),
    }
}

fn de_operator<'de, D: Deserializer<'de>>(d: D) -> Result<FieldOperator, D::Error> {
    let code = i64::deserialize(d)?;
    usize::try_from(code)
        .ok()
        .and_then(|i| FieldOperator::ALL.get(i).copied())
        .ok_or_else(|| serde::de::Error::custom(format!("unknown FieldOperator {code}")))
}

/// Словарь Guid → Guid в порядке свойств объекта (`DropStatuses`).
fn de_pairs<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<(Uuid, Uuid)>, D::Error> {
    struct Pairs;
    impl<'de> Visitor<'de> for Pairs {
        type Value = Vec<(Uuid, Uuid)>;

        fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.write_str("an object of Guid to Guid")
        }

        fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Self::Value, A::Error> {
            let mut pairs = Vec::new();
            while let Some((key, value)) = map.next_entry::<Uuid, Uuid>()? {
                pairs.push((key, value));
            }
            Ok(pairs)
        }
    }
    d.deserialize_map(Pairs)
}

fn parse<T: DeserializeOwned>(json: &str) -> serde_json::Result<T> {
    serde_json::from_str(json)
}

// ---------------------------------------------------------------------------------------------------------------- Project

impl IndexEntity for Project {
    const KIND: EntityKind = EntityKind::Project;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .str("Name", &self.name)
                .time("CreatedAt", &self.created_at)
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            name: String,
            created_at: Timestamp,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(Project {
            id: d.id,
            name: d.name,
            created_at: d.created_at,
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- User

impl IndexEntity for User {
    const KIND: EntityKind = EntityKind::User;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .str("Username", &self.username)
                .int("Kind", user_kind_code(self.kind))
                .opt_guid("OwnerId", self.owner_id.as_ref())
                .opt_str("Email", self.email.as_deref())
                .bool("IsAdmin", self.is_admin)
                .time("CreatedAt", &self.created_at)
                .str("Version", &self.version)
                .bool("IsAgent", self.is_agent());
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            username: String,
            #[serde(deserialize_with = "de_user_kind")]
            kind: UserKind,
            owner_id: Option<Uuid>,
            email: Option<String>,
            is_admin: bool,
            created_at: Timestamp,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(User {
            id: d.id,
            username: d.username,
            kind: d.kind,
            owner_id: d.owner_id,
            email: d.email,
            is_admin: d.is_admin,
            created_at: d.created_at,
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- Status, StatusSet

impl IndexEntity for Status {
    const KIND: EntityKind = EntityKind::Status;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .str("Color", &self.color)
                .str("Description", &self.description)
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            color: String,
            description: String,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(Status {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            color: d.color,
            description: d.description,
            version: d.version,
        })
    }
}

impl IndexEntity for StatusSet {
    const KIND: EntityKind = EntityKind::StatusSet;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .guids("StatusIds", &self.status_ids)
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            status_ids: Vec<Uuid>,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(StatusSet {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            status_ids: d.status_ids,
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- TaskType

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct TaskTypeFieldData {
    field_id: Uuid,
    required: bool,
}

impl IndexEntity for TaskType {
    const KIND: EntityKind = EntityKind::TaskType;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .str("Description", &self.description)
                .guid("StatusSetId", &self.status_set_id)
                .list("Fields", &self.fields, |out, f| {
                    nested(out, |o| {
                        o.guid("FieldId", &f.field_id).bool("Required", f.required);
                    })
                })
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            description: String,
            status_set_id: Uuid,
            fields: Vec<TaskTypeFieldData>,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(TaskType {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            description: d.description,
            status_set_id: d.status_set_id,
            fields: d
                .fields
                .into_iter()
                .map(|f| TaskTypeField {
                    field_id: f.field_id,
                    required: f.required,
                })
                .collect(),
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- Board

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct ColumnFieldFilterData {
    field_id: Uuid,
    #[serde(deserialize_with = "de_operator")]
    operator: FieldOperator,
    value: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct BoardColumnData {
    id: Uuid,
    name: String,
    status_ids: Vec<Uuid>,
    field_conditions: Vec<ColumnFieldFilterData>,
    #[serde(deserialize_with = "de_pairs")]
    drop_statuses: Vec<(Uuid, Uuid)>,
}

fn write_column(out: &mut String, c: &BoardColumn) {
    nested(out, |o| {
        o.guid("Id", &c.id).str("Name", &c.name).guids("StatusIds", &c.status_ids).list(
            "FieldConditions",
            &c.field_conditions,
            |out, f| {
                nested(out, |o| {
                    o.guid("FieldId", &f.field_id)
                        .int("Operator", operator_code(f.operator))
                        .opt_str("Value", f.value.as_deref());
                })
            },
        );
        let out = o.key("DropStatuses");
        out.push('{');
        for (i, (set, status)) in c.drop_statuses.iter().enumerate() {
            if i > 0 {
                out.push(',');
            }
            push_guid(out, set);
            out.push(':');
            push_guid(out, status);
        }
        out.push('}');
    })
}

impl IndexEntity for Board {
    const KIND: EntityKind = EntityKind::Board;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .guids("StatusSetIds", &self.status_set_ids)
                .list("Columns", &self.columns, write_column)
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            status_set_ids: Vec<Uuid>,
            columns: Vec<BoardColumnData>,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(Board {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            status_set_ids: d.status_set_ids,
            columns: d
                .columns
                .into_iter()
                .map(|c| BoardColumn {
                    id: c.id,
                    name: c.name,
                    status_ids: c.status_ids,
                    field_conditions: c
                        .field_conditions
                        .into_iter()
                        .map(|f| ColumnFieldFilter {
                            field_id: f.field_id,
                            operator: f.operator,
                            value: f.value,
                        })
                        .collect(),
                    drop_statuses: c.drop_statuses,
                })
                .collect(),
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- Series

impl IndexEntity for Series {
    const KIND: EntityKind = EntityKind::Series;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .str("Prefix", &self.prefix)
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            prefix: String,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(Series {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            prefix: d.prefix,
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- TaskItem

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct TaskSeriesNumberData {
    series_id: Uuid,
    number: i32,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct TaskLinkData {
    type_id: Uuid,
    target_id: Uuid,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct OwnFieldData {
    name: String,
    #[serde(deserialize_with = "de_field_type")]
    r#type: FieldType,
    required: bool,
    multiple: bool,
    enum_id: Option<Uuid>,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct TaskFieldData {
    field_id: Uuid,
    values: Vec<String>,
    own: Option<OwnFieldData>,
}

fn write_task_field(out: &mut String, f: &TaskField) {
    nested(out, |o| {
        o.guid("FieldId", &f.field_id);
        {
            let out = o.key("Values");
            out.push('[');
            for (i, v) in f.values.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write_string(out, v);
            }
            out.push(']');
        }
        match &f.own {
            None => {
                o.raw("Own", "null");
            }
            Some(own) => {
                let out = o.key("Own");
                write_own(out, own);
            }
        }
    })
}

fn write_own(out: &mut String, own: &OwnField) {
    nested(out, |o| {
        o.str("Name", &own.name)
            .int("Type", field_type_code(own.field_type))
            .bool("Required", own.required)
            .bool("Multiple", own.multiple)
            .opt_guid("EnumId", own.enum_id.as_ref());
    })
}

impl IndexEntity for TaskItem {
    const KIND: EntityKind = EntityKind::Task;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Title", &self.title)
                .opt_str("Description", self.description.as_deref())
                .guid("TypeId", &self.type_id)
                .guid("StatusId", &self.status_id)
                .list("SeriesNumbers", &self.series_numbers, |out, n| {
                    nested(out, |o| {
                        o.guid("SeriesId", &n.series_id).int("Number", n.number as i64);
                    })
                })
                .list("Links", &self.links, |out, l| {
                    nested(out, |o| {
                        o.guid("TypeId", &l.type_id).guid("TargetId", &l.target_id);
                    })
                })
                .list("Fields", &self.fields, write_task_field)
                .time("CreatedAt", &self.created_at)
                .time("UpdatedAt", &self.updated_at)
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            title: String,
            description: Option<String>,
            type_id: Uuid,
            status_id: Uuid,
            series_numbers: Vec<TaskSeriesNumberData>,
            links: Vec<TaskLinkData>,
            fields: Vec<TaskFieldData>,
            created_at: Timestamp,
            updated_at: Timestamp,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(TaskItem {
            id: d.id,
            project_id: d.project_id,
            title: d.title,
            description: d.description,
            type_id: d.type_id,
            status_id: d.status_id,
            series_numbers: d
                .series_numbers
                .into_iter()
                .map(|n| TaskSeriesNumber {
                    series_id: n.series_id,
                    number: n.number,
                })
                .collect(),
            links: d
                .links
                .into_iter()
                .map(|l| TaskLink {
                    type_id: l.type_id,
                    target_id: l.target_id,
                })
                .collect(),
            fields: d
                .fields
                .into_iter()
                .map(|f| TaskField {
                    field_id: f.field_id,
                    values: f.values,
                    own: f.own.map(|o| OwnField {
                        name: o.name,
                        field_type: o.r#type,
                        required: o.required,
                        multiple: o.multiple,
                        enum_id: o.enum_id,
                    }),
                })
                .collect(),
            created_at: d.created_at,
            updated_at: d.updated_at,
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- LinkType

impl IndexEntity for LinkType {
    const KIND: EntityKind = EntityKind::LinkType;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .str("OutwardName", &self.outward_name)
                .str("InwardName", &self.inward_name)
                .str("Version", &self.version)
                .bool("AllowCycles", self.allow_cycles)
                .bool("Hierarchical", self.hierarchical)
                .bool("IsSymmetric", self.is_symmetric());
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            outward_name: String,
            inward_name: String,
            version: String,
            allow_cycles: bool,
            hierarchical: bool,
        }
        let d: D = parse(json)?;
        Ok(LinkType {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            outward_name: d.outward_name,
            inward_name: d.inward_name,
            allow_cycles: d.allow_cycles,
            hierarchical: d.hierarchical,
            version: d.version,
        })
    }
}

// ---------------------------------------------------------------------------------------------------------------- Field, FieldEnum

impl IndexEntity for FieldDefinition {
    const KIND: EntityKind = EntityKind::Field;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .int("Type", field_type_code(self.field_type))
                .bool("Multiple", self.multiple)
                .opt_guid("EnumId", self.enum_id.as_ref())
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            #[serde(deserialize_with = "de_field_type")]
            r#type: FieldType,
            multiple: bool,
            enum_id: Option<Uuid>,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(FieldDefinition {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            field_type: d.r#type,
            multiple: d.multiple,
            enum_id: d.enum_id,
            version: d.version,
        })
    }
}

impl IndexEntity for FieldEnum {
    const KIND: EntityKind = EntityKind::FieldEnum;

    fn to_data(&self) -> String {
        object(|o| {
            o.guid("Id", &self.id)
                .guid("ProjectId", &self.project_id)
                .str("Name", &self.name)
                .list("Values", &self.values, |out, v| {
                    nested(out, |o| {
                        o.guid("Id", &v.id).str("Name", &v.name);
                    })
                })
                .str("Version", &self.version);
        })
    }

    fn from_data(json: &str) -> serde_json::Result<Self> {
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct V {
            id: Uuid,
            name: String,
        }
        #[derive(Deserialize)]
        #[serde(rename_all = "PascalCase")]
        struct D {
            id: Uuid,
            project_id: Uuid,
            name: String,
            values: Vec<V>,
            version: String,
        }
        let d: D = parse(json)?;
        Ok(FieldEnum {
            id: d.id,
            project_id: d.project_id,
            name: d.name,
            values: d.values.into_iter().map(|v| FieldEnumValue { id: v.id, name: v.name }).collect(),
            version: d.version,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn string(text: &str) -> String {
        let mut out = String::new();
        write_string(&mut out, text);
        out
    }

    // Эталон — System.Text.Json (`JsonSerializer.Serialize(string)`, кодировщик по умолчанию) на .NET 10.
    #[test]
    fn strings_are_escaped_like_the_default_javascript_encoder() {
        assert_eq!(
            string("plain ASCII 123 ~!@#$%^*()_-=[]{}|;:,./?"),
            "\"plain ASCII 123 ~!@#$%^*()_-=[]{}|;:,./?\""
        );
        assert_eq!(string("\"&'+<>`\\"), "\"\\u0022\\u0026\\u0027\\u002B\\u003C\\u003E\\u0060\\\\\"");
        assert_eq!(
            string("\u{8}\t\n\u{b}\u{c}\r\u{0}\u{1f}\u{7f}"),
            "\"\\b\\t\\n\\u000B\\f\\r\\u0000\\u001F\\u007F\""
        );
        assert_eq!(
            string("é\u{a0}Ж\u{2028}\u{feff}\u{fffd}"),
            "\"\\u00E9\\u00A0\\u0416\\u2028\\uFEFF\\uFFFD\""
        );
        assert_eq!(string("😀"), "\"\\uD83D\\uDE00\"");
    }

    #[test]
    fn enums_are_numbers_in_declaration_order() {
        assert_eq!(operator_code(FieldOperator::Equal), 0);
        assert_eq!(operator_code(FieldOperator::Set), 6);
        assert_eq!(operator_code(FieldOperator::Detached), 9);
        assert_eq!(user_kind_code(UserKind::Agent), 1);
        assert_eq!(field_type_code(FieldType::Enum), 5);
    }

    #[test]
    fn a_task_round_trips_and_nulls_are_written() {
        let json = r#"{"Id":"00000001-0000-4000-8000-000000000001","ProjectId":"11111111-1111-4111-8111-111111111111","Title":"\u0422\u0435\u0441\u0442 \u0022q\u0022","Description":null,"TypeId":"00000002-0000-4000-8000-000000000002","StatusId":"00000003-0000-4000-8000-000000000003","SeriesNumbers":[{"SeriesId":"00000004-0000-4000-8000-000000000004","Number":7}],"Links":[],"Fields":[{"FieldId":"00000005-0000-4000-8000-000000000005","Values":["a","b"],"Own":{"Name":"Own","Type":5,"Required":true,"Multiple":false,"EnumId":null}}],"CreatedAt":"2026-01-01T00:00:00+00:00","UpdatedAt":"2026-01-01T00:01:01.12345+03:00","Version":"0123456789abcdef"}"#;
        let task = TaskItem::from_data(json).unwrap();
        assert_eq!(task.title, "Тест \"q\"");
        assert_eq!(task.fields[0].own.as_ref().unwrap().field_type, FieldType::Enum);
        assert_eq!(task.updated_at.offset_minutes, 180);
        assert_eq!(task.to_data(), json);
    }

    #[test]
    fn computed_properties_are_written_and_skipped_on_read() {
        let json = r#"{"Id":"00000001-0000-4000-8000-000000000001","Username":"bot","Kind":1,"OwnerId":"00000002-0000-4000-8000-000000000002","Email":null,"IsAdmin":false,"CreatedAt":"2026-01-01T00:00:00.1+00:00","Version":"v","IsAgent":true}"#;
        let user = User::from_data(json).unwrap();
        assert_eq!(user.kind, UserKind::Agent);
        assert_eq!(user.to_data(), json);
    }
}
