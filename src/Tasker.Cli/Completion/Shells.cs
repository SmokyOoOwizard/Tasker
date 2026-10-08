using System.Reflection;

namespace Tasker.Cli.Completion;

/// <summary>Оболочка, для которой есть скрипт автодополнения (встроен в программу, файл в <c>Completion/Shells</c>).</summary>
internal sealed record Shell(string Name, string ResourceName, string[]? Aliases = null)
{
    /// <summary>Скрипт разбирает набранную строку по правилам PowerShell (<see cref="ShellDialect.PowerShell"/>), а не POSIX-оболочки.</summary>
    public ShellDialect Dialect => Name == "pwsh" ? ShellDialect.PowerShell : ShellDialect.Posix;

    /// <summary>Скрипт: что печатает <c>tasker completion &lt;оболочка&gt;</c>.</summary>
    public string Script()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Shells/{ResourceName}")
            ?? throw new InvalidOperationException($"The completion script {ResourceName} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Поддерживаемые оболочки. Скрипты только вызывают директиву <c>[suggest]</c> (<see cref="CompleteDirective"/>) и вставляют
/// ответ с экранированием по правилам своей оболочки, знаний о командах в них нет — поэтому новая оболочка (fish) —
/// это новый скрипт и строка здесь (и ещё строка выбора файла настроек в scripts/install.sh). PowerShell (<c>pwsh</c>) — для Windows,
/// Linux и macOS; его блок в <c>$PROFILE</c> ставит <c>tasker completion pwsh --install</c> (<see cref="PowerShellProfile"/>).
/// </summary>
internal static class Shells
{
    public static IReadOnlyList<Shell> All { get; } =
    [
        new("zsh", "tasker.zsh"),
        new("bash", "tasker.bash"),
        new("pwsh", "tasker.ps1", ["powershell"])
    ];

    /// <summary>Оболочка по имени или псевдониму (<c>powershell</c> — то же, что <c>pwsh</c>); нет такой — null.</summary>
    public static Shell? Find(string name) =>
        All.FirstOrDefault(x => x.Name == name || x.Aliases?.Contains(name) == true);

    /// <summary>Все допустимые написания: имена и псевдонимы.</summary>
    public static string[] AcceptedNames => [.. All.SelectMany(x => (string[])[x.Name, .. x.Aliases ?? []])];

    public static string[] Names => [.. All.Select(x => x.Name)];
}
