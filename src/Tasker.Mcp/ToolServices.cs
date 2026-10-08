using Microsoft.Extensions.DependencyInjection;

namespace Tasker.Mcp;

/// <summary>
/// Провайдер, по которому SDK решает, какие параметры инструмента брать из DI, а какие — из аргументов агента.
/// <para>
/// Autofac считает «зарегистрированной» любую коллекцию (<c>Guid[]</c>, <c>IEnumerable&lt;T&gt;</c>…):
/// пустую он соберёт сам. Без этой обёртки параметр вроде <c>Guid[] statusIds</c> пропадал из схемы
/// и всегда приходил пустым. Коллекции в инструменты из DI мы не внедряем — они всегда от агента.
/// </para>
/// </summary>
internal sealed class ToolServices(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
{
    public object? GetService(Type serviceType) =>
        serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

    public bool IsService(Type serviceType) =>
        !IsCollection(serviceType) && inner.GetService<IServiceProviderIsService>()?.IsService(serviceType) == true;

    private static bool IsCollection(Type type) =>
        type.IsArray
        || (type.IsGenericType && type.GetGenericArguments().Length == 1
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string));
}
