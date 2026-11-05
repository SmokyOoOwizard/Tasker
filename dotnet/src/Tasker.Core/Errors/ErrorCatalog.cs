using System.Collections.Frozen;
using System.Reflection;

namespace Tasker.Core;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class ErrorCodeAttribute(string code, params string[] wireParams) : Attribute
{
    public string Code { get; } = code;

    public string[] WireParams { get; } = wireParams;
}

public sealed record ErrorDefinition(string Code, int Status, IReadOnlyList<string> Params, bool HasUsages, MethodInfo Factory);

public static class ErrorCatalog
{
    private static readonly string[] NonWireParams = ["message", "usages"];

    public static readonly IReadOnlyList<ErrorDefinition> All = typeof(TaskerErrors)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(x => x.GetCustomAttribute<ErrorCodeAttribute>() != null)
        .Select(Define)
        .OrderBy(x => x.Code, StringComparer.Ordinal)
        .ToArray();

    internal static readonly FrozenDictionary<string, ErrorDefinition> ByFactory =
        All.ToFrozenDictionary(x => x.Factory.Name);

    public static IReadOnlyList<string> Fields { get; } = Enum.GetValues<ErrorField>().Select(x => x.Wire()).ToArray();

    private static ErrorDefinition Define(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var attribute = method.GetCustomAttribute<ErrorCodeAttribute>()!;
        return new ErrorDefinition(
            attribute.Code,
            StatusOf(method.ReturnType),
            attribute.WireParams.Length > 0 ? attribute.WireParams : parameters.Select(x => x.Name!).Where(x => !NonWireParams.Contains(x)).ToArray(),
            parameters.Any(x => x.Name == "usages"),
            method);
    }

    private static int StatusOf(Type type)
    {
        if (type == typeof(TaskerUnauthenticatedException))
            return 401;
        if (type == typeof(TaskerInternalException))
            return 500;
        if (type.IsAssignableTo(typeof(TaskerValidationException)))
            return 400;
        if (type.IsAssignableTo(typeof(TaskerForbiddenException)))
            return 403;
        if (type.IsAssignableTo(typeof(TaskerNotFoundException)))
            return 404;
        if (type.IsAssignableTo(typeof(TaskerConflictException)))
            return 409;
        throw new InvalidOperationException($"{type.Name} is not an error type");
    }
}
