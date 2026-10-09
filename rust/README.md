# Rust: консоль `tasker` и демон `tasker-mcpd`

Cargo workspace переноса консоли и демона MCP на Rust по плану `docs/rust-migration-plan.md`. Поведение .NET-версии
воспроизводится буквально (тексты ошибок, форматы файлов и вывода): в переходный период над одной папкой `.tasker` работают оба движка.

| Путь | Что это |
|---|---|
| `crates/tasker-core` | домен: сущности, валидация, `ShortId` и ссылки `PREFIX-N`, версии (хэш файла), конвенции JSON, канонизация значений полей, временные метки, атомарная запись и блокировки файлов, блокировки на время правки (`locks`), глобальные настройки (`settings.json`), диагностика `TASKER_*` |
| `crates/tasker-files` | файлы `.tasker`: разбор YAML через saphyr в модели `tasker-core` (неизвестные ключи игнорируются), собственный эмиттер байт в байт как YamlDotNet, `formatVersion` с апгрейдом старых версий в памяти, модели файлов всех видов сущностей; имена файлов `<slug>-<id8>.yaml` (`names`), раскладка `.tasker` и `.gitignore` (`layout`), запись под блокировками `write.lock` → очередь на файл (`write`, крючок `IndexRefresh` для индекса TSK-131), блокировки правки `.cache/edit-locks/*.json` (`edit_locks`), миграция файлов (`migration`); индекс области `.cache/index-rs.db` на SQLite (`index`, rusqlite bundled, TSK-131): своя схема `user_version = 1` и блокировка `index-rs.lock`, Sync по `(size, mtime)` и `Refresh(paths)` одной транзакцией, ошибки чтения как проблемы области, пересбор при повреждении; списки с пагинацией, фильтры и сортировки задач как `WorkspaceIndex.Sort` (.NET), серии, связи, поля — проверяется на снапшотах `task-list`/`*-list-json`/`sync` |
| `crates/tasker-services` | сервисы домена (TSK-132) — те же операции, проверки, тексты и коды ошибок, что у `Tasker.Core.*Service`: `Workspace` (папка, индекс, часы, держатель блокировок), хранилища сущностей поверх индекса и `write` (чтение из файла по id, списки из индекса, запись по версии с переименованием файла), `EditLockService`/`EntityLockService` (2 мин, ожидание каскадов до 30 с), `Usages` (единый текст 409), `CascadeResult`, Project, Status, StatusSet, TaskType (Clear/Keep), Field/FieldEnum/FieldConversion, Board/ColumnFilters, Task (поля, фильтры `Имя=значение`, сортировка, дерево до 10 уровней и 5000 строк, серии, поиск по ссылке), Series, TaskLink/LinkCycles (BFS и Тарьян, лимит 20 000), LinkType (дефолтные типы с детерминированными id), Cleanup, SeriesHealth/LinkHealth и сводка `sync`; синхронно, без async. Тесты повторяют сценарии `SeriesCoreTests`, `TaskFieldsTests`, `LinkCoreTests`, `LinkCycleTests`, `EditLockTests`, `FieldCatalogTests`, `CascadeLockTests`, `LinkCleanupTests`, `TaskHierarchyTests`, `BoardFieldFilterTests`, `ProjectDeleteTests`, `VanishedEntityTests` на временных областях |
| `crates/tasker-cli` | бинарник `tasker` (clap, синхронный): `migrate [--dry-run] [--check]`, `sync` и `cleanup [--resolve-conflicts] [--dry-run] [--check]` с глобальными `-w`, `-p`, `--json`, `-q` — команды, проверяемые на реальной области (снапшоты `expected/cli/001–009`, `194`, `195`, `213`, `expected/migrate/*`); вывод, коды выхода (0; 2 у `--check`; 1 при ошибке) и справка команд — как у .NET. Демона нет: `sync` всегда «via direct» |
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

Тесты пишут временные файлы в `target/tmp`, системный временный каталог не трогают. Интеграционный тест `tasker-cli` копирует
`tests/golden/workspace` в `target/tmp` и сверяет `tasker migrate` с `tests/golden/expected/migrate` (дерево и байты файлов,
вывод, коды выхода); тест индекса (`tasker-files/tests/index.rs`) строит `index-rs.db` по копии области и сверяет порядок списков,
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

Область SQLite (`--sqlite`) этой сборкой пока не открывается.
