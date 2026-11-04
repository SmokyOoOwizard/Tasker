using HotChocolate.Resolvers;

namespace Tasker.Web.Graph;

internal sealed class GraphScope
{
    public const string Key = "tasker.scope";

    private readonly SemaphoreSlim _storage = new(1, 1);

    public static readonly FieldMiddleware Exclusive = next => async context =>
    {
        var scope = (GraphScope)context.ContextData[Key]!;
        await scope._storage.WaitAsync(context.RequestAborted);
        try
        {
            await next(context);
        }
        finally
        {
            scope._storage.Release();
        }
    };
}
