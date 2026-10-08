using System.Text.RegularExpressions;
using Tasker.Core.Workspace;
using YamlDotNet.Serialization;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Основа всех файлов сущностей в .tasker: первое поле — версия формата. Благодаря ей по файлу видно, каким Tasker
/// (каким форматом) он записан, и когда формат изменится, старые файлы можно прочитать и перенести.
/// </summary>
internal abstract class FileModel
{
    /// <summary>
    /// Версия формата файла (<see cref="FormatVersions"/>). Пишется всегда (<c>formatVersion: 1</c>) и первой строкой;
    /// 0 при чтении — в файле поля нет, он записан до введения версии.
    /// </summary>
    [YamlMember(Order = -1000)]
    public int FormatVersion { get; set; }
}

/// <summary>
/// Версия формата файлов .tasker — число, а не версия программы: она растёт, только когда меняется формат файлов,
/// а не с каждым выпуском. Это отдельная версия от версии сущности (<c>Version</c>, хэш файла).
/// <list type="bullet">
/// <item><b>0</b> — файлы до введения версии (поля нет): те же поля, что в 1.</item>
/// <item><b>1</b> — то же, плюс <c>formatVersion: 1</c> первой строкой.</item>
/// <item><b>2</b> — файлы задач называются по заголовку (<see cref="EntityFileNames"/>); содержимое файлов то же.
/// Старый Tasker такие имена не узнаёт, поэтому версия поднята: он увидит файлы как «более нового формата», а не потеряет задачи молча.</item>
/// <item><b>3</b> — каталог полей и перечислений проекта (папки <c>fields/</c> и <c>enums/</c>); содержимое прежних файлов то же.
/// Старый Tasker этих папок не знает и молча проигнорировал бы их (а с ними — поля, которые следующие версии привяжут к типам и задачам),
/// поэтому версия поднята: файлы, записанные новым Tasker, он покажет как «более нового формата» и попросит обновиться.</item>
/// <item><b>4</b> — у типа задачи список полей (<c>fields</c> в <c>task-types/</c>), у задачи — значения полей и собственные поля
/// (<c>fields</c> в <c>tasks/</c>); прежние файлы остаются как есть (полей у них нет). Старый Tasker пропустил бы эти списки как неизвестные
/// и при первой же записи стёр бы их, поэтому версия поднята: файлы нового формата он покажет как «более нового формата».</item>
/// <item><b>5</b> — файлы остальных сущностей проекта (серии, типы задач, статусы, наборы статусов, доски, типы связей, поля,
/// перечисления) называются, как и задачи, по названию и короткому id (<see cref="EntityFileNames"/>); содержимое файлов то же.
/// Старый Tasker таких имён в этих папках не узнаёт и молча проигнорировал бы сущности (статусы, типы, серии
/// пропали бы из списков, а задачи ссылались бы на несуществующее), поэтому версия поднята: <c>tasker migrate</c> записывает её и в <c>project.yaml</c>, который старый Tasker читает всегда, и тот
/// покажет проект как «более нового формата».</item>
/// <item><b>6</b> — у колонок досок появились условия по полям каталога (<c>fieldFilters</c> в <c>boards/</c>); у прежних колонок их нет, файлы
/// остаются как есть. Старый Tasker пропустил бы этот список как неизвестный и при первой же правке доски стёр бы условия (колонка стала бы
/// показывать лишние задачи), поэтому версия поднята: такие файлы он покажет как «более нового формата».</item>
/// <item><b>7</b> — у типов связей появился признак <c>allowCycles</c> (допускает ли тип циклы, <c>link-types/</c>); в прежних файлах его нет,
/// и при чтении он выводится по умолчанию («Blocks» циклы запрещает, остальные допускают). Старый Tasker пропустил бы признак как неизвестный и при первой же
/// правке типа стёр бы его (цикл блокировок снова стал бы возможен), поэтому версия поднята: такие файлы он покажет как «более нового формата».</item>
/// <item><b>8</b> — у статусов и типов задач появилось необязательное описание (<c>description</c> в <c>statuses/</c> и <c>task-types/</c>); в прежних файлах
/// его нет, и описание пустое. Старый Tasker пропустил бы ключ как неизвестный и при первой же правке статуса или типа стёр бы его, поэтому версия поднята:
/// такие файлы он покажет как «более нового формата».</item>
/// <item><b>9</b> — текущий: у типов связей появился признак <c>hierarchical</c> (иерархический тип «Parent/Child»: эпик и его задачи, <c>link-types/</c>); в прежних файлах его нет,
/// и тип обычный. Старый Tasker пропустил бы признак как неизвестный и при первой же правке типа стёр бы его (иерархия пропала бы из списка задач),
/// поэтому версия поднята: такие файлы он покажет как «более нового формата».</item>
/// </list>
/// <para>
/// Правила. Tasker читает все версии до <see cref="Current"/> (шаги <see cref="Steps"/> приводят текст к текущему формату в памяти,
/// файл на диске при чтении не меняется), пишет всегда <see cref="Current"/>. Файл более новой версии не читает и не пишет:
/// <see cref="UnsupportedFormatException"/> (в списках — проблема файла «обновите Tasker»). Переписать все старые файлы на диске
/// можно командой <c>tasker migrate</c> (<see cref="FileMigration"/>).
/// </para>
/// <para>Как менять формат: поднять <see cref="Current"/>, добавить шаг в <see cref="Steps"/> (старый текст → новый, с выставленной версией) и тест на него.</para>
/// </summary>
internal static partial class FormatVersions
{
    public const int Current = 9;

    /// <summary>
    /// Шаги миграции: <c>To</c> — версия, в которую шаг приводит файл; <c>Apply</c> — преобразование текста YAML (и выставление версии).
    /// Применяются по порядку ко всем шагам с <c>To</c> больше версии файла.
    /// </summary>
    private static readonly (int To, Func<string, string> Apply)[] Steps =
    [
        // 0 → 1: поля те же, добавляется версия.
        (1, text => SetVersion(text, 1)),
        // 1 → 2: содержимое то же (меняется имя файла задачи — это делает tasker migrate, а не текстовый шаг).
        (2, text => SetVersion(text, 2)),
        // 2 → 3: содержимое то же (появились папки fields/ и enums/; в старых файлах ничего не меняется).
        (3, text => SetVersion(text, 3)),
        // 3 → 4: содержимое то же (у типов и задач появились списки полей; в старых файлах их просто нет).
        (4, text => SetVersion(text, 4)),
        // 4 → 5: содержимое то же (меняются имена файлов остальных сущностей — это делает tasker migrate, а не текстовый шаг).
        (5, text => SetVersion(text, 5)),
        // 5 → 6: содержимое то же (у колонок досок появился необязательный список fieldFilters; в старых файлах его просто нет).
        (6, text => SetVersion(text, 6)),
        // 6 → 7: содержимое то же (у типов связей появился необязательный признак allowCycles; в старых файлах его просто нет).
        (7, text => SetVersion(text, 7)),
        // 7 → 8: содержимое то же (у статусов и типов задач появилось необязательное описание description; в старых файлах его просто нет).
        (8, text => SetVersion(text, 8)),
        // 8 → 9: содержимое то же (у типов связей появился необязательный признак hierarchical; в старых файлах его просто нет).
        (9, text => SetVersion(text, 9))
    ];

    // \r?: у файлов с окончаниями строк Windows (autocrlf) перед концом строки стоит \r, а $ в .NET срабатывает только перед \n.
    [GeneratedRegex(@"^formatVersion:[ \t]*(?<value>[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    /// <summary>Версия формата из текста файла: 0 — поля нет. Поле ищется только на верхнем уровне (строки блоков с отступом не считаются).</summary>
    /// <exception cref="UnsupportedFormatException">Значение не число.</exception>
    public static int Of(string text, string path)
    {
        if (VersionLine().Match(text) is not { Success: true } match)
            return 0;

        return int.TryParse(match.Groups["value"].Value, out var version) && version >= 0
            ? version
            : throw new UnsupportedFormatException($"{Path.GetFileName(path)}: formatVersion '{match.Groups["value"].Value}' is not a number");
    }

    /// <summary>Приводит текст файла к текущему формату в памяти. Файл на диске не меняется.</summary>
    /// <exception cref="UnsupportedFormatException">Файл более нового формата или версия не читается.</exception>
    public static string Upgrade(string text, string path)
    {
        var version = Of(text, path);
        if (version > Current)
            throw Newer(version, path);

        foreach (var (to, apply) in Steps)
        {
            if (to > version)
                text = apply(text);
        }
        return text;
    }

    public static UnsupportedFormatException Newer(int version, string path) =>
        new($"{Path.GetFileName(path)}: format version {version} is newer than this Tasker supports ({Current}): update Tasker");

    /// <summary>Выставляет <c>formatVersion</c> первой строкой: заменяет имеющуюся или добавляет. Остальной текст не трогает.</summary>
    public static string SetVersion(string text, int version)
    {
        var line = $"formatVersion: {version}";
        // Заменяя строку, сохраняем её окончание (\r у файлов с окончаниями строк Windows): шагов может быть несколько подряд.
        return VersionLine().IsMatch(text)
            ? VersionLine().Replace(text, match => line + (match.Value.EndsWith('\r') ? "\r" : ""), 1)
            : line + (text.Contains("\r\n") ? "\r\n" : "\n") + text;
    }
}
