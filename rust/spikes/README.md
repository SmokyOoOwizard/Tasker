# Spikes (TSK-127): rmcp и собственный YAML-эмиттер

Разведка перед фазой 1 плана `docs/rust-migration-plan.md`. Оба крейта — самостоятельные, не входят в будущий workspace
`rust/`; их код пойдёт в `tasker-mcp`/`tasker-mcpd` и `tasker-files` в переработанном виде.

## `rmcp-echo` — rmcp 3.5.1, Streamable HTTP без сессий

`cargo run -- 5799`, затем запросы как в `scripts/golden/generate.py` (POST `/mcp`, `Accept: application/json, text/event-stream`).

Проверено и подходит:

- **Stateless**: `StreamableHttpServerConfig::default().with_legacy_session_mode(false)` + `NeverSessionManager` — каждый POST
  самостоятелен, `tools/call` без `initialize` работает, заголовок `Mcp-Session-Id` не выдаётся (как у .NET SDK в stateless-режиме).
- **Схемы руками**: `Tool::new(name, description, Arc<JsonObject>)` принимает любой JSON Schema — `workspace` добавляется,
  `projectId` не в `required`. Список отдаётся из `ServerHandler::list_tools`, вызов — из `call_tool` по имени: никаких макросов
  и рефлексии, 62 инструмента описываются таблицей.
- **Заголовки HTTP** (`X-Tasker-Agent`, `Host`): `context.extensions.get::<http::request::Parts>()`.
- **Ошибки инструментов**: `CallToolResult::error(vec![ContentBlock::text("[not_found] …")])` → `isError: true`, текст без
  префикса SDK; неизвестный инструмент — свой текст (`Unknown tool: 'x'`, как в снапшоте .NET).
- **Loopback**: `Host` проверяется по умолчанию (`localhost`, `127.0.0.1`, `::1`; чужой Host → 403), `Origin` — по списку.
- **Ответ**: `text/event-stream` с одной строкой `data: {…}` (как у .NET SDK); `with_json_response(true)` даёт чистый JSON.
- **Сырой UTF-8 на проводе**: serde_json не экранирует не-ASCII — `WireJson` повторять не нужно.

Отличия, которые придётся учесть в `tasker-mcpd`:

- rmcp **не проверяет аргументы по `inputSchema`** (пропущенный `required`, неверный тип уходят в инструмент как есть); .NET SDK
  отвечает ошибкой «An error occurred invoking '…'». Валидацию делать самим в слое вызова (нужна и для текстов `[invalid]`).
- Порядок ключей и `isError: false` в успешном ответе: rmcp пишет `{"jsonrpc","id","result"}` и `"isError":false`, .NET —
  `{"result","id","jsonrpc"}` и без `isError`. Сравнение со снапшотами `expected/mcp/` — семантическое (разбор JSON), не байтовое;
  `inputSchema` для `tools/list` строить с сохранением порядка ключей (`serde_json` с `preserve_order`).
- `claude mcp add` проверить до конца не удалось: сервер проектной области ждёт интерактивного одобрения в `claude`
  (`⏸ Pending approval`); протокол проверен запросами, идентичными тем, что шлёт тестовый клиент .NET.

Решение: **rmcp подходит**, свой JSON-RPC-слой не нужен.

## `yaml-emitter` — свой эмиттер вместо подгонки чужого

`cargo test`. Два теста на корпусе `rust/tests/golden`:

- `scalars_match_yamldotnet` — все строки-пробы из `yaml-style/*.json` записываются ровно как YamlDotNet 18.1;
- `workspace_files_round_trip` — все 120 файлов канонической области: разбор `saphyr` → `emit` даёт те же байты.

Эмиттер — порт `Emitter.AnalyzeScalar`/`SelectScalarStyle` YamlDotNet (≈300 строк) с настройками Tasker. Что в нём неочевидно:

- анализ идёт по **UTF-16-единицам**, как в .NET: суррогатные пары (эмодзи) «непечатаемые» → двойные кавычки и `\U0001F680`;
- `WithQuotingNecessaryStrings` — регулярное выражение YAML 1.2 (`null`, `~`, `true/false`, числа, `0x…`, `1e3`, `.inf`),
  поэтому `yes`/`no`/`off` пишутся без кавычек, а `"007"`, `"1.5"`, `"~"` — в двойных;
- одинарные кавычки — для строк с `: `, `#`, ведущими `-`/`[`/`{`/`'`… без собственных `'`; иначе двойные;
- многострочные строки — литеральный блок: `|-` без завершающего перевода строки, `|` с одним, `|+` с двумя, `|2-` когда текст
  начинается с пробела или пустой строки; строка из одних пробелов внутри или ` ` → двойные кавычки с экранированием;
- завершающие переводы строки литерала (`|`, `|+`) служат концом строки документа — отдельный `\n` после них не пишется;
- ширина строки не ограничена (`BestWidth = int.MaxValue`), переносов плоских строк нет.

Разбор: `saphyr` 0.1 читает все файлы корпуса, порядок ключей сохраняется (`hashlink`), `"007"` остаётся строкой, `1` — целым,
`false` — bool. Решение: **свой эмиттер + saphyr для чтения**, как и предполагал план.
