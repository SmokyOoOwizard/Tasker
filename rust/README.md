# Rust: консоль `tasker` и демон `tasker-mcpd`

Cargo workspace переноса консоли и демона MCP на Rust по плану `docs/rust-migration-plan.md`. Поведение .NET-версии
воспроизводится буквально (тексты ошибок, форматы файлов и вывода): в переходный период над одной папкой `.tasker` работают оба движка.

| Путь | Что это |
|---|---|
| `crates/tasker-core` | домен: сущности, валидация, `ShortId` и ссылки `PREFIX-N`, версии (хэш файла), конвенции JSON, канонизация значений полей, временные метки, атомарная запись и блокировки файлов, блокировки на время правки (`locks`), глобальные настройки (`settings.json`), диагностика `TASKER_*` |
| `crates/tasker-files` | файлы `.tasker`: разбор YAML через saphyr в модели `tasker-core` (неизвестные ключи игнорируются), собственный эмиттер байт в байт как YamlDotNet, `formatVersion` с апгрейдом старых версий в памяти, модели файлов всех видов сущностей; имена файлов `<slug>-<id8>.yaml` (`names`), раскладка `.tasker` и `.gitignore` (`layout`), запись под блокировками `write.lock` → очередь на файл (`write`, крючок `IndexRefresh` для индекса TSK-131), блокировки правки `.cache/edit-locks/*.json` (`edit_locks`), миграция файлов (`migration`), наблюдатель файлов для демона (`watch`: `notify` + свой сборщик окон 300 мс / 2 с / порог 500, TSK-133); индекс области `.cache/index-rs.db` на SQLite (`index`, rusqlite bundled, TSK-131): своя схема `user_version = 1` и блокировка `index-rs.lock`, Sync по `(size, mtime)` и `Refresh(paths)` одной транзакцией, ошибки чтения как проблемы области, пересбор при повреждении; списки с пагинацией, фильтры и сортировки задач как `WorkspaceIndex.Sort` (.NET), серии, связи, поля — проверяется на снапшотах `task-list`/`*-list-json`/`sync` |
| `crates/tasker-daemon` | клиентская часть демона: пока автозапуск (`autostart`) — launchd plist, systemd unit и XML Планировщика заданий Windows (UTF-16 LE с BOM, `conhost --headless`) байт в байт как у .NET, команды `launchctl`/`systemctl --user`/`schtasks` с теми же аргументами и текстами ошибок за трейтом исполнителя; эталоны `tests/golden/autostart` |
| `crates/tasker-cli` | бинарник `tasker` (clap, синхронный): пока только `migrate [--dry-run] [--check]` с глобальными `-w`, `--json`, `-q` — первая команда, проверяемая на реальной области; вывод, коды выхода (0; 2 у `--check`; 1 при ошибке) и справка команды — как у .NET |
| `tests/golden` | эталоны поведения .NET-сборки (TSK-124): область `.tasker`, версии файлов, стиль YAML, вывод консоли, снапшоты MCP — см. `tests/golden/README.md` |

Дальше по плану: сервисы домена (TSK-132), остальные команды консоли (`mcp autostart` подключит `tasker-daemon`), `tasker-mcpd`.

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
проблемы области — с `001-sync`.

## Запуск консоли

```bash
cd rust
cargo run -p tasker-cli -- migrate --check -w <папка с .tasker>   # код 2 — есть что мигрировать
cargo run -p tasker-cli -- migrate --dry-run -w <папка>           # показать, ничего не записывая
cargo run -p tasker-cli -- migrate -w <папка>                     # переписать formatVersion и переименовать файлы
cargo run -p tasker-cli -- migrate --help
```

Область SQLite (`--sqlite`) этой сборкой пока не открывается.
