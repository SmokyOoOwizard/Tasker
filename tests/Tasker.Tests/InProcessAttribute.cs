using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tasker.Tests;

/// <summary>
/// Тест (или весь класс), который гоняет консоль внутри процесса тестов — через <see cref="Tasker.Cli.CliApp.Run"/>
/// (<see cref="TestWorkspace.Run"/>, <see cref="TestWorkspace.Invoke"/>), а не отдельным процессом <c>tasker</c>. Такой тест
/// проверяет только .NET-сборку и бесполезен при прогоне против чужого бинарника (<c>TASKER_BIN</c>): его отфильтровывают
/// трейтом <c>Binary=in-process</c> — <c>dotnet test --filter "Binary!=in-process"</c>. Класс помечается целиком, когда он весь
/// о консоли и в нём нет тестов с отдельным процессом; иначе помечаются отдельные методы.
/// </summary>
[TraitDiscoverer("Tasker.Tests.InProcessDiscoverer", "Tasker.Tests")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InProcessAttribute : Attribute, ITraitAttribute
{
    public const string Name = "Binary";
    public const string Value = "in-process";
}

public sealed class InProcessDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute) =>
        [new KeyValuePair<string, string>(InProcessAttribute.Name, InProcessAttribute.Value)];
}
