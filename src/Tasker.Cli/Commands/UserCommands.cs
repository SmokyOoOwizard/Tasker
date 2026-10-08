using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Agents;
using Tasker.Core.Users;

namespace Tasker.Cli.Commands;

/// <summary>Пользователи и агенты. В консоли, как на десктопе, у пользователя только имя.</summary>
internal static class UserCommands
{
    /// <summary>По id, имени или короткому id (<see cref="ShortId"/>); агент и человек — разные сущности с общими именами.</summary>
    private static async Task<User> Find(Context ctx, string reference, UserKind kind)
    {
        var storage = ctx.Get<IUserStorage>();
        var user = Guid.TryParse(reference, out var id) ? await storage.GetById(id, ctx.Ct) : null;
        user ??= await storage.GetByUsername(reference, ctx.Ct);
        if (user == null && ShortId.TryKey(reference) != null)
        {
            var all = new List<User>();
            while (true)
            {
                var page = await storage.GetRange(new UserFilter { Kind = kind }, new Page(all.Count, Page.MaxLimit), ctx.Ct);
                all.AddRange(page.Data);
                if (page.Data.Length == 0 || all.Count >= page.TotalCount)
                    break;
            }

            user = Refs.ByIdPrefix(all, reference, x => x.Id, kind == UserKind.Agent ? "agent" : "user");
        }

        return user is { } found && found.Kind == kind
            ? found
            : throw new CliException($"No {(kind == UserKind.Agent ? "agent" : "user")} '{reference}'");
    }

    public static Command User(GlobalOptions g)
    {
        var name = Kit.Name("name", "User name");
        var reference = Kit.Ref("User");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.Users);
        
        return Kit.Group("user", "Users (only a name, as in the desktop app)",
            Kit.Leaf(g, "create", "Creates a user", c => c.Arguments.Add(name), async (parse, ctx) =>
            {
                var user = await ctx.Get<UserService>().Create(new CreateUser(parse.GetRequiredValue(name), null, null), ctx.Ct);
                ctx.Print(user, $"Created user '{user.Username}' {user.Id}");
            }),
            Kit.List(g, "Lists users",
                (ctx, page) => ctx.Get<IUserStorage>().GetRange(new UserFilter { Kind = UserKind.Human }, page, ctx.Ct),
                x => Kit.Row(x.Id, x.Username)),
            Kit.Leaf(g, "get", "Shows a user", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var user = await Find(ctx, parse.GetRequiredValue(reference), UserKind.Human);
                ctx.Print(user, Kit.Fields(("id", user.Id), ("name", user.Username), ("created", user.CreatedAt), ("version", user.Version)));
            }),
            Kit.Leaf(g, "update", "Renames a user", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName);
                var user = await Find(ctx, parse.GetRequiredValue(reference), UserKind.Human);
                var updated = await ctx.Get<UserService>().Update(user.Id,
                    new UpdateUser(parse.GetValue(newName), null, null, parse.GetValue(expected) ?? user.Version), ctx.Ct)
                    ?? throw new CliException($"No user '{user.Id}'");
                ctx.Print(updated, $"Updated user '{updated.Username}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a user", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var user = await Find(ctx, parse.GetRequiredValue(reference), UserKind.Human);
                await ctx.Get<UserService>().Delete(user.Id, parse.GetValue(expected) ?? user.Version, ctx.Ct);
                ctx.Print(new { deleted = user.Id }, Kit.Deleted("user", user.Username, user.Id));
            }));
    }

    public static Command Agent(GlobalOptions g)
    {
        var name = Kit.Name("name", "Agent name");
        var reference = Kit.Ref("Agent");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.Agents);
        
        return Kit.Group("agent", "Agents (users for LLMs working through MCP)",
            Kit.Leaf(g, "create", "Creates an agent", c => c.Arguments.Add(name), async (parse, ctx) =>
            {
                var agent = await ctx.Get<AgentService>().Create(new CreateAgent(parse.GetRequiredValue(name)), ctx.Ct);
                ctx.Print(agent, $"Created agent '{agent.Username}' {agent.Id}");
            }),
            Kit.List(g, "Lists agents",
                (ctx, page) => ctx.Get<AgentService>().GetRange(page, ctx.Ct),
                x => Kit.Row(x.Id, x.Username)),
            Kit.Leaf(g, "get", "Shows an agent", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var agent = await Find(ctx, parse.GetRequiredValue(reference), UserKind.Agent);
                ctx.Print(agent, Kit.Fields(("id", agent.Id), ("name", agent.Username), ("created", agent.CreatedAt), ("version", agent.Version)));
            }),
            Kit.Leaf(g, "update", "Renames an agent", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName);
                var agent = await Find(ctx, parse.GetRequiredValue(reference), UserKind.Agent);
                var updated = await ctx.Get<AgentService>().Update(agent.Id,
                    new UpdateAgent(parse.GetValue(newName), parse.GetValue(expected) ?? agent.Version), ctx.Ct)
                    ?? throw new CliException($"No agent '{agent.Id}'");
                ctx.Print(updated, $"Updated agent '{updated.Username}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes an agent", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var agent = await Find(ctx, parse.GetRequiredValue(reference), UserKind.Agent);
                await ctx.Get<AgentService>().Delete(agent.Id, parse.GetValue(expected) ?? agent.Version, ctx.Ct);
                ctx.Print(new { deleted = agent.Id }, Kit.Deleted("agent", agent.Username, agent.Id));
            }));
    }
}
