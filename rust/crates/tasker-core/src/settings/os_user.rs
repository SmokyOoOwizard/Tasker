//! `OsUser`: имя пользователя операционной системы для подписи правок и блокировок.

/// Имя пользователя ОС без домена (`Environment.UserName`): `USER`/`LOGNAME` на Unix, `USERNAME` на Windows.
pub fn os_user_name() -> String {
    let raw = if cfg!(windows) {
        std::env::var("USERNAME").ok()
    } else {
        std::env::var("USER")
            .ok()
            .filter(|s| !s.is_empty())
            .or_else(|| std::env::var("LOGNAME").ok())
    };
    normalize_user_name(raw.as_deref())
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
