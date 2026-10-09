//! `DaemonFiles` (.NET): файлы состояния демона в `<data>/mcp` — блокировка единственного экземпляра `daemon.lock` и
//! `daemon.json` с адресом и секретом управления. Их читают консоль (`tasker mcp status|stop|sync`) и десктоп, в том числе
//! .NET-сборки, поэтому форма и права файлов повторяются буквально.
use std::io;
use std::path::{Path, PathBuf};
use std::time::Duration;
use tasker_core::Timestamp;
use tasker_core::io::FileLock;
use tasker_core::settings::daemon_dir;

/// Как подключиться к запущенному демону: процесс, порт и секрет управления. Файл читает только владелец.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DaemonInfo {
    pub pid: u32,
    pub port: i32,
    pub token: String,
    pub started_at: Timestamp,
}

/// Файлы состояния демона в каталоге `mcp` каталога данных.
pub struct DaemonFiles {
    directory: PathBuf,
}

/// Держится открытой на всё время жизни демона: по ней видно, работает ли он, и второй экземпляр не запустится.
/// Освободить — `drop`.
pub struct InstanceLock {
    _lock: FileLock,
}

impl DaemonFiles {
    /// Каталог `<data>/mcp` (`AppDirectories.Daemon`).
    pub fn current() -> DaemonFiles {
        DaemonFiles::new(daemon_dir())
    }

    pub fn new(directory: PathBuf) -> DaemonFiles {
        DaemonFiles { directory }
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// `daemon.lock`.
    pub fn instance_lock(&self) -> PathBuf {
        self.directory.join("daemon.lock")
    }

    /// `daemon.json`.
    pub fn info_file(&self) -> PathBuf {
        self.directory.join("daemon.json")
    }

    /// Берёт «единственный экземпляр» (`flock(LOCK_EX|LOCK_NB)`, на Windows — `FileShare.None`); `Ok(None)` — демон уже работает.
    pub fn try_hold(&self) -> io::Result<Option<InstanceLock>> {
        std::fs::create_dir_all(&self.directory)?;
        match FileLock::acquire(&self.instance_lock(), Some(Duration::ZERO)) {
            Ok(lock) => Ok(Some(InstanceLock { _lock: lock })),
            Err(e) if e.kind() == io::ErrorKind::TimedOut => Ok(None),
            Err(e) => Err(e),
        }
    }

    /// Работает ли демон: блокировка единственного экземпляра занята. Упавший демон блокировку не оставляет.
    pub fn is_running(&self) -> bool {
        matches!(self.try_hold(), Ok(None))
    }

    /// `daemon.json`; None — файла нет или он не читается.
    pub fn read_info(&self) -> Option<DaemonInfo> {
        let text = tasker_core::io::read_all_text(&self.info_file()).ok()?;
        let value: serde_json::Value = serde_json::from_str(&text).ok()?;
        let object = value.as_object()?;
        let get = |name: &str| object.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v);
        Some(DaemonInfo {
            pid: u32::try_from(get("pid")?.as_i64()?).ok()?,
            port: i32::try_from(get("port")?.as_i64()?).ok()?,
            token: get("token")?.as_str()?.to_string(),
            started_at: Timestamp::parse(get("startedAt")?.as_str()?)?,
        })
    }

    /// Пишет `daemon.json`: через `.tmp` и переименование, права 0600 (секрет читает только владелец).
    pub fn write_info(&self, info: &DaemonInfo) -> io::Result<()> {
        std::fs::create_dir_all(&self.directory)?;
        let target = self.info_file();
        let mut temp = target.as_os_str().to_os_string();
        temp.push(".tmp");
        let temp = PathBuf::from(temp);
        std::fs::write(&temp, serialize(info))?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            std::fs::set_permissions(&temp, std::fs::Permissions::from_mode(0o600))?;
        }
        tasker_core::io::move_file(&temp, &target, true)
    }

    /// Убирает файл, только если он про этот процесс: новый демон мог уже записать свой.
    pub fn delete_info(&self, pid: u32) -> io::Result<()> {
        if self.read_info().is_some_and(|info| info.pid == pid) {
            std::fs::remove_file(self.info_file())?;
        }
        Ok(())
    }
}

/// `JsonSerializer.Serialize(info, TaskerJson + WriteIndented)`: без завершающего перевода строки.
fn serialize(info: &DaemonInfo) -> String {
    tasker_core::json::to_string_pretty(&serde_json::json!({
        "pid": info.pid,
        "port": info.port,
        "token": info.token,
        "startedAt": info.started_at.format_json(),
    }))
}

/// Секрет управления: 32 случайных байта в base64url без `=` (как у .NET).
pub fn new_token() -> String {
    use rand::RngCore as _;
    let mut bytes = [0u8; 32];
    rand::rng().fill_bytes(&mut bytes);
    base64url(&bytes)
}

fn base64url(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let mut buffer = [0u8; 3];
        buffer[..chunk.len()].copy_from_slice(chunk);
        let n = (u32::from(buffer[0]) << 16) | (u32::from(buffer[1]) << 8) | u32::from(buffer[2]);
        let chars = chunk.len() + 1;
        for i in 0..chars {
            let index = (n >> (18 - 6 * i)) & 0x3F;
            out.push(ALPHABET[index as usize] as char);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn files() -> DaemonFiles {
        let dir = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        DaemonFiles::new(dir.join("mcp"))
    }

    #[test]
    fn the_lock_is_held_by_one_instance_and_released_on_drop() {
        let files = files();
        let held = files.try_hold().unwrap().expect("first instance");
        assert!(files.try_hold().unwrap().is_none());
        assert!(files.is_running());
        drop(held);
        assert!(!files.is_running());
        assert!(files.try_hold().unwrap().is_some());
        let _ = std::fs::remove_dir_all(files.directory().parent().unwrap());
    }

    #[test]
    fn info_is_written_as_readable_json_with_owner_only_access() {
        let files = files();
        let info = DaemonInfo {
            pid: 4242,
            port: 5719,
            token: new_token(),
            started_at: Timestamp::parse("2026-10-09T13:58:27.0563210+00:00").unwrap(),
        };
        files.write_info(&info).unwrap();
        let text = std::fs::read_to_string(files.info_file()).unwrap();
        assert_eq!(
            text,
            format!(
                "{{\n  \"pid\": 4242,\n  \"port\": 5719,\n  \"token\": \"{}\",\n  \"startedAt\": \"2026-10-09T13:58:27.056321+00:00\"\n}}",
                info.token
            )
        );
        assert_eq!(files.read_info().as_ref(), Some(&info));
        assert!(!files.info_file().with_extension("json.tmp").exists());
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            assert_eq!(std::fs::metadata(files.info_file()).unwrap().permissions().mode() & 0o777, 0o600);
        }

        // Чужой файл (другой pid) не удаляется, свой — удаляется.
        files.delete_info(1).unwrap();
        assert!(files.info_file().exists());
        files.delete_info(4242).unwrap();
        assert!(!files.info_file().exists());
        assert!(files.read_info().is_none());
        let _ = std::fs::remove_dir_all(files.directory().parent().unwrap());
    }

    #[test]
    fn token_is_43_base64url_characters() {
        let token = new_token();
        assert_eq!(token.len(), 43);
        assert!(token.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_'));
        assert_ne!(token, new_token());
        assert_eq!(base64url(b"\xfb\xff"), "-_8");
        assert_eq!(base64url(b"abc"), "YWJj");
    }
}
