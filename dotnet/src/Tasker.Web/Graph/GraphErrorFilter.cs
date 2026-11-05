using HotChocolate;
using HotChocolate.Execution;
using Tasker.Core;

namespace Tasker.Web.Graph;

internal sealed class GraphErrorFilter : IErrorFilter
{
    public IError OnError(IError error)
    {
        if (error.Exception is TaskerException refusal)
            return Refuse(error.WithMessage(refusal.Message), refusal.Error.Code, refusal.Status, refusal.Error.Params, refusal.Error.Usages).WithException(null);
        if (error.Exception != null)
            return error;
        return Refuse(error, TaskerErrors.Invalid(error.Message).Error.Code, 400, new Dictionary<string, object?>(), []);
    }

    private static IError Refuse(IError error, string code, int status, IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<ErrorUsage> usages) =>
        error.WithCode(code)
            .SetExtension("status", status)
            .SetExtension("params", parameters.ToDictionary(x => x.Key, x => x.Value))
            .SetExtension("usages", usages.Select(Usage).ToArray());

    private static Dictionary<string, object?> Usage(ErrorUsage usage)
    {
        var result = new Dictionary<string, object?> { ["kind"] = usage.Kind };
        if (usage.Name != null)
            result["name"] = usage.Name;
        if (usage.Count != null)
            result["count"] = usage.Count;
        if (usage.Tasks != null)
            result["tasks"] = usage.Tasks.ToArray();
        if (usage.Board != null)
            result["board"] = usage.Board;
        if (usage.Column != null)
            result["column"] = usage.Column;
        return result;
    }

    public static bool IsRefused(IError error) => error.Extensions?.ContainsKey("status") == true;

    public static bool IsDomainError(Exception? exception) => exception is TaskerException;

    public static bool IsUnauthenticated(IError error) => error.Extensions?.TryGetValue("status", out var status) == true && status is 401;
}
