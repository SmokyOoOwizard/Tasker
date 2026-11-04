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
