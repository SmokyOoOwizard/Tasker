//! `UserPath`: пути, набранные человеком. `~` раскрывает оболочка (zsh, bash), но не cmd.exe и не PowerShell при вызове программы:
//! там `-w ~\work` доходит как есть. Раскрываем сами, на любой платформе, и с `/`, и с `\`.
use std::path::{Path, PathBuf};

/// Каталог пользователя (`Environment.SpecialFolder.UserProfile`): `HOME` на Unix, `USERPROFILE` на Windows; пусто — не определён.
pub fn home_dir() -> String {
    let name = if cfg!(windows) { "USERPROFILE" } else { "HOME" };
    std::env::var(name).unwrap_or_default()
}

/// `~`, `~/…` и `~\…` — в каталоге пользователя; всё остальное (в том числе `~user`) без изменений.
pub fn expand_user_path(path: &str) -> String {
    expand_user_path_in(path, &home_dir())
}

/// `home` — каталог пользователя; пусто (профиль не определён) — путь не меняется.
pub fn expand_user_path_in(path: &str, home: &str) -> String {
    if path.is_empty() || !path.starts_with('~') || home.is_empty() {
        return path.to_string();
    }
    if path.len() == 1 {
        return home.to_string();
    }
    let rest = &path[1..];
    if rest.starts_with(['/', '\\']) {
        let rest = &rest[1..];
        // Path.Combine: пустой хвост — сам каталог; корневой хвост — он сам.
        if rest.is_empty() {
            return home.to_string();
        }
        let mut result = PathBuf::from(home);
        result.push(Path::new(rest));
        result.to_string_lossy().into_owned()
    } else {
        path.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tilde_forms() {
        let home = if cfg!(windows) { "C:\\Users\\me" } else { "/home/me" };
        let sep = std::path::MAIN_SEPARATOR;
        assert_eq!(expand_user_path_in("~", home), home);
        assert_eq!(expand_user_path_in("~/", home), home);
        assert_eq!(expand_user_path_in("~/work", home), format!("{home}{sep}work"));
        assert_eq!(expand_user_path_in("~\\work", home), format!("{home}{sep}work"));
        assert_eq!(expand_user_path_in("~user/x", home), "~user/x");
        assert_eq!(expand_user_path_in("/x", home), "/x");
        assert_eq!(expand_user_path_in("~/x", ""), "~/x");
        assert_eq!(expand_user_path_in("", home), "");
    }
}
