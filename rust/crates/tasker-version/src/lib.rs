//! Версия сборки консоли и демона (см. `build.rs`): номер из файла `VERSION` (или `TASKER_VERSION` при сборке) и коммит.

/// Полная версия, её печатает `tasker --version`: `0.1.0` у релиза, `0.1.0+f2a114d[-dirty]` у сборки из исходников.
pub const VERSION: &str = env!("TASKER_BUILD_VERSION");

/// Коммит, из которого собрано (`f2a114d`, `f2a114d-dirty`); пусто, если при сборке не было git.
pub const COMMIT: &str = env!("TASKER_BUILD_COMMIT");

/// Версия без метаданных сборки (`+…`) — так её показывает демон (.NET `DaemonBuild.Version`): `0.1.0`.
pub fn release() -> &'static str {
    VERSION.split('+').next().unwrap_or(VERSION)
}

#[cfg(test)]
mod tests {
    #[test]
    fn version_starts_with_the_version_file() {
        let file = include_str!("../../../../VERSION").trim();
        if option_env!("TASKER_VERSION").is_none() {
            assert!(super::VERSION.starts_with(file), "{} / {file}", super::VERSION);
            assert_eq!(super::release(), file.split('+').next().unwrap());
        }
        assert!(!super::release().contains('+') && !super::VERSION.contains('\n'));
        if !super::COMMIT.is_empty() && option_env!("TASKER_VERSION").is_none() {
            assert_eq!(super::VERSION, format!("{}+{}", super::release(), super::COMMIT));
        }
    }
}
