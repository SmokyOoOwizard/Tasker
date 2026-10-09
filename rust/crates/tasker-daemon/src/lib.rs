//! Клиентская часть демона `tasker-mcpd` на Rust (фаза 4 плана `docs/rust-migration-plan.md`). Пока — автозапуск (`autostart`):
//! описания служб launchd, systemd и Планировщика заданий Windows дословно как у .NET-версии (`Tasker.Daemon.Services`) и команды
//! `launchctl` / `systemctl --user` / `schtasks` с теми же аргументами, кодами возврата и текстами ошибок. Эталоны —
//! `rust/tests/golden/autostart`.
//!
//! TODO (TSK-139): клиент демона (`daemon.json`, `/daemon/*`), `DaemonController` (остановка демона перед `Enable`, ожидание готовности).

pub mod autostart;
