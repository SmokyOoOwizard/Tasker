# Эталоны автозапуска: описания служб и команды системы

Файлы служб (plist launchd, unit systemd, XML Планировщика заданий) и команды системы, которые вызывает `tasker mcp autostart`.
Крейт `tasker-daemon` (`autostart`) обязан давать байт-идентичные файлы и те же команды — проверяет
`crates/tasker-daemon/tests/golden_autostart.rs`. Эталоны сняты с .NET-сборки 0.1.0 (генераторы `Tasker.Daemon.Services`, macOS,
.NET 10.0.201) и меняются только вместе с поведением.

## Как сняты

- `launchd-cli/` — настоящая консоль: `TASKER_HOME=<ROOT>/home TASKER_SERVICE_DIR=<ROOT>/service TASKER_SERVICE_LABEL=com.tasker.golden-autostart
  dotnet <BIN>/tasker.dll mcp autostart enable`, затем `… mcp autostart status`. В `PATH` впереди стоял поддельный `launchctl`
  (пишет аргументы в журнал, код 0) — в настоящем launchd ничего не регистрировалось, `~/Library/LaunchAgents`
  не тронут. `enable` завершился ошибкой «The MCP server did not answer in 30 s» (демон никто не запустил), но plist к тому моменту
  записан. `launchctl-calls.txt` — три вызова: `bootout`, `bootstrap` (enable) и `print` (status).
- остальные каталоги — генератор на .NET (запуск с `HOME=<ROOT>/home`): конструкторы `LaunchdService`, `SystemdService`, `WindowsTaskService` с явным каталогом и именем и `IProcessRunner`,
  который записывает вызовы (`calls.txt`, аргументы через табуляцию) и отвечает кодом 0. Так сняты ветки Linux и Windows на macOS:
  генераторы чистые, от ОС зависит только `Launcher.DaemonCommand` (путь `dotnet` и `tasker-mcpd.dll` рядом с главной сборкой —
  пустой файл) и каталог данных по умолчанию.

| Каталог | `TASKER_HOME` | Имя | Что проверяет |
|---|---|---|---|
| `launchd` | `<ROOT>/home/data` | `com.tasker.golden-autostart` | блок `EnvironmentVariables`, журнал `<data>/logs/mcp-launchd.log` |
| `launchd-nohome` | не задан | то же | без `EnvironmentVariables`, журнал в `<HOME>/Library/Application Support/Tasker/logs` (macOS) |
| `launchd-special` | `<ROOT>/tasker home/a&b <c>` | то же | экранирование `&`, `<`, `>` (`SecurityElement.Escape`) |
| `launchd-cli` | `<ROOT>/home` | то же | настоящая консоль, см. выше |
| `systemd` | `<ROOT>/home/data` | `tasker-golden.service` | `Environment=` без кавычек |
| `systemd-nohome` | не задан | то же | без `Environment=` |
| `systemd-space` | `<ROOT>/tasker home` | то же | квотирование systemd: `Environment="TASKER_HOME=…"` |
| `windows` | `<ROOT>/home/data` | `Tasker MCP Golden`, пользователь `PC\Артём` | `cmd.exe /d /s /c "set "TASKER_HOME=…" && …"`, UTF-16 LE с BOM, CRLF |
| `windows-nohome` | не задан | то же | `conhost --headless <program> --detached`, рабочий каталог — каталог данных |

Команда демона во всех случаях — `<DOTNET> <BIN>/tasker-mcpd.dll --detached` (консоль запущена через `dotnet tasker.dll`; нативный
`tasker` дал бы `<BIN>/tasker-mcpd --detached` — генераторы от этого не зависят, Rust-консоль всегда нативная). Эталон с нативным
`tasker-mcpd.exe`, пробелами и кириллицей в путях — в `windows_task.rs` (`task_xml_matches_the_dotnet_golden_sample`).

## Плейсхолдеры

`<ROOT>` — временный каталог запуска, `<BIN>` — каталог сборки, `<DOTNET>` — `/usr/local/share/dotnet/dotnet`, `<HOME>` — домашний каталог
пользователя (в .NET `Environment.GetFolderPath(UserProfile)` переменную `HOME` не читает — подставлен настоящий), `gui/<UID>` — uid.
Все остальные байты (включая метку порядка байтов `FF FE` и CRLF у XML) — как записала .NET-версия. Тест подставляет вместо плейсхолдеров
строки без пробелов и спецсимволов, поскольку квотирование systemd/Windows от них зависит, а в оригинале их не было.
