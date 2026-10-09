# Rust: консоль `tasker` и демон `tasker-mcpd`

Cargo workspace переноса консоли и демона MCP на Rust по плану `docs/rust-migration-plan.md`. Поведение .NET-версии
воспроизводится буквально (тексты ошибок, форматы файлов и вывода): в переходный период над одной папкой `.tasker` работают оба движка.

| Путь | Что это |
|---|---|
| `crates/tasker-core` | домен: сущности, валидация, `ShortId` и ссылки `PREFIX-N`, версии (хэш файла), конвенции JSON, канонизация значений полей, временные метки, атомарная запись и блокировки файлов, блокировки на время правки (`locks`), глобальные настройки (`settings.json`), диагностика `TASKER_*` |
| `crates/tasker-files` | файлы `.tasker`: разбор YAML через saphyr в модели `tasker-core` (неизвестные ключи игнорируются), собственный эмиттер байт в байт как YamlDotNet, `formatVersion` с апгрейдом старых версий в памяти, модели файлов всех видов сущностей; имена файлов `<slug>-<id8>.yaml` (`names`), раскладка `.tasker` и `.gitignore` (`layout`), запись под блокировками `write.lock` → очередь на файл (`write`, крючок `IndexRefresh` для индекса TSK-131), блокировки правки `.cache/edit-locks/*.json` (`edit_locks`), миграция файлов (`migration`); индекс области `.cache/index-rs.db` на SQLite (`index`, rusqlite bundled, TSK-131): своя схема `user_version = 1` и блокировка `index-rs.lock`, Sync по `(size, mtime)` и `Refresh(paths)` одной транзакцией, ошибки чтения как проблемы области, пересбор при повреждении; списки с пагинацией, фильтры и сортировки задач как `WorkspaceIndex.Sort` (.NET), серии, связи, поля — проверяется на снапшотах `task-list`/`*-list-json`/`sync` |
| `crates/tasker-services` | сервисы домена (TSK-132) — те же операции, проверки, тексты и коды ошибок, что у `Tasker.Core.*Service`: `Workspace` (папка, индекс, часы, держатель блокировок), хранилища сущностей поверх индекса и `write` (чтение из файла по id, списки из индекса, запись по версии с переименованием файла), `EditLockService`/`EntityLockService` (2 мин, ожидание каскадов до 30 с), `Usages` (единый текст 409), `CascadeResult`, Project, Status, StatusSet, TaskType (Clear/Keep), Field/FieldEnum/FieldConversion, Board/ColumnFilters, Task (поля, фильтры `Имя=значение`, сортировка, дерево до 10 уровней и 5000 строк, серии, поиск по ссылке), Series, TaskLink/LinkCycles (BFS и Тарьян, лимит 20 000), LinkType (дефолтные типы с детерминированными id), Cleanup, SeriesHealth/LinkHealth и сводка `sync`; синхронно, без async. Тесты повторяют сценарии `SeriesCoreTests`, `TaskFieldsTests`, `LinkCoreTests`, `LinkCycleTests`, `EditLockTests`, `FieldCatalogTests`, `CascadeLockTests`, `LinkCleanupTests`, `TaskHierarchyTests`, `BoardFieldFilterTests`, `ProjectDeleteTests`, `VanishedEntityTests` на временных областях |
| `crates/tasker-cli` | бинарник `tasker` (clap, синхронный) и его каркас (TSK-134): декларативное дерево всех 112 команд с их аргументами, параметрами и описаниями (`spec`), по которому строится дерево clap и печатается справка в формате System.CommandLine — разделы, выравнивание колонок, `[default: …]`, `(REQUIRED)`, подсказки значений из текущей области (`help`, `hints`), подсказки опечаток и тексты ошибок разбора (`Required command was not provided.`, `Unrecognized command or argument '…'.`, `Option '…' is required.`); область и выбор проекта (`session`: `-p`, `TASKER_PROJECT`, единственный проект; `--sqlite` — ошибка «используйте .NET-сборку»), вывод (`context`: таблицы с выравниванием по ширине знаков и обрезкой «…», `Found N, shown a-b`, `-q`, компактный `--json`, коды выхода), терминал и ширина (`terminal`: `--truncate`/`--width`/`TASKER_WIDTH`, `COLUMNS`+`LINES` под watch, UTF-8 на Windows), тексты ошибок `Not found:`/`In use:`/`Locked:`/`Modified by someone else:`/`Error:` (`errors`). Реализована пока `migrate`; остальные команды до TSK-135 открывают область и проект, затем отвечают `Error: not implemented in this build` (код 1) |
| `tests/golden` | эталоны поведения .NET-сборки (TSK-124): область `.tasker`, версии файлов, стиль YAML, вывод консоли, снапшоты MCP — см. `tests/golden/README.md` |

Дальше по плану: наблюдатель файлов (TSK-133), остальные команды консоли (TSK-134), `tasker-mcpd`.

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
снапшотами корпуса и на области с «висячей» связью после слияния.

## Запуск консоли

```bash
cd rust
cargo run -p tasker-cli -- migrate --check -w <папка с .tasker>   # код 2 — есть что мигрировать
cargo run -p tasker-cli -- migrate --dry-run -w <папка>           # показать, ничего не записывая
cargo run -p tasker-cli -- migrate -w <папка>                     # переписать formatVersion и переименовать файлы
cargo run -p tasker-cli -- migrate --help
cargo run -p tasker-cli -- sync -w <папка>                        # сверить кэш и показать проблемы файлов, серий и связей
cargo run -p tasker-cli -- cleanup --check -w <папка>             # код 2 — есть что чистить
cargo run -p tasker-cli -- cleanup --resolve-conflicts -w <папка> # убрать висячие ссылки и связи, решить дубликаты номеров
```

`tasker --help` и `tasker <команда> --help` печатают справку как .NET-консоль; команды, ещё не перенесённые, отвечают
`Error: not implemented in this build`. Область SQLite (`--sqlite`) этой сборкой не открывается: `Error: --sqlite is not supported
by this build: use the .NET build of Tasker`.
