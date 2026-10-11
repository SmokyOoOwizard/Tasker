//! `OsUser`: имя пользователя операционной системы для подписи правок и блокировок.

/// Имя пользователя ОС без домена (`Environment.UserName`, как у десктопа): на Unix — запись учётной записи процесса (`getpwuid_r`),
/// без неё — `USER`/`LOGNAME`; на Windows — `USERNAME`.
pub fn os_user_name() -> String {
    let raw = if cfg!(windows) {
        std::env::var("USERNAME").ok()
    } else {
        account_name().or_else(|| {
            std::env::var("USER")
                .ok()
                .filter(|s| !s.is_empty())
                .or_else(|| std::env::var("LOGNAME").ok())
        })
    };
    normalize_user_name(raw.as_deref())
}

/// Имя учётной записи действующего пользователя процесса из базы пользователей (`/etc/passwd`, каталог).
#[cfg(unix)]
fn account_name() -> Option<String> {
    let mut buffer = vec![0 as libc::c_char; 16 * 1024];
    // SAFETY: getpwuid_r пишет только в переданные passwd и buffer указанной длины; pw_name указывает в buffer, который жив до копирования.
    unsafe {
        let mut entry: libc::passwd = std::mem::zeroed();
        let mut result: *mut libc::passwd = std::ptr::null_mut();
        let code = libc::getpwuid_r(libc::geteuid(), &mut entry, buffer.as_mut_ptr(), buffer.len(), &mut result);
        if code != 0 || result.is_null() || entry.pw_name.is_null() {
            return None;
        }
        let name = std::ffi::CStr::from_ptr(entry.pw_name).to_string_lossy().into_owned();
        if name.is_empty() { None } else { Some(name) }
    }
}

#[cfg(not(unix))]
fn account_name() -> Option<String> {
    None
}

/// `DOMAIN\user` и `user@domain.local` (так имя встречается на Windows в переменных окружения и в сведениях о доменных
/// учётных записях) — просто `user`: домен в подписи только мешает. Пусто — «user».
pub fn normalize_user_name(name: Option<&str>) -> String {
    let mut text = name.unwrap_or("").trim();
    if let Some(slash) = text.rfind('\\') {
        text = &text[slash + 1..];
    }
    if let Some(at) = text.find('@')
        && at > 0
    {
        text = &text[..at];
    }
    if text.is_empty() { "user".to_string() } else { text.to_string() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn domains_are_dropped() {
        assert_eq!(normalize_user_name(Some("DOMAIN\\ivan")), "ivan");
        assert_eq!(normalize_user_name(Some("ivan@corp.local")), "ivan");
        assert_eq!(normalize_user_name(Some("  ivan  ")), "ivan");
        assert_eq!(normalize_user_name(Some("@x")), "@x");
        assert_eq!(normalize_user_name(Some("")), "user");
        assert_eq!(normalize_user_name(None), "user");
        assert_eq!(normalize_user_name(Some("DOMAIN\\")), "user");
    }
}
