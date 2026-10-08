using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core;
using Tasker.Core.Agents;
using Tasker.Core.Auth;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;
using Tasker.Core.Workspace;
using Tasker.Mcp;
using Tasker.Web.Auth;

namespace Tasker.Web;

/// <summary>
/// HTTP API. Всё, кроме самих проектов, живёт под /api/projects/{projectId}/… —
/// сущности разных проектов не пересекаются.
/// Изменение — PATCH (поля null не меняются), удаление — DELETE (204; 409, если сущность используется).
/// Оба требуют версию, которую видел клиент: PATCH — в теле (<c>version</c>), DELETE — в <c>?version=</c>.
/// Если запись уже изменил кто-то другой — 409 с кодом <c>modified</c> (см. Tasker.Core.Versioning).
/// Списки — страницами <c>{totalCount, offset, limit, data}</c>: <c>?offset=&amp;limit=</c>, limit до <see cref="Page.MaxLimit"/>.
/// </summary>
internal static class TaskerApi
{
    /// <summary>Тело <c>POST /tasks/{id}/series</c>.</summary>
    private record AddTaskToSeries(Guid SeriesId, string? Version);

    /// <summary>Тело <c>POST /tasks/{id}/links</c>: связь «эта задача —<c>typeId</c>→ <c>targetId</c>»; version — версия этой задачи (необязательна).</summary>
    private record AddTaskLink(Guid TypeId, Guid TargetId, string? Version);

    /// <summary>Тело <c>POST /tasks/{id}/series/{seriesId}/renumber</c>: <c>to</c> null — следующий свободный номер.</summary>
    private record RenumberTask(int? To, string? Version);

    public static void MapTaskerApi(this IEndpointRouteBuilder api, TaskerMode mode)
    {
        // Сервер: без входа доступны только /health, регистрация первого пользователя и вход.
        var root = mode == TaskerMode.Server
            ? api.MapGroup("").RequireAuthorization()
            : api.MapGroup("");

        if (mode == TaskerMode.Server)
            MapAuth(api, root);
        MapUsers(root, mode);
        MapAgents(root, mode);

        // Файлы .tasker, которые не удалось прочитать (например, конфликт слияния git): их сущностей нет в списках.
        // Есть только при хранении в файлах; иначе список пустой.
        if (mode != TaskerMode.Server)
        {
            root.MapGet("/workspace/problems", async (IServiceProvider services, int? offset, int? limit, CancellationToken ct) =>
                Results.Ok(services.GetService<IWorkspaceIndex>() is { } workspace
                    ? await workspace.GetProblems(Page.Of(offset, limit), ct)
                    : Page.Of(offset, limit).Apply(Array.Empty<WorkspaceProblem>())));

            // Адрес MCP и имя этой рабочей области для агента (аргумент workspace). Адрес общий для всех областей. Если работает
            // демон MCP, десктоп свой MCP не поднимает: адрес — у демона (если область ему разрешена); иначе — постоянный порт десктопа.
            root.MapGet("/workspace/mcp", async Task<IResult> (HttpContext context, IServiceProvider services, CancellationToken ct) =>
            {
                const string agentHeader = McpRegistration.AgentHeader;
                var feature = context.Features.Get<Workspaces.WorkspaceFeature>();
                var external = feature != null && services.GetService<IExternalMcp>() is { } finder
                    ? await finder.Find(feature.Location, ct)
                    : null;

                if (external is { DaemonRunning: true })
                {
                    return Results.Ok(new { url = external.Url, workspace = external.Workspace, agentHeader, source = "daemon", allowed = external.Url != null });
                }

                return Results.Ok(services.GetService<LocalMcpAddress>()?.Port is { } port
                    ? new { url = (string?)McpRegistration.LocalUrl(port), workspace = feature?.Key, agentHeader, source = (string?)"desktop", allowed = true }
                    : new { url = (string?)null, workspace = (string?)null, agentHeader, source = (string?)null, allowed = false });
            });
        }

        root.MapGet("/projects", async (ProjectService service, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await service.GetRange(Page.Of(offset, limit), ct)));

        root.MapPost("/projects", async (ProjectService service, CreateProject command, CancellationToken ct) =>
            Created(await service.Create(command, ct)));

        root.MapGet("/projects/{projectId:guid}", async (ProjectService service, Guid projectId, CancellationToken ct) =>
            OkOrNotFound(await service.GetById(projectId, ct)));

        // Сколько чего в проекте (для подтверждения удаления). Недоступный проект — 404, как и сам проект.
        root.MapGet("/projects/{projectId:guid}/stats", async (ProjectService service, Guid projectId, CancellationToken ct) =>
            OkOrNotFound(await service.GetStats(projectId, ct)));

        root.MapPatch("/projects/{projectId:guid}", async (ProjectService service, Guid projectId, UpdateProject command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, command, ct)));

        // Удаляет проект со всем содержимым.
        root.MapDelete("/projects/{projectId:guid}", async (ProjectService service, Guid projectId, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, version, ct)));

        var project = root.MapGroup("/projects/{projectId:guid}")
            .AddEndpointFilter(RequireProject);

        if (mode == TaskerMode.Server)
            MapMembers(project);

        // description — необязательное описание статуса (в POST/PATCH и в ответах; пустая строка в PATCH очищает). ?descriptionLength= в списке — как в /tasks:
        // не задан или -1 — полное, 0 — без описания, N — первые N символов (descriptionTruncated, descriptionLength — полная длина); меньше -1 — 400.
        project.MapGet("/statuses", async (StatusService service, Guid projectId, int? offset, int? limit, int? descriptionLength, CancellationToken ct) =>
            Results.Ok(await service.List(projectId, Page.Of(offset, limit), DescriptionPreview.Check(descriptionLength), ct)));

        project.MapGet("/statuses/{id:guid}", async (IStatusStorage storage, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await storage.GetById(projectId, id, ct)));

        project.MapPost("/statuses", async (StatusService service, Guid projectId, CreateStatus command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/statuses/{id:guid}", async (StatusService service, Guid projectId, Guid id, UpdateStatus command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        project.MapDelete("/statuses/{id:guid}", async (StatusService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        project.MapGet("/status-sets", async (IStatusSetStorage storage, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await storage.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapGet("/status-sets/{id:guid}", async (IStatusSetStorage storage, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await storage.GetById(projectId, id, ct)));

        project.MapPost("/status-sets", async (StatusSetService service, Guid projectId, CreateStatusSet command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/status-sets/{id:guid}", async (StatusSetService service, Guid projectId, Guid id, UpdateStatusSet command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        project.MapDelete("/status-sets/{id:guid}", async (StatusSetService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // description — как у статусов; ?descriptionLength= в списке — так же.
        project.MapGet("/task-types", async (TaskTypeService service, Guid projectId, int? offset, int? limit, int? descriptionLength, CancellationToken ct) =>
            Results.Ok(await service.List(projectId, Page.Of(offset, limit), DescriptionPreview.Check(descriptionLength), ct)));

        project.MapGet("/task-types/{id:guid}", async (ITaskTypeStorage storage, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await storage.GetById(projectId, id, ct)));

        project.MapPost("/task-types", async (TaskTypeService service, Guid projectId, CreateTaskType command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        // Поля типа — fields [{fieldId, required}] (заменяются целиком); убрали поле, у задач которого есть значения, —
        // с выбором removedFields: "clear" (убрать значения у задач) или "keep" (поле станет дополнительным у задач); без выбора — 409 in_use.
        // Ответ — тип и affectedTasks (число затронутых задач).
        project.MapPatch("/task-types/{id:guid}", async (TaskTypeService service, Guid projectId, Guid id, UpdateTaskType command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        project.MapDelete("/task-types/{id:guid}", async (TaskTypeService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // Каталог полей и перечислений проекта. PATCH поля меняет имя, type, multiple и enumId вместе со значениями задач (атомарно);
        // если значения нельзя сохранить как есть — 409 in_use с числом задач, пока в теле нет choice {clearUnconvertible, several: keepFirst|clear,
        // mapping: [{from, to}]}.
        // Поле, которое подключено к типу или записано у задачи, и перечисление, на которое ссылаются поля, удалить нельзя (409 in_use).
        project.MapGet("/fields", async (FieldService service, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await service.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapGet("/fields/{id:guid}", async (FieldService service, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await service.GetById(projectId, id, ct)));

        project.MapPost("/fields", async (FieldService service, Guid projectId, CreateField command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/fields/{id:guid}", async (FieldService service, Guid projectId, Guid id, UpdateField command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        project.MapDelete("/fields/{id:guid}", async (FieldService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // PATCH заменяет список значений целиком; удаляемое значение, выбранное у задач, — только с выбором в теле:
        // removed {clear: true} или removed {reassignTo: <id оставшегося значения>}; без выбора — 409 in_use с числом задач.
        // Ответ — перечисление и affectedTasks (число переписанных задач).
        project.MapGet("/enums", async (FieldEnumService service, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await service.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapGet("/enums/{id:guid}", async (FieldEnumService service, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await service.GetById(projectId, id, ct)));

        project.MapPost("/enums", async (FieldEnumService service, Guid projectId, CreateFieldEnum command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/enums/{id:guid}", async (FieldEnumService service, Guid projectId, Guid id, UpdateFieldEnum command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        project.MapDelete("/enums/{id:guid}", async (FieldEnumService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // Типы связей между задачами (Blocks, Duplicate… — по умолчанию как в Jira): у каждого два названия, по одному на сторону связи.
        // В проекте без типов они создаются при первом обращении.
        project.MapGet("/link-types", async (LinkTypeService service, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await service.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapGet("/link-types/{id:guid}", async (LinkTypeService service, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await service.GetById(projectId, id, ct)));

        project.MapPost("/link-types", async (LinkTypeService service, Guid projectId, CreateLinkType command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/link-types/{id:guid}", async (LinkTypeService service, Guid projectId, Guid id, UpdateLinkType command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        // Тип, по которому есть связи, удалить нельзя (409 in_use).
        project.MapDelete("/link-types/{id:guid}", async (LinkTypeService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // ?statusId=, ?typeId=, ?seriesId= (серия: задачи, у которых есть номер в ней): можно повторять (?statusId=A&statusId=B) — значения одного параметра по ИЛИ, разные параметры
        // (статус, тип, серия, field) по И; одно значение работает как раньше (TaskFilter.Of). ?field=Имя=значение (можно повторять, условия по И): условие по полю каталога или собственному полю задач (то же имя и тип) —
        // = и != (любой тип), > >= < <= (int, float, date; в URL знаки кодируются), Имя:set|unset|attached|detached (TaskService.ParseFieldFilters).
        // Неверное имя, оператор или значение — 400. ?sort=: порядок — ключи через запятую, «-» перед ключом — по убыванию
        // (status,-updated,Имя поля; TaskService.ParseSort); задачи без значения — в конце, равные — по времени создания; неверный ключ — 400. ?descriptionLength=: описание в записях списка — -1 или не задан: полное (как раньше),
        // 0: без описания, N: первые N символов (descriptionTruncated, descriptionLength — полная длина, linksCount — число связей); меньше -1 — 400.
        // Иерархия (иерархические типы связей, TaskService.ListTree): в каждой записи parentIds (id родителей) и childCount (число дочерних); список по умолчанию плоский,
        // чтобы не менять клиентов. ?flat=false — строки деревом: эпик, под ним его дочерние (у записи depth и repeated — повтор под вторым родителем), offset/limit — по верхнему
        // уровню (поддеревья целиком), totalCount — уникальные задачи, topLevelCount — задачи верхнего уровня.
        project.MapGet("/tasks", async (TaskService service, Guid projectId, Guid[]? statusId, Guid[]? typeId, Guid[]? seriesId, string[]? field, int? offset, int? limit, int? descriptionLength, string? sort, bool? flat, CancellationToken ct) =>
            flat == false
                ? Results.Ok(await service.ListTree(projectId, TaskFilter.Of(typeId, statusId, seriesId), field, Page.Of(offset, limit), DescriptionPreview.Check(descriptionLength), ct, sort))
                : Results.Ok(await service.List(projectId, TaskFilter.Of(typeId, statusId, seriesId), field, Page.Of(offset, limit),
                    DescriptionPreview.Check(descriptionLength), ct, sort)));

        // Задачи по ссылке: Guid или «ПРЕФИКС-номер» (TSK-5). При дубликате номера — несколько. Неверная ссылка — 400.
        // Маршрут литеральный, а {id:guid} слова «resolve» не примет, так что они не пересекаются.
        project.MapGet("/tasks/resolve", async (TaskService service, Guid projectId, string? @ref, CancellationToken ct) =>
            Results.Ok(await service.Resolve(projectId, @ref ?? "", ct)));

        // Одна задача — вместе с видом её полей (fieldViews: определения, источник, названия значений enum) и связями с обеих сторон (linkViews, linkCount); в списках их нет.
        project.MapGet("/tasks/{id:guid}", async (TaskService service, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await service.Describe(projectId, id, ct)));

        // Значения полей и дополнительные/собственные поля — в теле: fields {values, addFields, newOwnFields, removeFields}.
        project.MapPost("/tasks", async (TaskService service, Guid projectId, CreateTask command, CancellationToken ct) =>
            Created(await service.Describe(projectId, await service.Create(projectId, command, ct), ct)));

        project.MapPatch("/tasks/{id:guid}", async (TaskService service, Guid projectId, Guid id, UpdateTask command, CancellationToken ct) =>
            await service.Update(projectId, id, command, ct) is { } updated
                ? Results.Ok(await service.Describe(projectId, updated, ct))
                : Results.NotFound());

        project.MapDelete("/tasks/{id:guid}", async (TaskService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // Номера задач в сериях. Возвращают изменённую задачу; версия — та, что клиент видел у задачи.
        project.MapPost("/tasks/{id:guid}/series", async (TaskService service, Guid projectId, Guid id, AddTaskToSeries command, CancellationToken ct) =>
            OkOrNotFound(await service.AddToSeries(projectId, id, command.SeriesId, command.Version, ct)));

        project.MapDelete("/tasks/{id:guid}/series/{seriesId:guid}", async (TaskService service, Guid projectId, Guid id, Guid seriesId, string? version, CancellationToken ct) =>
            OkOrNotFound(await service.RemoveFromSeries(projectId, id, seriesId, version, ct)));

        project.MapPost("/tasks/{id:guid}/series/{seriesId:guid}/renumber",
            async (TaskService service, Guid projectId, Guid id, Guid seriesId, RenumberTask command, CancellationToken ct) =>
                OkOrNotFound(await service.Renumber(projectId, id, seriesId, command.To, command.Version, ct)));

        // Связи задачи: GET — все, и исходящие, и входящие, с названием типа со стороны этой задачи («blocks» / «is blocked by»).
        // POST добавляет исходящую связь, DELETE (?typeId=&targetId=) убирает; version — версия этой задачи, необязательна.
        // Возвращают изменённую задачу. Связи только внутри проекта; задача-цель при этом не меняется.
        project.MapGet("/tasks/{id:guid}/links", async (TaskLinkService service, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await service.GetLinks(projectId, id, ct)));

        project.MapPost("/tasks/{id:guid}/links", async (TaskLinkService service, Guid projectId, Guid id, AddTaskLink command, CancellationToken ct) =>
            OkOrNotFound(await service.Add(projectId, id, command.TypeId, command.TargetId, command.Version, ct)));

        project.MapDelete("/tasks/{id:guid}/links", async (TaskLinkService service, Guid projectId, Guid id, Guid typeId, Guid targetId, string? version, CancellationToken ct) =>
            OkOrNotFound(await service.Remove(projectId, id, typeId, targetId, version, ct)));

        // Серии проекта. /series/health — литерал, {id:guid} его не перехватывает.
        project.MapGet("/series", async (ISeriesStorage storage, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await storage.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapGet("/series/health", async (SeriesHealthService health, Guid projectId, CancellationToken ct) =>
            Results.Ok(await health.Check(projectId, ct)));

        project.MapGet("/series/{id:guid}", async (ISeriesStorage storage, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await storage.GetById(projectId, id, ct)));

        project.MapPost("/series", async (SeriesService service, Guid projectId, CreateSeries command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/series/{id:guid}", async (SeriesService service, Guid projectId, Guid id, UpdateSeries command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        // Серию с задачами удалять можно: ссылки на неё из задач убираются в той же операции.
        project.MapDelete("/series/{id:guid}", async (SeriesService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        project.MapGet("/boards", async (IBoardStorage storage, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await storage.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapGet("/boards/{id:guid}", async (IBoardStorage storage, Guid projectId, Guid id, CancellationToken ct) =>
            OkOrNotFound(await storage.GetById(projectId, id, ct)));

        project.MapPost("/boards", async (BoardService service, Guid projectId, CreateBoard command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)));

        project.MapPatch("/boards/{id:guid}", async (BoardService service, Guid projectId, Guid id, UpdateBoard command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)));

        project.MapDelete("/boards/{id:guid}", async (BoardService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)));

        // Задачи колонки грузятся постранично и отдельно для каждой колонки. ?field=… — срез по полям, ?sort=… — порядок внутри колонки, ?descriptionLength= — усечение описаний, как в /tasks.
        project.MapGet("/boards/{id:guid}/columns/{columnId:guid}/tasks",
            async (BoardService boards, Guid projectId, Guid id, Guid columnId, string[]? field, int? offset, int? limit, int? descriptionLength, string? sort, CancellationToken ct) =>
                OkOrNotFound(await boards.GetColumnTasks(projectId, id, columnId, Page.Of(offset, limit), ct, field, DescriptionPreview.Check(descriptionLength), sort)));

        // Перенос задачи в колонку: статус берётся из правила переноса колонки. Возвращает изменённую задачу.
        project.MapPost("/boards/{id:guid}/columns/{columnId:guid}/move",
            async (BoardService boards, Guid projectId, Guid id, Guid columnId, MoveTask command, CancellationToken ct) =>
                OkOrNotFound(await boards.MoveTask(projectId, id, columnId, command, ct)));

        MapLocks(project);
    }

    /// <summary>
    /// Блокировка на время правки (см. Tasker.Core.Locks): <c>POST …/lock</c> берёт её или продлевает свою,
    /// <c>GET</c> показывает, <c>DELETE</c> снимает свою (204, даже если снимать было нечего).
    /// Занято другим — 409 с кодом <c>locked</c> и <c>heldBy</c>. Для проекта — <c>/projects/{id}/lock</c>.
    /// Пока блокировку держит другой, изменить и удалить сущность нельзя; свободную можно, и без блокировки.
    /// </summary>
    private static void MapLocks(RouteGroupBuilder project)
    {
        // Все действующие блокировки проекта — для индикаторов «правит Иван» в списках.
        project.MapGet("/locks", async (EntityLockService locks, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(Page.Of(offset, limit).Apply(await locks.GetByProject(projectId, ct))));

        MapLock(project, "/lock", LockedEntity.Project);
        MapLock(project, "/tasks/{id:guid}/lock", LockedEntity.Task);
        MapLock(project, "/task-types/{id:guid}/lock", LockedEntity.TaskType);
        MapLock(project, "/statuses/{id:guid}/lock", LockedEntity.Status);
        MapLock(project, "/status-sets/{id:guid}/lock", LockedEntity.StatusSet);
        MapLock(project, "/boards/{id:guid}/lock", LockedEntity.Board);
        MapLock(project, "/series/{id:guid}/lock", LockedEntity.Series);
        MapLock(project, "/link-types/{id:guid}/lock", LockedEntity.LinkType);
        MapLock(project, "/fields/{id:guid}/lock", LockedEntity.Field);
        MapLock(project, "/enums/{id:guid}/lock", LockedEntity.Enum);
    }

    private static void MapLock(RouteGroupBuilder project, string pattern, LockedEntity entity)
    {
        // У самого проекта в маршруте только projectId — он и есть id блокируемой сущности.
        static Guid IdOf(HttpContext http, LockedEntity entity) =>
            Guid.Parse((string)http.Request.RouteValues[entity == LockedEntity.Project ? "projectId" : "id"]!);

        project.MapPost(pattern, async (HttpContext http, EntityLockService locks, Guid projectId, CancellationToken ct) =>
            OkOrNotFound(await locks.Acquire(projectId, entity, IdOf(http, entity), ct)));

        project.MapGet(pattern, async (HttpContext http, EntityLockService locks, CancellationToken ct) =>
            OkOrNotFound(await locks.Get(entity, IdOf(http, entity), ct)));

        project.MapDelete(pattern, async (HttpContext http, EntityLockService locks, CancellationToken ct) =>
        {
            await locks.Release(entity, IdOf(http, entity), ct);
            return Results.NoContent();
        });
    }

    /// <summary>Вход (только сервер). Регистрация открыта, только пока на сервере нет ни одного пользователя.</summary>
    private static void MapAuth(IEndpointRouteBuilder anonymous, IEndpointRouteBuilder secured)
    {
        // Экран входа: показывать ли форму регистрации первого админа.
        anonymous.MapGet("/auth/registration", async (IUserStorage users, CancellationToken ct) =>
            Results.Ok(new { open = await users.Count(ct: ct) == 0 }));

        anonymous.MapPost("/auth/register", async (AuthService auth, RegisterUser command, CancellationToken ct) =>
            Created(await auth.Register(command, ct)));

        anonymous.MapPost("/auth/login", async (AuthService auth, SignIn command, CancellationToken ct) =>
            await auth.SignIn(command, ct) is { } session
                ? Results.Ok(session)
                : Unauthorized("Invalid username/email or password"));

        // Без access-токена: к моменту обмена он обычно уже истёк.
        anonymous.MapPost("/auth/refresh", async (AuthService auth, RefreshSession command, CancellationToken ct) =>
            await auth.Refresh(command, ct) is { } session
                ? Results.Ok(session)
                : Unauthorized("Refresh token is invalid, expired or already used: sign in again"));

        anonymous.MapPost("/auth/logout", async (AuthService auth, RefreshSession command, CancellationToken ct) =>
        {
            await auth.SignOut(command, ct);
            return Results.NoContent();
        });

        secured.MapGet("/auth/me", async (UserService users, CancellationToken ct) =>
            Results.Ok(await users.GetCurrent(ct)));
    }

    private static IResult Unauthorized(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Пользователи. На сервере создаёт и удаляет админ, остальные меняют только себя (права — в UserService).
    /// На десктопе — просто список имён.
    /// </summary>
    private static void MapUsers(IEndpointRouteBuilder root, TaskerMode mode)
    {
        root.MapGet("/users", async (IUserStorage storage, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await storage.GetRange(null, Page.Of(offset, limit), ct)));

        root.MapGet("/users/{id:guid}", async (IUserStorage storage, Guid id, CancellationToken ct) =>
            OkOrNotFound(await storage.GetById(id, ct)));

        root.MapPost("/users", async (UserService service, CreateUser command, CancellationToken ct) =>
            Created(await service.Create(command, ct)));

        root.MapPatch("/users/{id:guid}", async (UserService service, Guid id, UpdateUser command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(id, command, ct)));

        root.MapDelete("/users/{id:guid}", async (UserService service, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(id, version, ct)));

        if (mode == TaskerMode.Server)
        {
            // Смена пароля завершает все сессии пользователя: access- и refresh-токены перестают действовать.
            root.MapPost("/users/{id:guid}/password", async (AuthService auth, Guid id, ChangePassword command, CancellationToken ct) =>
                NoContentOrNotFound(await auth.ChangePassword(id, command, ct)));
        }
    }

    /// <summary>
    /// Агенты — пользователи для MCP. Сервер: каждый управляет своими агентами, админ — всеми;
    /// токены агента выпускаются здесь и работают только в /mcp. Десктоп: агенты общие, токенов нет.
    /// </summary>
    private static void MapAgents(IEndpointRouteBuilder root, TaskerMode mode)
    {
        root.MapGet("/agents", async (AgentService agents, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await agents.GetRange(Page.Of(offset, limit), ct)));

        root.MapGet("/agents/{id:guid}", async (AgentService agents, Guid id, CancellationToken ct) =>
            OkOrNotFound(await agents.GetById(id, ct)));

        root.MapPost("/agents", async (AgentService agents, CreateAgent command, CancellationToken ct) =>
            Created(await agents.Create(command, ct)));

        root.MapPatch("/agents/{id:guid}", async (AgentService agents, Guid id, UpdateAgent command, CancellationToken ct) =>
            OkOrNotFound(await agents.Update(id, command, ct)));

        root.MapDelete("/agents/{id:guid}", async (AgentService agents, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await agents.Delete(id, version, ct)));

        if (mode != TaskerMode.Server)
            return;

        root.MapGet("/agents/{id:guid}/tokens", async (AgentService agents, Guid id, int? offset, int? limit, CancellationToken ct) =>
            OkOrNotFound(await agents.GetTokens(id, Page.Of(offset, limit), ct)));

        // Значение токена — только в этом ответе: сохраняется лишь его хэш.
        root.MapPost("/agents/{id:guid}/tokens", async (AgentService agents, Guid id, CreateAgentToken command, CancellationToken ct) =>
            await agents.CreateToken(id, command, ct) is { } issued
                ? Created(new { token = issued.Value, info = issued.Token })
                : Results.NotFound());

        root.MapDelete("/agents/{id:guid}/tokens/{tokenId:guid}", async (AgentService agents, Guid id, Guid tokenId, CancellationToken ct) =>
            NoContentOrNotFound(await agents.RevokeToken(id, tokenId, ct)));
    }

    /// <summary>Участники проекта (только сервер). Доступ к проекту уже проверен фильтром группы.</summary>
    private static void MapMembers(IEndpointRouteBuilder project)
    {
        project.MapGet("/members", async (ProjectMemberService service, Guid projectId, int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await service.GetRange(projectId, Page.Of(offset, limit), ct)));

        project.MapPost("/members", async (ProjectMemberService service, Guid projectId, AddProjectMember command, CancellationToken ct) =>
            Created(await service.Add(projectId, command, ct)));

        project.MapDelete("/members/{userId:guid}", async (ProjectMemberService service, Guid projectId, Guid userId, CancellationToken ct) =>
            NoContentOrNotFound(await service.Remove(projectId, userId, ct)));
    }

    /// <summary>
    /// Ошибки из Core: неверные данные → 400 <c>{error}</c>; нет прав → 403 <c>{error}</c>;
    /// конфликт с текущими данными → 409 <c>{error, code}</c>,
    /// где code — <c>in_use</c> (сущность используется) или <c>modified</c> (её изменил кто-то другой).
    /// </summary>
    public static async Task HandleErrors(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (TaskerValidationException e) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = e.Message });
        }
        catch (TaskerNotFoundException e) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = e.Message });
        }
        catch (TaskerForbiddenException e) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = e.Message });
        }
        catch (TaskerConflictException e) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            // Занято блокировкой — клиенту нужно знать, кто правит и до какого времени: «правит Иван».
            object? heldBy = e is Core.Locks.TaskerLockedException locked
                ? new { name = locked.HeldBy.Holder.Name, expiresAt = locked.HeldBy.ExpiresAt }
                : null;
            await context.Response.WriteAsJsonAsync(new { error = e.Message, code = ConflictCodeName(e.Code), heldBy });
        }
    }

    private static string ConflictCodeName(ConflictCode code) => code switch
    {
        ConflictCode.InUse => "in_use",
        ConflictCode.Modified => "modified",
        ConflictCode.Locked => "locked",
        ConflictCode.UnsupportedFormat => "unsupported_format",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    // Несуществующий или недоступный проект — 404 для всех вложенных маршрутов, в том числе POST.
    // Недоступный — тоже 404, а не 403: не раскрываем, какие проекты есть на сервере.
    private static async ValueTask<object?> RequireProject(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var projectId = Guid.Parse((string)http.Request.RouteValues["projectId"]!);
        var projects = http.RequestServices.GetRequiredService<ProjectService>();

        if (await projects.GetById(projectId, http.RequestAborted) == null)
            return Results.NotFound(new { error = $"Project {projectId} not found" });

        return await next(context);
    }

    private static IResult OkOrNotFound<T>(T? value) where T : class =>
        value == null ? Results.NotFound() : Results.Ok(value);

    private static IResult NoContentOrNotFound(bool found) =>
        found ? Results.NoContent() : Results.NotFound();

    // Адрес созданного ресурса не отдаём: клиенту достаточно тела с id.
    private static IResult Created<T>(T value) => Results.Json(value, statusCode: StatusCodes.Status201Created);
}
