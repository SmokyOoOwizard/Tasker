# OpenAPI-схема REST API Tasker

`openapi.json` — полное описание REST API: сервера (режим Server, вход по JWT), локального демона (режим Local, без входа, адрес `/w/{workspace}/api/…`) и управляющих маршрутов демона (`/daemon/*`, `/ready`, `/health`, секрет в заголовке `X-Tasker-Control`). У каждой операции в `x-tasker-modes` указано, где она есть. Чтение данных и живые изменения идут через GraphQL: операция `executeGraphQL` (`POST /api/graphql`, подписка отвечает потоком `text/event-stream`), схема GraphQL лежит в `graphql/schema.graphql`.

Схема — вход для генераторов клиентов (Swift: swift-openapi-generator, C#: NSwag/Kiota). Версия OpenAPI — 3.0.

## Как устроено

Файл не пишется руками. Его строит тест `ApiSchemaTests` (`dotnet/tests/Tasker.Tests`): поднимает приложение в режимах Server и Local и управляющие маршруты демона, забирает схему встроенным генератором `Microsoft.AspNetCore.OpenApi`, объединяет и приводит к виду, удобному генераторам клиентов. В рантайм демона и консоли генератор не входит: он подключён только к тестам, NativeAOT не затронут.

Типы ответов берутся из аннотаций маршрутов в `Tasker.Web/TaskerApi.cs` (`RespondsOk`, `RespondsCreated`, `RespondsNoContent`); коды ошибок 400/401/403/404/409 добавляются к маршрутам автоматически.

## Что делать при изменении API

Тест падает, если файл расходится с API. Обновить файл и закоммитить его вместе с изменением:

```bash
TASKER_GOLDEN_UPDATE=1 dotnet test dotnet/tests/Tasker.Tests --filter "FullyQualifiedName~ApiSchemaTests"
```

Новый маршрут без аннотации ответа тест пропустит, но в схеме у него не будет типа успешного ответа: добавьте `.RespondsOk<T>("имя")` и т. п.

Из схемы собирается REST-клиент приложения для macOS (`macos/TaskerAPI`). После обновления файла пересоздайте его командой `scripts/gen-openapi-client.sh`, а после изменения `graphql/schema.graphql` — командой `python3 scripts/gen-graphql-client.py`. Иначе проверка `macOS API client` в CI упадёт.

## Отказы сервера

Любой отказ REST приходит одним форматом, какой бы ни был статус ответа:

```json
{
  "error": "Status 'В работе' is used by 2 task(s); status set 'Основной'; board 'Спринт' (column 'Идёт') and cannot be deleted",
  "code": "status.inUse",
  "params": { "name": "В работе" },
  "usages": [
    { "kind": "tasks", "count": 2, "tasks": ["TSK-1", "TSK-2"] },
    { "kind": "statusSet", "name": "Основной" },
    { "kind": "boardColumn", "board": "Спринт", "column": "Идёт" }
  ]
}
```

- `code` — стабильный код, часть контракта. Клиент показывает сообщение по коду и параметрам, текст не разбирает.
- `params` — параметры кода. Клиентам они нужны для подстановки в перевод; в ошибках доски может быть ещё `columnIndex`: номер колонки, к которой относится отказ.
- `usages` — чем используется сущность. У остальных отказов список пуст.
- `error` — английский текст, запасной вариант. Его показывают MCP и консоль. Код `validation.invalid` означает отказ, который можно получить только через API, поэтому своего перевода у него нет и клиент показывает `error`.

В GraphQL те же данные лежат в `errors[].extensions`: `{ "status": 409, "code": "status.inUse", "params": {…}, "usages": […] }`, а текст — в `errors[].message`. В `schema.graphql` ошибки не описаны: SDL не описывает `extensions`.

### Каталог кодов

Источник — фабрики `TaskerErrors` (`Tasker.Core/Errors/TaskerErrors.cs`): метод с атрибутом `[ErrorCode("область.смысл")]`, параметры метода становятся `params`. Создать отказ иначе нельзя: конструкторы исключений внутренние, обход ловит тест `ErrorCatalogTests`.

`error-codes.json` строит тот же тест из фабрик и сверяет с закоммиченным, поэтому случайное переименование кода роняет тесты. `scripts/gen-strings.py` требует, чтобы на каждый код была строка `error.<код>` в `design/strings.json` на обоих языках, и генерирует каталоги для Swift и веба. Подстановки в строке идут подряд с `{0}` и соответствуют первым параметрам кода.

Новый отказ:

1. Добавить фабрику в `TaskerErrors`.
2. Обновить каталог и схему:

```bash
TASKER_GOLDEN_UPDATE=1 dotnet test dotnet/tests/Tasker.Tests --filter "FullyQualifiedName~ErrorCatalogTests|FullyQualifiedName~ApiSchemaTests"
```

3. Добавить строку `error.<код>` в `design/strings.json` и запустить `python3 -I scripts/gen-strings.py`.
