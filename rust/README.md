# Rust: консоль `tasker` и демон `tasker-mcpd`

Cargo workspace переноса консоли и демона MCP на Rust по плану `docs/rust-migration-plan.md`. Поведение .NET-версии
воспроизводится буквально (тексты ошибок, форматы файлов и вывода): в переходный период над одной папкой `.tasker` работают оба движка.

| Путь | Что это |
|---|---|
| `crates/tasker-core` | домен: сущности, валидация, `ShortId` и ссылки `PREFIX-N`, версии (хэш файла), конвенции JSON, канонизация значений полей, временные метки, атомарная запись и блокировки файлов, глобальные настройки (`settings.json`), диагностика `TASKER_*` |
| `crates/tasker-files` | файлы `.tasker` как байты: разбор YAML через saphyr в модели `tasker-core` (неизвестные ключи игнорируются), собственный эмиттер байт в байт как YamlDotNet (стиль скаляров, литеральные блоки, кавычки), `formatVersion` с апгрейдом старых версий в памяти, модели файлов всех видов сущностей |
| `tests/golden` | эталоны поведения .NET-сборки (TSK-124): область `.tasker`, версии файлов, стиль YAML, вывод консоли, снапшоты MCP — см. `tests/golden/README.md` |

Дальше по плану: имена файлов, `AtomicFile`/`FileLock` и `tasker migrate` в `tasker-files` (TSK-130), затем `tasker-index`, `tasker-cli`, `tasker-mcpd`.

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

Тесты пишут временные файлы в `target/tmp`, системный временный каталог не трогают.
