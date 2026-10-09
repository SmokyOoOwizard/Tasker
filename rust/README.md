# Rust: консоль `tasker` и демон `tasker-mcpd`

Cargo workspace переноса консоли и демона MCP на Rust по плану `docs/rust-migration-plan.md`. Поведение .NET-версии
воспроизводится буквально (тексты ошибок, форматы файлов и вывода): в переходный период над одной папкой `.tasker` работают оба движка.

| Путь | Что это |
|---|---|
| `crates/tasker-core` | домен: сущности, валидация, `ShortId` и ссылки `PREFIX-N`, версии (хэш файла), конвенции JSON, канонизация значений полей, временные метки, атомарная запись и блокировки файлов, блокировки на время правки (`locks`), глобальные настройки (`settings.json`) и слежение за ними для демона (`SettingsStore::watch`: `notify`, тишина 200 мс, отпечаток содержимого, битый файл игнорируется), диагностика `TASKER_*` |
| `crates/tasker-files` | файлы `.tasker`: разбор YAML через saphyr в модели `tasker-core` (неизвестные ключи игнорируются), собственный эмиттер байт в байт как YamlDotNet, `formatVersion` с апгрейдом старых версий в памяти, модели файлов всех видов сущностей; имена файлов `<slug>-<id8>.yaml` (`names`), раскладка `.tasker` и `.gitignore` (`layout`), запись под блокировками `write.lock` → очередь на файл (`write`, крючок `IndexRefresh` для индекса TSK-131), блокировки правки `.cache/edit-locks/*.json` (`edit_locks`), миграция файлов (`migration`); индекс области `.cache/index-rs.db` на SQLite (`index`, rusqlite bundled, TSK-131): своя схема `user_version = 1` и блокировка `index-rs.lock`, Sync по `(size, mtime)` и `Refresh(paths)` одной транзакцией, ошибки чтения как проблемы области, пересбор при повреждении; списки с пагинацией, фильтры и сортировки задач как `WorkspaceIndex.Sort` (.NET), серии, связи, поля — проверяется на снапшотах `task-list`/`*-list-json`/`sync`; наблюдатель файлов `.tasker` для демона на `notify` (`watch`: пачки путей с окном 300 мс и потолком 2 с, больше 500 путей или потеря событий — `FullSync`, TSK-133) |
| `crates/tasker-services` | сервисы домена (TSK-132) — те же операции, проверки, тексты и коды ошибок, что у `Tasker.Core.*Service`: `Workspace` (папка, индекс, часы, держатель блокировок), хранилища сущностей поверх индекса и `write` (чтение из файла по id, списки из индекса, запись по версии с переименованием файла), `EditLockService`/`EntityLockService` (2 мин, ожидание каскадов до 30 с), `Usages` (единый текст 409), `CascadeResult`, Project, Status, StatusSet, TaskType (Clear/Keep), Field/FieldEnum/FieldConversion, Board/ColumnFilters, Task (поля, фильтры `Имя=значение`, сортировка, дерево до 10 уровней и 5000 строк, серии, поиск по ссылке), Series, TaskLink/LinkCycles (BFS и Тарьян, лимит 20 000), LinkType (дефолтные типы с детерминированными id), Cleanup, SeriesHealth/LinkHealth и сводка `sync` (общая форма JSON `health::to_json` для консоли и `/daemon/sync`); синхронно, без async. Тесты повторяют сценарии `SeriesCoreTests`, `TaskFieldsTests`, `LinkCoreTests`, `LinkCycleTests`, `EditLockTests`, `FieldCatalogTests`, `CascadeLockTests`, `LinkCleanupTests`, `TaskHierarchyTests`, `BoardFieldFilterTests`, `ProjectDeleteTests`, `VanishedEntityTests` на временных областях |
| `crates/tasker-daemon` | клиентская часть демона: пока автозапуск (`autostart`) — launchd plist, systemd unit и XML Планировщика заданий Windows (UTF-16 LE с BOM, `conhost --headless`) байт в байт как у .NET, команды `launchctl`/`systemctl --user`/`schtasks` с теми же аргументами и текстами ошибок за трейтом исполнителя; эталоны `tests/golden/autostart` |
| `crates/tasker-mcpd` | бинарник `tasker-mcpd` (tokio + axum, TSK-137) в одиночном режиме (`McpDaemon.Run` в .NET): `daemon.lock` (`flock`, второй экземпляр — код 1 с тем же текстом), ожидание занятого порта до 5 с, `daemon.json` `{pid, port, token, startedAt}` через `.tmp` + rename с правами 0600, HTTP на `127.0.0.1:<port>`: `GET /health`, `GET /api/health`, `GET /ready` (503, пока области открываются), `GET /daemon/status`, `POST /daemon/sync|stop|upgrade` с заголовком `X-Tasker-Control` (без секрета — 401; `upgrade` — 409 «один процесс», как .NET без супервизора), фильтр «только с этой машины» по `Host`/`Origin` (403), адреса десктопа `/w/{key}/…` — 404 с теми же подсказками; реестр областей из `settings.json` (`registry`: ключ — slug имени папки, одноимённые — `-2`, `-3`; `sync`: статусы `opening/open/failed`, тексты ошибок `<path> does not exist`, повтор failed-областей каждые 30 с, добавление/удаление на лету по `SettingsStore::watch`, смена порта — после перезапуска; на область — `Workspace::open` и наблюдатель `watch` → `refresh`/`sync`); остановка по SIGTERM/SIGINT или `/daemon/stop` (закрыть области, до 5 с на запросы, убрать `daemon.json`, снять блокировку), `--detached` — `setsid`; журнал `logs/mcp-YYYYMMDD.log` (7 файлов) в формате Serilog `2026-10-09 13:58:27.056 +02:00 [INF] …` (`tracing`, свой дневной файл), не в фоне — ещё и консоль. Домен зовётся через `spawn_blocking`. Нет: `/mcp` (501, TSK-138), `--supervised`/`--worker` (код 2, TSK-139), области SQLite (статус `failed` с текстом «use the .NET build»), Windows-ветки (именованное событие остановки `Local\tasker-mcp-stop-<16 hex>`) написаны по C# и не проверены |
| `crates/tasker-cli` | бинарник `tasker` (clap, синхронный): декларативное дерево всех 112 команд (`spec`), справка и ошибки разбора в формате System.CommandLine (`help`, `hints`), область и выбор проекта (`session`), вывод (`context`, `table`, `kit`), терминал и ширина (`terminal`), тексты ошибок (`errors`) — каркас TSK-134; все команды дерева (TSK-135, по модулю на группу в `commands/`): project, status, status-set, task-type, link-type, field, enum, board (`get`/`show`/`tasks`), task (создание, списки деревом и `--flat`, карточка `get` с полями и связями, правка полей `--field`/`--custom-field`/`--add-field`/`--remove-field`, родители, `link`/`unlink`/`links`), series (`add-task`/`remove-task`/`renumber-task`), lock, whoami, user/agent, sync, cleanup, migrate, hooks (git-хуки по `GitHooks.cs`), manual (страницы `src/Tasker.Cli/Manual` через `include_str!`), completion (скрипты оболочек байт в байт, `pwsh --install/--uninstall`), директива `[suggest:N]` автодополнения (`suggest`), `mcp` — клиент демона (`daemon`: `daemon.lock`/`daemon.json`, `/daemon/status`, `/daemon/stop` через ureq, запуск `tasker-mcpd --detached` рядом с бинарником, автозапуск через `tasker-daemon`; `mcp run`/`upgrade` — честная ошибка до TSK-137/139); JSON сущностей как `TaskerJson` (`entities`), `TASKER_PROFILE=1` (`perf`). Область SQLite (`--sqlite`) этой сборкой не открывается |
| `tests/golden` | эталоны поведения .NET-сборки (TSK-124): область `.tasker`, версии файлов, стиль YAML, вывод консоли, снапшоты MCP — см. `tests/golden/README.md` |

Дальше по плану: автодополнение и замеры (TSK-136), инструменты MCP (TSK-138), супервизор и воркер (TSK-139).

## Сборка и тесты

Нужен стабильный toolchain Rust 1.85+ (`rustup` или `brew install rust`).

```bash
cd rust
cargo build
cargo test                                            # unit-тесты и проверки на golden-корпусе
cargo clippy --all-targets -- -D warnings             # без замечаний
cargo fmt                                             # rustfmt.toml: max_width = 140
cargo build --release                                 # профиль из Cargo.toml: lto fat, opt-level s, panic abort, strip
```

Тесты пишут временные файлы в `target/tmp`, системный временный каталог не трогают. Интеграционные тесты `tasker-cli` копируют
`tests/golden/workspace` в `target/tmp` и сверяют `tasker migrate` с `tests/golden/expected/migrate` (дерево и байты файлов,
вывод, коды выхода), ошибки области и разбора — с `expected/cli/*error-*`, а справку всех 112 команд — байт в байт с
`expected/cli/*help-*` (`tests/help.rs`; бинарник запускается из корня репозитория, как при снятии эталонов: подсказки
`<R&D|Баг|Фича>`, `<Tasker>` берутся из его области `.tasker`); тест индекса (`tasker-files/tests/index.rs`) строит `index-rs.db` по копии области и сверяет порядок списков,
фильтры и сортировки `task list` с `expected/cli/*task-list*` (параметры из `.args`), списки сущностей — с `*-list-json`,
проблемы области — с `001-sync`. Тесты `tasker-services` (`crates/tasker-services/tests/*.rs`) строят область с нуля
во временной папке и проверяют сервисы по сценариям тестов .NET; `tasker-cli/tests/sync_cleanup.rs` сверяет `sync` и `cleanup` со
снапшотами корпуса и на области с «висячей» связью после слияния. Тесты `tasker-mcpd/tests/daemon.rs` запускают собранный
`tasker-mcpd` на свободном порту с изолированным `TASKER_HOME` (`target/tmp`) и проверяют файлы состояния, управление, области и
остановку по сценариям `DaemonTests` (.NET); настоящий демон пользователя (порт 5719) не трогается.

.NET-тесты демона против Rust-бинарника (ветка TSK-125, `TASKER_BIN`/`TASKER_MCPD_BIN`): собрать `tests/Tasker.Tests`, скопировать
каталог сборки, положить в копию `target/release/tasker-mcpd` вместо `tasker-mcpd`.dll-обвязки и запустить
`TASKER_BIN=<копия>/tasker TASKER_MCPD_BIN=<копия>/tasker-mcpd dotnet test tests/Tasker.Tests --no-build --filter "FullyQualifiedName~DaemonTests"`
— .NET-консоль ищет `tasker-mcpd` рядом с собой. Тесты, которым нужен `/mcp`, в этой сборке падают.

## Запуск консоли

```bash
cd rust
cargo run -p tasker-cli -- task list --all -w <папка с .tasker> -p <проект>   # любая команда .NET-консоли, тот же вывод
cargo run -p tasker-cli -- task get TSK-5 --json -w <папка> -p <проект>
cargo run -p tasker-cli -- migrate --check -w <папка>                       # код 2 — есть что мигрировать
cargo run -p tasker-cli -- sync -w <папка>                                  # сверить кэш и показать проблемы файлов, серий и связей
cargo run -p tasker-cli -- cleanup --check -w <папка>                       # код 2 — есть что чистить
cargo run -p tasker-cli -- mcp status                                       # демон tasker-mcpd рядом с бинарником (и .NET-демон тоже)
TASKER_PROFILE=1 cargo run -p tasker-cli -- task list -w <папка>            # фазы вызова в stderr: [profile] имя мс 0
```


