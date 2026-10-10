//! Обмен супервизора демона с рабочим процессом (.NET `WorkerProtocol.cs`, `WireMessage`): одна строка JSON на сообщение.
//! Супервизор пишет в stdin рабочего процесса, рабочий пишет в stdout строки с префиксом [`PREFIX`] (остальной вывод игнорируется).
//! Один тип на все сообщения, лишние поля неизвестны другой стороне — так сборки супервизора и рабочего процесса могут
//! отличаться на версию и даже на язык: .NET-супервизор с Rust-рабочим процессом и наоборот.
//!
//! - супервизор → рабочий: `hello` (секрет управления, порт), `activate` (открыть приём вызовов), `drain` (закончить начатое и
//!   выйти), `stop` (остановиться), `workers` (список рабочих процессов для статуса), `reply` (ответ на запрос);
//! - рабочий → супервизор: `ready` (области открыты, можно принимать вызовы), `active` (приём вызовов открыт), `request`
//!   (`upgrade` или `stop` из управления демоном).
//!
//! JSON — как `TaskerJson` с `WhenWritingNull`: camelCase, свойства в порядке объявления в C# (`type`, `id`, `op`, `ok`,
//! `message`, `token`, `port`, `supervisorPid`, `supervisorStartedAt`, `listenSocket`, `version`, `build`, `workspaces`,
//! `workers`, `upgrade`, `require`, `result`), null не пишутся ни на каком уровне, `id` (не nullable в C#) пишется всегда.
//! Чтение — имена без учёта регистра (`JsonSerializerDefaults.Web`); строка, которая не разбирается, пропускается.
use crate::state::{UpgradeRequest, UpgradeResult, WorkerStatus, WorkspaceStatus, optional_string, property};
use serde_json::{Map, Value};
use tasker_core::Timestamp;

/// Префикс строк рабочего процесса в stdout.
pub const PREFIX: &str = "@tasker ";

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct WireMessage {
    pub r#type: String,
    /// Номер запроса (`request`): ответ (`reply`) несёт тот же.
    pub id: i64,
    pub op: Option<String>,
    pub ok: Option<bool>,
    pub message: Option<String>,
    // hello
    pub token: Option<String>,
    pub port: Option<i32>,
    pub supervisor_pid: Option<i64>,
    pub supervisor_started_at: Option<Timestamp>,
    /// Слушающий сокет, если он передаётся сообщением, а не наследуется (Windows): см. `handoff`.
    pub listen_socket: Option<String>,
    // ready
    pub version: Option<String>,
    pub build: Option<String>,
    pub workspaces: Option<Vec<WorkspaceStatus>>,
    // workers
    pub workers: Option<Vec<WorkerStatus>>,
    // request upgrade / reply
    pub upgrade: Option<UpgradeRequest>,
    /// Пути областей, которые у старого процесса открыты: новый обязан их открыть, иначе замена откатывается.
    pub require: Option<Vec<String>>,
    pub result: Option<UpgradeResult>,
}

impl WireMessage {
    pub fn new(r#type: &str) -> WireMessage {
        WireMessage {
            r#type: r#type.to_string(),
            ..WireMessage::default()
        }
    }

    /// Строка JSON без перевода строки.
    pub fn to_line(&self) -> String {
        let mut map = Map::new();
        map.insert("type".into(), Value::String(self.r#type.clone()));
        map.insert("id".into(), Value::from(self.id));
        put(&mut map, "op", self.op.clone().map(Value::String));
        put(&mut map, "ok", self.ok.map(Value::Bool));
        put(&mut map, "message", self.message.clone().map(Value::String));
        put(&mut map, "token", self.token.clone().map(Value::String));
        put(&mut map, "port", self.port.map(Value::from));
        put(&mut map, "supervisorPid", self.supervisor_pid.map(Value::from));
        put(
            &mut map,
            "supervisorStartedAt",
            self.supervisor_started_at.map(|t| Value::String(t.format_json())),
        );
        put(&mut map, "listenSocket", self.listen_socket.clone().map(Value::String));
        put(&mut map, "version", self.version.clone().map(Value::String));
        put(&mut map, "build", self.build.clone().map(Value::String));
        put(
            &mut map,
            "workspaces",
            self.workspaces
                .as_ref()
                .map(|w| Value::Array(w.iter().map(|x| without_nulls(x.to_json())).collect())),
        );
        put(
            &mut map,
            "workers",
            self.workers
                .as_ref()
                .map(|w| Value::Array(w.iter().map(|x| without_nulls(x.to_json())).collect())),
        );
        put(&mut map, "upgrade", self.upgrade.as_ref().map(|u| without_nulls(u.to_json())));
        put(
            &mut map,
            "require",
            self.require
                .as_ref()
                .map(|r| Value::Array(r.iter().cloned().map(Value::String).collect())),
        );
        put(&mut map, "result", self.result.as_ref().map(|r| without_nulls(r.to_json())));
        tasker_core::json::to_string(&Value::Object(map))
    }

    /// Разбор строки; None — не JSON-объект или свойство не того типа (как `JsonException` в .NET: строка пропускается).
    pub fn parse(line: &str) -> Option<WireMessage> {
        let value: Value = serde_json::from_str(line).ok()?;
        value.as_object()?;
        let int = |name: &str| -> Option<Option<i64>> {
            match property(&value, name) {
                None | Some(Value::Null) => Some(None),
                Some(v) => v.as_i64().map(Some),
            }
        };
        let list = |name: &str| -> Option<Option<&Vec<Value>>> {
            match property(&value, name) {
                None | Some(Value::Null) => Some(None),
                Some(Value::Array(items)) => Some(Some(items)),
                Some(_) => None,
            }
        };
        let object = |name: &str| -> Option<Option<&Value>> {
            match property(&value, name) {
                None | Some(Value::Null) => Some(None),
                Some(v @ Value::Object(_)) => Some(Some(v)),
                Some(_) => None,
            }
        };
        Some(WireMessage {
            r#type: optional_string(&value, "type")?.unwrap_or_default(),
            id: int("id")?.unwrap_or(0),
            op: optional_string(&value, "op")?,
            ok: match property(&value, "ok") {
                None | Some(Value::Null) => None,
                Some(v) => Some(v.as_bool()?),
            },
            message: optional_string(&value, "message")?,
            token: optional_string(&value, "token")?,
            port: match int("port")? {
                None => None,
                Some(p) => Some(i32::try_from(p).ok()?),
            },
            supervisor_pid: int("supervisorPid")?,
            supervisor_started_at: match optional_string(&value, "supervisorStartedAt")? {
                None => None,
                Some(text) => Some(Timestamp::parse(&text)?),
            },
            listen_socket: optional_string(&value, "listenSocket")?,
            version: optional_string(&value, "version")?,
            build: optional_string(&value, "build")?,
            workspaces: match list("workspaces")? {
                None => None,
                Some(items) => Some(items.iter().map(WorkspaceStatus::from_json).collect::<Option<Vec<_>>>()?),
            },
            workers: match list("workers")? {
                None => None,
                Some(items) => Some(items.iter().map(WorkerStatus::from_json).collect::<Option<Vec<_>>>()?),
            },
            upgrade: match object("upgrade")? {
                None => None,
                Some(v) => Some(UpgradeRequest::from_json(v)?),
            },
            require: match list("require")? {
                None => None,
                Some(items) => Some(items.iter().map(|x| x.as_str().map(str::to_string)).collect::<Option<Vec<_>>>()?),
            },
            result: match object("result")? {
                None => None,
                Some(v) => Some(UpgradeResult::from_json(v)?),
            },
        })
    }
}

fn put(map: &mut Map<String, Value>, name: &str, value: Option<Value>) {
    if let Some(value) = value {
        map.insert(name.to_string(), value);
    }
}

/// `WhenWritingNull` для вложенных объектов.
fn without_nulls(value: Value) -> Value {
    match value {
        Value::Object(map) => Value::Object(map.into_iter().filter(|(_, v)| !v.is_null()).collect()),
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::{WorkerRole, WorkspaceState};
    use tasker_core::settings::WorkspaceKind;

    #[test]
    fn hello_is_written_like_the_dotnet_supervisor_writes_it() {
        let hello = WireMessage {
            token: Some("abc".into()),
            port: Some(5719),
            supervisor_pid: Some(42),
            supervisor_started_at: Timestamp::parse("2026-10-10T08:00:01.1234500+00:00"),
            ..WireMessage::new("hello")
        };
        assert_eq!(
            hello.to_line(),
            r#"{"type":"hello","id":0,"token":"abc","port":5719,"supervisorPid":42,"supervisorStartedAt":"2026-10-10T08:00:01.12345+00:00"}"#
        );
        assert_eq!(WireMessage::parse(&hello.to_line()), Some(hello));
        assert_eq!(WireMessage::new("activate").to_line(), r#"{"type":"activate","id":0}"#);
    }

    #[test]
    fn ready_omits_nulls_in_nested_workspaces() {
        let ready = WireMessage {
            version: Some("0.1.0".into()),
            build: Some("1a2b3c4d".into()),
            workspaces: Some(vec![
                WorkspaceStatus {
                    kind: WorkspaceKind::Files,
                    path: "/w/a".into(),
                    state: WorkspaceState::Open,
                    key: Some("a".into()),
                    error: None,
                },
                WorkspaceStatus {
                    kind: WorkspaceKind::Files,
                    path: "/w/b".into(),
                    state: WorkspaceState::Failed,
                    key: None,
                    error: Some("/w/b does not exist".into()),
                },
            ]),
            ..WireMessage::new("ready")
        };
        assert_eq!(
            ready.to_line(),
            r#"{"type":"ready","id":0,"version":"0.1.0","build":"1a2b3c4d","workspaces":[{"kind":"files","path":"/w/a","state":"open","key":"a"},{"kind":"files","path":"/w/b","state":"failed","error":"/w/b does not exist"}]}"#
        );
        assert_eq!(WireMessage::parse(&ready.to_line()), Some(ready));
    }

    #[test]
    fn upgrade_request_and_reply_round_trip() {
        let request = WireMessage {
            id: 7,
            op: Some("upgrade".into()),
            upgrade: Some(UpgradeRequest {
                file: "/bin/tasker-mcpd".into(),
                arguments: vec![],
                timeout_seconds: 60,
            }),
            require: Some(vec!["/w/a".into()]),
            ..WireMessage::new("request")
        };
        assert_eq!(
            request.to_line(),
            r#"{"type":"request","id":7,"op":"upgrade","upgrade":{"file":"/bin/tasker-mcpd","arguments":[],"timeoutSeconds":60},"require":["/w/a"]}"#
        );
        let reply = WireMessage {
            id: 7,
            ok: Some(false),
            message: Some("no".into()),
            result: Some(UpgradeResult {
                old_pid: Some(10),
                ..UpgradeResult::failed("no")
            }),
            ..WireMessage::new("reply")
        };
        assert_eq!(
            reply.to_line(),
            r#"{"type":"reply","id":7,"ok":false,"message":"no","result":{"ok":false,"message":"no","oldPid":10}}"#
        );
        assert_eq!(WireMessage::parse(&reply.to_line()), Some(reply));
    }

    #[test]
    fn workers_and_case_insensitive_reading() {
        let line = r#"{"Type":"workers","Id":0,"Workers":[{"Pid":5,"Version":"0.1.0","Build":"x","Role":"Active","StartedAt":"2026-10-10T08:00:00+00:00"}],"extra":1}"#;
        let message = WireMessage::parse(line).unwrap();
        assert_eq!(message.r#type, "workers");
        let workers = message.workers.unwrap();
        assert_eq!(workers[0].role, WorkerRole::Active);
        assert_eq!(workers[0].pid, 5);
        assert!(WireMessage::parse("not json").is_none());
        assert!(WireMessage::parse(r#"{"type":5}"#).is_none());
        assert!(WireMessage::parse("[]").is_none());
    }
}
