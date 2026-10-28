# Эталоны автозапуска: описания служб и команды системы

Файлы служб (plist launchd, unit systemd, XML Планировщика заданий) и команды системы, которые вызывает `tasker mcp autostart`.
Крейт `tasker-daemon` (`autostart`) обязан давать байт-идентичные файлы и те же команды — проверяет
`rust/crates/tasker-daemon/tests/golden_autostart.rs`.

## Как проверяются

- Файлы служб — `render_launchd`, `render_systemd`, `render_windows_task` для входов каждого каталога (таблица ниже).
- `calls.txt` — команды `enable` у `LaunchdService` (uid 502), `SystemdService`, `WindowsTaskService` с явным каталогом службы
  `<ROOT>/out/<каталог>` и именем: исполнитель команд записывает вызовы (аргументы через табуляцию) и отвечает кодом 0, в настоящем
  launchd, systemd и Планировщике ничего не регистрируется.
- `launchd-cli/` — путь `tasker mcp autostart enable` + `status`: `TASKER_HOME=<ROOT>/home`, каталог службы `<ROOT>/service`,
  имя `com.tasker.golden-autostart`; записанный plist и `launchctl-calls.txt` — три вызова через пробел: `bootout`, `bootstrap`
  (enable) и `print` (проверка, что служба загружена).

Команда демона во всех случаях задаётся явно — `<DOTNET> <BIN>/tasker-mcpd.dll --detached`: генераторы от неё не зависят, она лишь
подставляется в файлы. Эталон с нативным `tasker-mcpd.exe`, пробелами и кириллицей в путях — в `windows_task.rs`
(`task_xml_matches_the_golden_sample`).

| Каталог | `TASKER_HOME` | Имя | Что проверяет |
|---|---|---|---|
| `launchd` | `<ROOT>/home/data` | `com.tasker.golden-autostart` | блок `EnvironmentVariables`, журнал `<data>/logs/mcp-launchd.log` |
| `launchd-nohome` | не задан | то же | без `EnvironmentVariables`, журнал в `<HOME>/Library/Application Support/Tasker/logs` (macOS) |
| `launchd-special` | `<ROOT>/tasker home/a&b <c>` | то же | экранирование `&`, `<`, `>` в XML |
| `launchd-cli` | `<ROOT>/home` | то же | путь `tasker mcp autostart enable` / `status`, см. выше |
| `systemd` | `<ROOT>/home/data` | `tasker-golden.service` | `Environment=` без кавычек |
| `systemd-nohome` | не задан | то же | без `Environment=` |
| `systemd-space` | `<ROOT>/tasker home` | то же | квотирование systemd: `Environment="TASKER_HOME=…"` |
| `windows` | `<ROOT>/home/data` | `Tasker MCP Golden`, пользователь `PC\Артём` | `cmd.exe /d /s /c "set "TASKER_HOME=…" && …"`, UTF-16 LE с BOM, CRLF |
| `windows-nohome` | не задан | то же | `conhost --headless <program> --detached`, рабочий каталог — каталог данных |

## Плейсхолдеры

`<ROOT>` — каталог запуска, `<BIN>` — каталог сборки, `<DOTNET>` — `/usr/local/share/dotnet/dotnet`, `<HOME>` — домашний каталог
пользователя, `gui/<UID>` — uid. Тест подставляет вместо них строки без пробелов и спецсимволов (квотирование systemd/Windows от них
зависит, пробелы и спецсимволы проверяются через `TASKER_HOME`); для `calls.txt` и `launchd-cli` `<ROOT>` — настоящий каталог теста.
Все остальные байты (включая метку порядка байтов `FF FE` и CRLF у XML) — как в файлах.

## Как переснять

`cd rust && TASKER_GOLDEN_UPDATE=1 cargo test -p tasker-daemon --test golden_autostart` записывает файлы служб и вызовы текущей
сборкой с обратной заменой на плейсхолдеры (XML — снова UTF-16 LE с BOM); дифф каталога — изменения поведения. Переснимать на macOS
или Linux: тесты launchd на Windows пропускаются, а замена путей там неточна. Общие правила — в `../README.md`.
