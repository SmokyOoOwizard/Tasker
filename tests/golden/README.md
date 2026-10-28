# Golden-корпус: эталоны поведения консоли и демона MCP

Консоль `tasker` и демон `tasker-mcpd` (`rust/`) обязаны воспроизводить всё, что здесь лежит: `--json`, файлы `.tasker`, ответы MCP,
файлы служб и индекс — байт в байт (ответы MCP — по смыслу, см. ниже), текстовый вывод консоли — построчно. Корпус общий с .NET-тестами
`dotnet/tests/Tasker.Tests`: `McpInputSchemaTests` сверяет схемы инструментов с `expected/mcp/tools-list.json`, `TaskFileKeyOrderTests`
читает файлы задач из `workspace/`.

## Что внутри

| Путь | Что это | Чем сверяется (`rust/crates/…`) |
|---|---|---|
| `workspace/.tasker/` | Каноническая рабочая область — **вход** всех проверок, не переснимается: проект **Golden** (все сущности, все типы полей, «опасные» строки, иерархия, связи всех типов, две серии, доски с условиями колонок, пользователи и агенты) в папке `projects/golden-11111111/` и проект **Legacy** (папка и файлы со старыми именами `<guid>`, файлы formatVersion 0–9, CRLF, BOM, файл с маркером конфликта git, файл формата 99 «из будущего»). Без `.cache` — индекс строится заново. | все разделы ниже |
| `versions.json` | Версия каждого файла области: первые 16 hex SHA-256 от байтов без BOM и с CRLF→LF. Так же версии показывают `task get --json`, MCP и REST. | `tasker-core/tests/golden.rs`, `tasker-files/tests/golden.rs` |
| `yaml-style/titles.json`, `yaml-style/descriptions.json` | Стилевые пробы YamlDotNet 18.1: значение строки и то, как она записана в файл (кавычки `'`/`"`, `\|-`, `\|`, `\|+`, `\|2-`, экранирование). Спецификация собственного YAML-эмиттера; переснимается только поле `yaml`. | `tasker-files/tests/golden.rs` |
| `expected/cli/NNN-<имя>.{args,out,err,code}` | Вывод команд консоли на копии области: аргументы (вход, по одному в строке, путь области — `<ROOT>/golden`), stdout, stderr, код выхода. Списки и `get` всех сущностей в тексте и `--json`, фильтры и сортировки `task list`, `sync`, `cleanup --check`, `migrate --check`/`--dry-run`, блокировки, ошибки, справка каждой команды (`help-*`), `completion <shell>`, директива `[suggest:N]`, `manual`. | `tasker-cli/tests/golden_cli.rs` (все), `help.rs`, `sync_cleanup.rs`, `migrate.rs`; `tasker-files/tests/index.rs` читает `*-list-json` и `*task-list*` |
| `expected/migrate/` | `tasker migrate` на копии области: вывод команды, повторный `migrate --check` (код 2 — остаются файл с конфликтом и файл формата 99), `tasker/` — вся область после миграции, `versions.json` — версии файлов после неё. | `tasker-cli/tests/golden_cli.rs`, `migrate.rs`; `tasker-files/tests/golden.rs` |
| `expected/mcp/` | Снапшоты демона MCP (области `golden` — копия корпуса и `broken` — несуществующая папка): `initialize`, `tools-list.json` (имена, описания, inputSchema всех инструментов; `tool-names.txt` — список), ответы инструментов на чтение, `errors/` — по ответу на каждый код ошибки. `cli/` — `tasker sync` (текст, `--json`, `-q`) при работающем демоне, который обслуживает область (`via daemon`, TSK-160); тестом не сверяется и не переснимается — поведение проверяет `tasker-cli/tests/mcp_daemon.rs`. | `tasker-mcpd/tests/mcp.rs` |
| `autostart/` | Файлы автозапуска — launchd plist, systemd unit, XML Планировщика (UTF-16 LE с BOM) — и вызываемые команды; см. `autostart/README.md` (TSK-140). | `tasker-daemon/tests/golden_autostart.rs` |
| `sqlite/` | Раздел SQLite (TSK-159): проекты Golden и Legacy без файловых особенностей в файле SQLite. `workspace.sql` — база SQL-дампом, **вход** (схема в порядке `sqlite_master`, строки по `rowid`; тест восстанавливает её в `<ROOT>/golden.db` в журнале WAL); `schema.sql` — `.schema` новой базы, созданной `Workspace::open_sqlite` (та же схема, что у `EnsureCreated` в `dotnet/src/Tasker.Storage.Db`); `expected/cli` — те же команды консоли с `--sqlite <ROOT>/golden.db` (без справки и автодополнения), `125-error-both-locations` — `-w` вместе с `--sqlite`; `expected/mcp` — демон с областью `{"kind":"sqlite"}` (`golden.db`): чтение, ошибки, правка `update_task`. | `tasker-cli/tests/golden_sqlite.rs`, `tasker-mcpd/tests/mcp.rs`, `tasker-services/tests/db_schema.rs` |
| `index/` | Кэш индекса `.tasker/.cache/index.db`, общий с .NET (`dotnet/src/Tasker.Storage.Files`, TSK-161), после `sync` по копии `workspace/`: `schema.sql` — `.schema` базы (у таблиц `CREATE TABLE IF NOT EXISTS`, как печатает оболочка `sqlite3`), `index.txt` — канонический дамп: `user_version`, режим журнала, строки таблиц `files` (по `path`), `task_series`, `task_links`, `task_fields`, `task_field_values` (по `path` и `rowid`) через `quote()`, `mtime` — `<MTIME>`, если он равен времени изменения файла в тиках .NET (иначе число — и расхождение), и содержимое `.cache`. Пути в дампе относительные — `<ROOT>` не нужен. | `tasker-files/tests/index.rs` |
| `notes.json`, `sqlite/notes.json` | Канонические id проектов, id особых файлов (конфликт, «из будущего»), отвергнутые консолью строки. | — |

## Как сверить

```bash
cd rust && cargo test
```

Тесты копируют `workspace/` (или восстанавливают `sqlite/workspace.sql`) в `rust/target/tmp/<uuid>`, запускают консоль и демон с
изолированным `TASKER_HOME` (имя пользователя `golden-user`) и сравнивают результат с эталонами. Расхождение сборки с корпусом —
ошибка. `GOLDEN_FILTER=<подстрока>[,<подстрока>]` ограничивает набор снапшотов `golden_cli.rs` и `golden_sqlite.rs` при отладке.

## Как переснять

```bash
cd rust && TASKER_GOLDEN_UPDATE=1 cargo test                      # весь корпус
cd rust && TASKER_GOLDEN_UPDATE=1 cargo test -p tasker-mcpd        # раздел одного крейта
cd rust && TASKER_GOLDEN_UPDATE=1 cargo test --test golden_cli     # один тест (expected/cli, expected/migrate)
git diff -- tests/golden                                           # изменения поведения — на ревью
```

В режиме пересъёмки (`TASKER_GOLDEN_UPDATE` не пуста и не `0`) каждая сверка сначала записывает эталон тем, что выдала текущая
сборка, а затем сравнивает как обычно, поэтому тесты проходят. Входы — `workspace/`, `sqlite/workspace.sql`, `*.args`, `notes.json` —
не переписываются. Дифф `tests/golden` и есть список изменений поведения: намеренное изменение меняет корпус в том же изменении,
случайное видно на ревью. Пересъёмка на неизменной сборке не даёт диффа.

Переснимать нужно на macOS или Linux: на Windows пути печатаются с `\`, и обратная замена на `<ROOT>` неточна; тесты демона MCP на
Windows пропускаются (TSK-156).

## Плейсхолдеры и канонические id

Id и время в `workspace/` и `sqlite/workspace.sql` детерминированы:

- id проектов — `11111111-…` (Golden) и `22222222-…` (Legacy); id типов связей по умолчанию — по правилу MD5 от
  `tasker-link-type:<projectId>:<key>` с порядком байтов `Guid(byte[])` (little-endian), поэтому корпус заодно проверяет и это правило;
- остальные id — `NNNNNNNN-0000-4000-8000-NNNNNNNNNNNN` по порядку обхода (папка, имя сущности); имена файлов — `<slug>-<id8>.yaml`;
  в SQLite в столбцах-Guid id записаны заглавными, в значениях полей — строчными;
- временные метки — от `2026-01-01T00:00:00` с шагом 61 с в порядке создания (в SQLite — `yyyy-MM-dd HH:mm:ss.FFFFFFF`).

Что зависит от запуска, в эталонах заменено плейсхолдерами: путь временного каталога → `<ROOT>`; id областей демона (12 hex SHA-256
от `Files:<канонический путь>` и `Sqlite:<канонический путь>`) → `<WSID:golden>`, `<WSID:broken>`, `<WSID:golden.db>`; id, время
создания и версия локального агента MCP (создаётся демоном при первом вызове) → `<LOCAL-AGENT-*>` (в SQLite версия — счётчик, она не
заменяется); время по часам машины (срок блокировки, время правки в `sqlite/expected/mcp/update_task.json`) → `<TIME>`.

Ответы MCP хранятся целым конвертом JSON-RPC (отступ 2) и сравниваются по смыслу: порядок ключей конверта и `isError: false` не в
счёт, `text` с JSON внутри сравнивается как JSON с порядком ключей; `tools-list.json` сверяется ещё и по порядку ключей схем.

## Известные особенности, зафиксированные корпусом

- Нечитаемый файл задачи (маркер конфликта слияния, формат 99) по полному и по короткому id даёт ошибку его чтения с кодом 1
  (`task-get-future`, `task-get-conflict`, `task-get-conflict-short-id`, `task-get-future-short-id`); три последних идут в конце
  `expected/cli`, чтобы не сдвигать номера остальных. В SQLite таких файлов нет: `task-get-future` там — несуществующий id.
- Ошибку `[unavailable]` (область ещё открывается) детерминированно не снять — она зависит от времени; текст —
  `rust/crates/tasker-mcp/src/call.rs`.
- Консоль и десктоп на одной машине — один держатель блокировок, поэтому «Locked:» в консоли не воспроизвести; ошибка `[locked]`
  снята через MCP.
- Описание с CRLF внутри (`Description: crlf`) консоль нормализует в LF до записи.
