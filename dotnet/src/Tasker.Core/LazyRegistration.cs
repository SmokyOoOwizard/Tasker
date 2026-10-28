using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Core.Registration;

namespace Tasker.Core;

/// <summary>
/// Регистрация типа «по требованию». <c>RegisterType</c> на <c>Build</c> сразу готовит каждому типу фабрику (дерево выражений, компиляция,
/// JIT) — на десятках сервисов это десятки миллисекунд при каждом запуске консоли, даже если команде нужна пара сервисов.
/// Здесь регистрация создаётся при первом запросе сервиса; описание (<c>As</c>, время жизни) задаётся так же, как у <c>RegisterType</c>.
/// </summary>
public static class LazyRegistration
{
    /// <summary>
    /// Как <c>builder.RegisterType&lt;T&gt;()</c> с настройкой <paramref name="configure"/>, но фабрика строится, когда сервис впервые понадобился.
    /// </summary>
    public static void RegisterTypeLazily<T>(this ContainerBuilder builder,
        Action<IRegistrationBuilder<T, ConcreteReflectionActivatorData, SingleRegistrationStyle>> configure) where T : notnull
    {
        var registration = RegistrationBuilder.ForType<T>();
        configure(registration);
        builder.RegisterSource(new LazySource<T>(registration));
    }

    private sealed class LazySource<T>(IRegistrationBuilder<T, ConcreteReflectionActivatorData, SingleRegistrationStyle> builder) : IRegistrationSource
        where T : notnull
    {
        private readonly Service[] _services = builder.RegistrationData.Services.ToArray();
        private readonly object _gate = new();
        private IComponentRegistration? _registration;

        public bool IsAdapterForIndividualComponents => false;

        public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
        {
            if (!_services.Contains(service))
                return [];

            lock (_gate)
                return [_registration ??= RegistrationBuilder.CreateRegistration(builder)];
        }
    }
}
