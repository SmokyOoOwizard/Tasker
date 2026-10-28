//! Клиентская часть демона `tasker-mcpd` на Rust. Пока — автозапуск (`autostart`): описания служб launchd, systemd и
//! Планировщика заданий Windows и команды `launchctl` / `systemctl --user` / `schtasks` с их аргументами, кодами возврата и
//! текстами ошибок. Эталоны — `tests/golden/autostart`.
//!
//! TODO (TSK-139): клиент демона (`daemon.json`, `/daemon/*`), управление демоном (остановка демона перед включением автозапуска, ожидание готовности).

pub mod autostart;
