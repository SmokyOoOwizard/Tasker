//! Какая это сборка демона (.NET `DaemonBuild`): версия (`tasker-version`: файл `VERSION`, как `InformationalVersion` без `+…`) и
//! короткий идентификатор сборки — он меняется с каждой сборкой, в отличие от версии: по нему в статусе видно, что рабочий процесс
//! заменён на новую сборку. У .NET это первые 8 hex MVID модуля; здесь — первые 8 hex SHA-256 исполняемого файла (считается один раз).
use sha2::{Digest as _, Sha256};
use std::sync::OnceLock;

/// Версия сборки (`0.1.0`).
pub fn version() -> &'static str {
    tasker_version::release()
}

/// Короткий идентификатор сборки: 8 hex.
pub fn build_id() -> &'static str {
    static ID: OnceLock<String> = OnceLock::new();
    ID.get_or_init(|| {
        let bytes = std::env::current_exe().and_then(std::fs::read).unwrap_or_else(|_| {
            // Файла не прочитать — идентификатор хотя бы различает процессы.
            let mut random = [0u8; 16];
            rand::RngCore::fill_bytes(&mut rand::rng(), &mut random);
            random.to_vec()
        });
        Sha256::digest(&bytes)[..4].iter().map(|b| format!("{b:02x}")).collect()
    })
}

#[cfg(test)]
mod tests {
    #[test]
    fn version_and_build_look_like_dotnet() {
        assert!(!super::version().is_empty());
        assert!(!super::version().contains('\n'));
        let id = super::build_id();
        assert_eq!(id.len(), 8);
        assert!(id.chars().all(|c| c.is_ascii_hexdigit()));
        assert_eq!(id, super::build_id());
    }
}
