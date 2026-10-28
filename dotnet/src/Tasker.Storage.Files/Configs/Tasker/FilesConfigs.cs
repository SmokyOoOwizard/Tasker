using Tasker.Configs;

namespace Tasker.Storage.Files.Configs.Tasker;

/// <summary>Хранение файлами (только десктоп).</summary>
public class FilesConfigs : AConfigs
{
    /// <summary>
    /// Рабочая папка, внутри которой создаётся <c>.tasker</c>.
    /// <c>TASKER_FILES_CONFIGS_PATH</c> или <c>--files=/path/to/workspace</c>.
    /// </summary>
    [ConfigAlias("files")]
    public string? Path { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(Path);
}
