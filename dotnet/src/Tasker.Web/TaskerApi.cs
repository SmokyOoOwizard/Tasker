using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tasker.Core;
using Tasker.Core.Agents;
using Tasker.Core.Auth;
using Tasker.Core.Boards;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;
using Tasker.Web.Auth;

namespace Tasker.Web;

/// <summary>
/// HTTP API. Всё, кроме самих проектов, живёт под /api/projects/{projectId}/… —
/// сущности разных проектов не пересекаются.
/// Изменение — PATCH (поля null не меняются), удаление — DELETE (204; 409, если сущность используется).
/// Оба требуют версию, которую видел клиент: PATCH — в теле (<c>version</c>), DELETE — в <c>?version=</c>.
/// Если запись уже изменил кто-то другой — 409 с кодом <c>modified</c> (см. Tasker.Core.Versioning).
/// </summary>
internal static class TaskerApi
{
    /// <summary>Тело <c>POST /tasks/{id}/series</c>.</summary>
    private record AddTaskToSeries(Guid SeriesId, string? Version);

    /// <summary>Тело <c>POST /tasks/{id}/links</c>: связь «эта задача —<c>typeId</c>→ <c>targetId</c>»; version — версия этой задачи (необязательна).</summary>
    private record AddTaskLink(Guid TypeId, Guid TargetId, string? Version);

    /// <summary>Тело <c>POST /tasks/{id}/series/{seriesId}/renumber</c>: <c>to</c> null — следующий свободный номер.</summary>
    private record RenumberTask(int? To, string? Version);

    /// <summary>Тело <c>POST /series/{id}/resolve-conflicts</c>: <c>token</c> — токен плана, который клиент видел (GraphQL <c>seriesConflictPlan</c>).</summary>
    private record ResolveSeriesConflicts(string? Token);

    public static void MapTaskerApi(this IEndpointRouteBuilder api, TaskerMode mode)
    {
        // Сервер: без входа доступны только /health, регистрация первого пользователя и вход.
        var root = mode == TaskerMode.Server
            ? api.MapGroup("").RequireAuthorization()
            : api.MapGroup("");
        root.WithApiErrors(mode);

        if (mode == TaskerMode.Server)
        {
            var anonymous = api.MapGroup("");
            anonymous.WithApiErrors(mode);
            MapAuth(anonymous, root);
        }
        MapUsers(root, mode);
        MapAgents(root, mode);

        root.MapPost("/projects", async (ProjectService service, CreateProject command, CancellationToken ct) =>
            Created(await service.Create(command, ct)))
            .RespondsCreated<Project>("createProject");

        root.MapPatch("/projects/{projectId:guid}", async (ProjectService service, Guid projectId, UpdateProject command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, command, ct)))
            .RespondsOk<Project>("updateProject");

        // Удаляет проект со всем содержимым.
        root.MapDelete("/projects/{projectId:guid}", async (ProjectService service, Guid projectId, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, version, ct)))
            .RespondsNoContent("deleteProject");

        var project = root.MapGroup("/projects/{projectId:guid}")
            .AddEndpointFilter(RequireProject);

        if (mode == TaskerMode.Server)
            MapMembers(project);

        // description — необязательное описание статуса (в POST/PATCH и в ответах; пустая строка в PATCH очищает).
        project.MapPost("/statuses", async (StatusService service, Guid projectId, CreateStatus command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<Status>("createStatus");

        project.MapPatch("/statuses/{id:guid}", async (StatusService service, Guid projectId, Guid id, UpdateStatus command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<Status>("updateStatus");

        project.MapDelete("/statuses/{id:guid}", async (StatusService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteStatus");

        project.MapPost("/status-sets", async (StatusSetService service, Guid projectId, CreateStatusSet command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<StatusSet>("createStatusSet");

        project.MapPatch("/status-sets/{id:guid}", async (StatusSetService service, Guid projectId, Guid id, UpdateStatusSet command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<StatusSet>("updateStatusSet");

        project.MapDelete("/status-sets/{id:guid}", async (StatusSetService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteStatusSet");

        // description — как у статусов.
        project.MapPost("/task-types", async (TaskTypeService service, Guid projectId, CreateTaskType command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<TaskType>("createTaskType");

        // Поля типа — fields [{fieldId, required}] (заменяются целиком); убрали поле, у задач которого есть значения, —
        // с выбором removedFields: "clear" (убрать значения у задач) или "keep" (поле станет дополнительным у задач); без выбора — 409 in_use.
        // Ответ — тип и affectedTasks (число затронутых задач).
        project.MapPatch("/task-types/{id:guid}", async (TaskTypeService service, Guid projectId, Guid id, UpdateTaskType command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<CascadeResult<TaskType>>("updateTaskType");

        project.MapDelete("/task-types/{id:guid}", async (TaskTypeService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteTaskType");

        // Каталог полей и перечислений проекта. PATCH поля меняет имя, type, multiple и enumId вместе со значениями задач (атомарно);
        // если значения нельзя сохранить как есть — 409 in_use с числом задач, пока в теле нет choice {clearUnconvertible, several: keepFirst|clear,
        // mapping: [{from, to}]}.
        // Поле, которое подключено к типу или записано у задачи, и перечисление, на которое ссылаются поля, удалить нельзя (409 in_use).
        project.MapPost("/fields", async (FieldService service, Guid projectId, CreateField command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<FieldDefinition>("createField");

        project.MapPatch("/fields/{id:guid}", async (FieldService service, Guid projectId, Guid id, UpdateField command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<FieldDefinition>("updateField");

        project.MapDelete("/fields/{id:guid}", async (FieldService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteField");

        // PATCH заменяет список значений целиком; удаляемое значение, выбранное у задач, — только с выбором в теле:
        // removed {clear: true} или removed {reassignTo: <id оставшегося значения>}; без выбора — 409 in_use с числом задач.
        // Ответ — перечисление и affectedTasks (число переписанных задач).
        project.MapPost("/enums", async (FieldEnumService service, Guid projectId, CreateFieldEnum command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<FieldEnum>("createFieldEnum");

        project.MapPatch("/enums/{id:guid}", async (FieldEnumService service, Guid projectId, Guid id, UpdateFieldEnum command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<CascadeResult<FieldEnum>>("updateFieldEnum");

        project.MapDelete("/enums/{id:guid}", async (FieldEnumService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteFieldEnum");

        // Типы связей между задачами (Blocks, Duplicate… — по умолчанию как в Jira): у каждого два названия, по одному на сторону связи.
        // В проекте без типов они создаются при первом обращении.
        project.MapPost("/link-types", async (LinkTypeService service, Guid projectId, CreateLinkType command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<LinkType>("createLinkType");

        project.MapPatch("/link-types/{id:guid}", async (LinkTypeService service, Guid projectId, Guid id, UpdateLinkType command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<LinkType>("updateLinkType");

        // Тип, по которому есть связи, удалить нельзя (409 in_use).
        project.MapDelete("/link-types/{id:guid}", async (LinkTypeService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteLinkType");

        // Значения полей и дополнительные/собственные поля — в теле: fields {values, addFields, newOwnFields, removeFields}.
        project.MapPost("/tasks", async (TaskService service, Guid projectId, CreateTask command, CancellationToken ct) =>
            Created(await service.Describe(projectId, await service.Create(projectId, command, ct), ct)))
            .RespondsCreated<TaskDetails>("createTask");

        project.MapPatch("/tasks/{id:guid}", async (TaskService service, Guid projectId, Guid id, UpdateTask command, CancellationToken ct) =>
            await service.Update(projectId, id, command, ct) is { } updated
                ? Results.Ok(await service.Describe(projectId, updated, ct))
                : NotFoundResult())
            .RespondsOk<TaskDetails>("updateTask");

        project.MapDelete("/tasks/{id:guid}", async (TaskService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteTask");

        // Номера задач в сериях. Возвращают изменённую задачу; версия — та, что клиент видел у задачи.
        project.MapPost("/tasks/{id:guid}/series", async (TaskService service, Guid projectId, Guid id, AddTaskToSeries command, CancellationToken ct) =>
            OkOrNotFound(await service.AddToSeries(projectId, id, command.SeriesId, command.Version, ct)))
            .RespondsOk<TaskItem>("addTaskToSeries");

        project.MapDelete("/tasks/{id:guid}/series/{seriesId:guid}", async (TaskService service, Guid projectId, Guid id, Guid seriesId, string? version, CancellationToken ct) =>
            OkOrNotFound(await service.RemoveFromSeries(projectId, id, seriesId, version, ct)))
            .RespondsOk<TaskItem>("removeTaskFromSeries");

        project.MapPost("/tasks/{id:guid}/series/{seriesId:guid}/renumber",
            async (TaskService service, Guid projectId, Guid id, Guid seriesId, RenumberTask command, CancellationToken ct) =>
                OkOrNotFound(await service.Renumber(projectId, id, seriesId, command.To, command.Version, ct)))
            .RespondsOk<TaskItem>("renumberTask");

        // Связи задачи: POST добавляет исходящую связь, DELETE (?typeId=&targetId=) убирает; version — версия этой задачи, необязательна.
        // Возвращают изменённую задачу. Связи только внутри проекта; задача-цель при этом не меняется.
        project.MapPost("/tasks/{id:guid}/links", async (TaskLinkService service, Guid projectId, Guid id, AddTaskLink command, CancellationToken ct) =>
            OkOrNotFound(await service.Add(projectId, id, command.TypeId, command.TargetId, command.Version, ct)))
            .RespondsOk<TaskItem>("addTaskLink");

        project.MapDelete("/tasks/{id:guid}/links", async (TaskLinkService service, Guid projectId, Guid id, Guid typeId, Guid targetId, string? version, CancellationToken ct) =>
            OkOrNotFound(await service.Remove(projectId, id, typeId, targetId, version, ct)))
            .RespondsOk<TaskItem>("removeTaskLink");

        project.MapPost("/series", async (SeriesService service, Guid projectId, CreateSeries command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<Series>("createSeries");

        project.MapPatch("/series/{id:guid}", async (SeriesService service, Guid projectId, Guid id, UpdateSeries command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<Series>("updateSeries");

        // Разрешение конфликтов номеров серии: план строит GraphQL (seriesConflictPlan), здесь его подтверждают токеном. Токен не совпал
        // (задачи изменились или план уже применил другой) — 409 modified, ничего не перенумеровано.
        project.MapPost("/series/{id:guid}/resolve-conflicts",
            async (SeriesConflictService service, Guid projectId, Guid id, ResolveSeriesConflicts command, CancellationToken ct) =>
                Results.Ok(await service.Apply(projectId, id, command.Token, ct)))
            .RespondsOk<ConflictPlan>("resolveSeriesConflicts");

        // Серию с задачами удалять можно: ссылки на неё из задач убираются в той же операции.
        project.MapDelete("/series/{id:guid}", async (SeriesService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteSeries");

        project.MapPost("/boards", async (BoardService service, Guid projectId, CreateBoard command, CancellationToken ct) =>
            Created(await service.Create(projectId, command, ct)))
            .RespondsCreated<Board>("createBoard");

        project.MapPatch("/boards/{id:guid}", async (BoardService service, Guid projectId, Guid id, UpdateBoard command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(projectId, id, command, ct)))
            .RespondsOk<Board>("updateBoard");

        project.MapDelete("/boards/{id:guid}", async (BoardService service, Guid projectId, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(projectId, id, version, ct)))
            .RespondsNoContent("deleteBoard");

        // Перенос задачи в колонку: статус берётся из правила переноса колонки. Возвращает изменённую задачу.
        project.MapPost("/boards/{id:guid}/columns/{columnId:guid}/move",
            async (BoardService boards, Guid projectId, Guid id, Guid columnId, MoveTask command, CancellationToken ct) =>
                OkOrNotFound(await boards.MoveTask(projectId, id, columnId, command, ct)))
            .RespondsOk<TaskItem>("moveTaskToBoardColumn");

        MapLocks(project);
    }

    /// <summary>
    /// Блокировка на время правки (см. Tasker.Core.Locks): <c>POST …/lock</c> берёт её или продлевает свою,
    /// <c>DELETE</c> снимает свою (204, даже если снимать было нечего).
    /// Занято другим — 409 с кодом <c>locked</c> и <c>heldBy</c>. Для проекта — <c>/projects/{id}/lock</c>.
    /// Пока блокировку держит другой, изменить и удалить сущность нельзя; свободную можно, и без блокировки.
    /// </summary>
    private static void MapLocks(RouteGroupBuilder project)
    {
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
        var name = entity.ToString();
        MapLockRoutes(project, pattern, entity, name);
    }

    private static void MapLockRoutes(RouteGroupBuilder project, string pattern, LockedEntity entity, string name)
    {
        // У самого проекта в маршруте только projectId — он и есть id блокируемой сущности.
        static Guid IdOf(HttpContext http, LockedEntity entity) =>
            Guid.Parse((string)http.Request.RouteValues[entity == LockedEntity.Project ? "projectId" : "id"]!);

        project.MapPost(pattern, async (HttpContext http, EntityLockService locks, Guid projectId, CancellationToken ct) =>
            OkOrNotFound(await locks.Acquire(projectId, entity, IdOf(http, entity), ct)))
            .RespondsOk<LockInfo>("acquire" + name + "Lock");

        project.MapDelete(pattern, async (HttpContext http, EntityLockService locks, CancellationToken ct) =>
        {
            await locks.Release(entity, IdOf(http, entity), ct);
            return Results.NoContent();
        })
            .RespondsNoContent("release" + name + "Lock");
    }

    /// <summary>Вход (только сервер). Регистрация открыта, только пока на сервере нет ни одного пользователя.</summary>
    private static void MapAuth(IEndpointRouteBuilder anonymous, IEndpointRouteBuilder secured)
    {
        anonymous.MapPost("/auth/register", async (AuthService auth, RegisterUser command, CancellationToken ct) =>
            Created(await auth.Register(command, ct)))
            .RespondsCreated<AuthSession>("register");

        anonymous.MapPost("/auth/login", async (AuthService auth, SignIn command, CancellationToken ct) =>
            await auth.SignIn(command, ct) is { } session
                ? Results.Ok(session)
                : Refusal(TaskerErrors.InvalidCredentials()))
            .RespondsOk<AuthSession>("login");

        // Без access-токена: к моменту обмена он обычно уже истёк.
        anonymous.MapPost("/auth/refresh", async (AuthService auth, RefreshSession command, CancellationToken ct) =>
            await auth.Refresh(command, ct) is { } session
                ? Results.Ok(session)
                : Refusal(TaskerErrors.SessionExpired()))
            .RespondsOk<AuthSession>("refreshSession");

        anonymous.MapPost("/auth/logout", async (AuthService auth, RefreshSession command, CancellationToken ct) =>
        {
            await auth.SignOut(command, ct);
            return Results.NoContent();
        })
            .RespondsNoContent("logout");
    }

    /// <summary>
    /// Пользователи. На сервере создаёт и удаляет админ, остальные меняют только себя (права — в UserService).
    /// На десктопе — просто список имён.
    /// </summary>
    private static void MapUsers(IEndpointRouteBuilder root, TaskerMode mode)
    {
        root.MapPost("/users", async (UserService service, CreateUser command, CancellationToken ct) =>
            Created(await service.Create(command, ct)))
            .RespondsCreated<User>("createUser");

        root.MapPatch("/users/{id:guid}", async (UserService service, Guid id, UpdateUser command, CancellationToken ct) =>
            OkOrNotFound(await service.Update(id, command, ct)))
            .RespondsOk<User>("updateUser");

        root.MapDelete("/users/{id:guid}", async (UserService service, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await service.Delete(id, version, ct)))
            .RespondsNoContent("deleteUser");

        if (mode == TaskerMode.Server)
        {
            // Смена пароля завершает все сессии пользователя: access- и refresh-токены перестают действовать.
            root.MapPost("/users/{id:guid}/password", async (AuthService auth, Guid id, ChangePassword command, CancellationToken ct) =>
                NoContentOrNotFound(await auth.ChangePassword(id, command, ct)))
                .RespondsNoContent("changeUserPassword");
        }
    }

    /// <summary>
    /// Агенты — пользователи для MCP. Сервер: каждый управляет своими агентами, админ — всеми;
    /// токены агента выпускаются здесь и работают только в /mcp. Десктоп: агенты общие, токенов нет.
    /// </summary>
    private static void MapAgents(IEndpointRouteBuilder root, TaskerMode mode)
    {
        root.MapPost("/agents", async (AgentService agents, CreateAgent command, CancellationToken ct) =>
            Created(await agents.Create(command, ct)))
            .RespondsCreated<User>("createAgent");

        root.MapPatch("/agents/{id:guid}", async (AgentService agents, Guid id, UpdateAgent command, CancellationToken ct) =>
            OkOrNotFound(await agents.Update(id, command, ct)))
            .RespondsOk<User>("updateAgent");

        root.MapDelete("/agents/{id:guid}", async (AgentService agents, Guid id, string? version, CancellationToken ct) =>
            NoContentOrNotFound(await agents.Delete(id, version, ct)))
            .RespondsNoContent("deleteAgent");

        if (mode != TaskerMode.Server)
            return;

        // Значение токена — только в этом ответе: сохраняется лишь его хэш.
        root.MapPost("/agents/{id:guid}/tokens", async (AgentService agents, Guid id, CreateAgentToken command, CancellationToken ct) =>
            await agents.CreateToken(id, command, ct) is { } issued
                ? Created(new AgentTokenIssued(issued.Value, issued.Token))
                : NotFoundResult())
            .RespondsCreated<AgentTokenIssued>("createAgentToken");

        root.MapDelete("/agents/{id:guid}/tokens/{tokenId:guid}", async (AgentService agents, Guid id, Guid tokenId, CancellationToken ct) =>
            NoContentOrNotFound(await agents.RevokeToken(id, tokenId, ct)))
            .RespondsNoContent("revokeAgentToken");
    }

    /// <summary>Участники проекта (только сервер). Доступ к проекту уже проверен фильтром группы.</summary>
    private static void MapMembers(IEndpointRouteBuilder project)
    {
        project.MapPost("/members", async (ProjectMemberService service, Guid projectId, AddProjectMember command, CancellationToken ct) =>
            Created(await service.Add(projectId, command, ct)))
            .RespondsCreated<User>("addProjectMember");

        project.MapDelete("/members/{userId:guid}", async (ProjectMemberService service, Guid projectId, Guid userId, CancellationToken ct) =>
            NoContentOrNotFound(await service.Remove(projectId, userId, ct)))
            .RespondsNoContent("removeProjectMember");
    }

    public static async Task HandleErrors(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (TaskerException e) when (!context.Response.HasStarted)
        {
            await WriteError(context, e);
        }
        catch (Exception e) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(TaskerApi)).LogError(e, "Unhandled request error");
            await WriteError(context, TaskerErrors.Unexpected());
        }
    }

    internal static Task WriteError(HttpContext context, TaskerException error)
    {
        context.Response.StatusCode = error.Status;
        return context.Response.WriteAsJsonAsync(ApiError.From(error));
    }

    private static IResult Refusal(TaskerException error) => Results.Json(ApiError.From(error), statusCode: error.Status);

    // Несуществующий или недоступный проект — 404 для всех вложенных маршрутов, в том числе POST.
    // Недоступный — тоже 404, а не 403: не раскрываем, какие проекты есть на сервере.
    private static async ValueTask<object?> RequireProject(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var projectId = Guid.Parse((string)http.Request.RouteValues["projectId"]!);
        var projects = http.RequestServices.GetRequiredService<ProjectService>();

        if (await projects.GetById(projectId, http.RequestAborted) == null)
            return Refusal(TaskerErrors.ProjectNotFound(projectId));

        return await next(context);
    }

    private static IResult NotFoundResult() => Refusal(TaskerErrors.EntityNotFound());

    private static IResult OkOrNotFound<T>(T? value) where T : class =>
        value == null ? NotFoundResult() : Results.Ok(value);

    private static IResult NoContentOrNotFound(bool found) =>
        found ? Results.NoContent() : NotFoundResult();

    // Адрес созданного ресурса не отдаём: клиенту достаточно тела с id.
    private static IResult Created<T>(T value) => Results.Json(value, statusCode: StatusCodes.Status201Created);
}
