using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core.Boards;
using Tasker.Core.Locks;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Global;

namespace Tasker.Cli.Commands;

/// <summary>
/// Блокировка на время правки. Команда живёт недолго, а блокировка действует 2 минуты, поэтому полезна для
/// короткой серии команд («занять, изменить несколько сущностей, освободить»). Держатель — «консоль»:
/// блокировку интерфейса команды не обходят, и наоборот.
/// </summary>
internal static class LockCommands
{
    private const string Kinds = "project, task, taskType, linkType, field, enum, status, statusSet, board, series";

    public static Command Lock(GlobalOptions g)
    {
        var entity = new Argument<string>("entity") { Description = $"Kind of the entity: {Kinds}" };
        var reference = new Argument<string>("id-or-name") { Description = "The entity (id or name; tasks also by PREFIX-number)" };

        entity.Suggests(g, Sources.EntityKinds);
        reference.Suggests(g, Sources.LockedEntity(entity));
        
        void Args(Command c)
        {
            c.Arguments.Add(entity);
            c.Arguments.Add(reference);
        }

        return Kit.Group("lock", "Locks an entity while you edit it, so nobody else changes or deletes it (expires after 2 minutes)",
            Kit.Leaf(g, "acquire", "Locks an entity, or renews your lock", Args, async (parse, ctx) =>
            {
                var (kind, projectId, id) = await Resolve(ctx, parse.GetRequiredValue(entity), parse.GetRequiredValue(reference));
                var held = await ctx.Get<EntityLockService>().Acquire(projectId, kind, id, ctx.Ct)
                    ?? throw new CliException($"No {parse.GetRequiredValue(entity)} '{parse.GetRequiredValue(reference)}'");
                ctx.Print(held, $"Locked {kind} {id} until {held.ExpiresAt:yyyy-MM-dd HH:mm:ss zzz}");
            }),
            Kit.Leaf(g, "release", "Releases your lock (nothing to release is not an error)", Args, async (parse, ctx) =>
            {
                var (kind, _, id) = await Resolve(ctx, parse.GetRequiredValue(entity), parse.GetRequiredValue(reference));
                var released = await ctx.Get<EntityLockService>().Release(kind, id, ctx.Ct);
                ctx.Print(new { released }, released ? $"Released {kind} {id}" : $"{kind} {id} is not locked by you");
            }),
            Kit.Leaf(g, "show", "Shows who is editing an entity", Args, async (parse, ctx) =>
            {
                var (kind, _, id) = await Resolve(ctx, parse.GetRequiredValue(entity), parse.GetRequiredValue(reference));
                var held = await ctx.Get<EntityLockService>().Get(kind, id, ctx.Ct);
                ctx.Print(new { @lock = held }, held == null
                    ? $"{kind} {id} is not locked"
                    : $"{kind} {id} is edited by {held.Holder}{(held.Mine ? " (you)" : "")} until {held.ExpiresAt:yyyy-MM-dd HH:mm:ss zzz}");
            }));
    }

    /// <summary>Кто вы для остальных: имя в блокировках («правит Иван») на десктопе и в консоли.</summary>
    public static Command Whoami(GlobalOptions g, SettingsStore settings)
    {
        var name = new Argument<string?>("name") { Description = "New name; without it the current one is shown", Arity = ArgumentArity.ZeroOrOne }.NoSuggestions();
        var clear = new Option<bool>("--clear") { Description = "Use the operating system user name again" };

        return Kit.Plain(g, "whoami", "Shows or sets your name that others see in edit locks (shared by the desktop app and the console)", c =>
        {
            c.Arguments.Add(name);
            c.Options.Add(clear);
        }, async (parse, ctx) =>
        {
            var value = parse.GetValue(name);
            if (value != null && parse.GetValue(clear))
                throw new CliException("Use either a name or --clear, not both");

            if (value != null || parse.GetValue(clear))
            {
                await settings.SetUserName(value, ctx.Ct);
                var changed = settings.Load().UserName ?? OsUser.Name;
                ctx.Print(new { name = changed }, $"Your name is {changed}");
                return;
            }

            var current = settings.Load().UserName ?? OsUser.Name;
            ctx.Print(new { name = current }, current);
        });
    }

    private static async Task<(LockedEntity Kind, Guid? ProjectId, Guid Id)> Resolve(Context ctx, string entity, string reference)
    {
        var kind = EntityLockService.ParseEntity(entity);
        if (kind == LockedEntity.User)
            throw new CliException($"Users cannot be locked: use one of {Kinds}");

        var project = await ctx.OptionalProject();
        if (kind == LockedEntity.Project)
            return (kind, project?.Id, (await ctx.FindProject(reference)).Id);

        var projectId = await ctx.ProjectId();
        var id = kind switch
        {
            LockedEntity.Task => await FindTask(ctx, projectId, reference),
            LockedEntity.TaskType => Refs.Find(await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "task type"),
            LockedEntity.LinkType => Refs.Find(await ctx.Get<Core.Links.LinkTypeService>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "link type"),
            LockedEntity.Field => Refs.Find(await ctx.Get<Core.Fields.FieldService>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "field"),
            LockedEntity.Enum => Refs.Find(await ctx.Get<Core.Fields.FieldEnumService>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "enum"),
            LockedEntity.Status => Refs.Find(await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "status"),
            LockedEntity.StatusSet => Refs.Find(await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "status set"),
            LockedEntity.Board => Refs.Find(await ctx.Get<IBoardStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "board"),
            _ => (await SeriesCommands.Find(ctx, projectId, reference)).Id
        };
        return (kind, projectId, id);
    }

    private static async Task<Guid> FindTask(Context ctx, Guid projectId, string reference)
    {
        var found = await TaskCommands.FindAll(ctx, projectId, reference);
        return found.Length == 1
            ? found[0].Id
            : throw new CliException($"Several tasks match '{reference}', use the id: {string.Join(", ", found.Select(x => x.Id))}");
    }
}
