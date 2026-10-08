using System.Text;

namespace Tasker.Cli.Completion;

/// <summary>
/// Файлы для <see cref="PowerShellProfile"/>: профиль PowerShell и скрипт автодополнения. Профиль читаем и пишем так, чтобы и Windows
/// PowerShell 5.1 (файл без BOM читает в ANSI), и PowerShell 7 (читает UTF-8) увидели то же самое: BOM сохраняем, а если в файле
/// появились не-ASCII знаки (папка пользователя с кириллицей), ставим его. Файл, который не читается как UTF-8 (ANSI-страница),
/// не трогаем: перекодировка испортила бы чужие настройки.
/// </summary>
internal static class PowerShellProfileFiles
{
    public const string BackupSuffix = ".tasker-backup";

    /// <summary>Установка или удаление блока в одном файле профиля; результат — что сделано и строка для вывода.</summary>
    public static (ProfileChange Change, string Report) Apply(string path, string? scriptPath, string newlineFallback)
    {
        var exists = File.Exists(path);
        var bytes = exists ? File.ReadAllBytes(path) : [];
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        string content;
        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            return (ProfileChange.Broken, $"  skipped:   {path} is not UTF-8 (probably the ANSI code page): add the block yourself, see 'tasker manual windows'");
        }

        var newline = PowerShellProfile.NewlineOf(content, newlineFallback);
        var (updated, change) = scriptPath == null
            ? PowerShellProfile.Uninstall(content, newline)
            : PowerShellProfile.Install(content, PowerShellProfile.Block(scriptPath, newline), newline);

        switch (change)
        {
            case ProfileChange.Broken:
                return (change, $"  skipped:   {path} has only one of the '{PowerShellProfile.Begin}' / '{PowerShellProfile.End}' lines: fix or delete it, then run this command again");
            case ProfileChange.Unchanged:
                return (change, $"  unchanged: {path} already has the tasker completion block");
            case ProfileChange.Absent:
                return (change, $"  unchanged: {path} has no tasker completion block");
        }

        var report = new StringBuilder();
        if (exists && !File.Exists(path + BackupSuffix))
        {
            File.Copy(path, path + BackupSuffix);
            report.AppendLine($"  backup:    {path}{BackupSuffix} (the file as it was before)");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var withBom = hasBom || updated.Any(c => c > 0x7F);
        var body = Encoding.UTF8.GetBytes(updated);
        File.WriteAllBytes(path, withBom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body);
        report.Append(change switch
        {
            ProfileChange.Added => $"  changed:   {path} (the tasker completion block is added at the end)",
            ProfileChange.Replaced => $"  changed:   {path} (the tasker completion block is updated)",
            _ => $"  changed:   {path} (the tasker completion block is removed)"
        });
        return (change, report.ToString());
    }

    /// <summary>
    /// Пишет скрипт автодополнения в файл (заменяя прежний). ASCII, но на всякий случай с BOM: Windows PowerShell 5.1 читает файл
    /// без BOM в ANSI.
    /// </summary>
    public static void WriteScript(string path, string script)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(script)]);
    }
}
