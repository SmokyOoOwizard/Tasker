using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Tasker.Cli;

/// <summary>Страница справочника: тема на одном языке. <see cref="Markdown"/> — весь файл, как он лежит в ресурсе.</summary>
/// <param name="Id">Имя темы: имя файла без номера порядка и расширения (<c>01-first-project.md</c> → <c>first-project</c>).</param>
/// <param name="Title">Заголовок — строка <c># …</c> в начале файла.</param>
/// <param name="Description">Короткое описание — первый абзац после заголовка.</param>
internal sealed record ManualPage(string Language, string Id, string Title, string Description, string Markdown)
{
    /// <summary>Страница в виде для терминала: без разметки, с подчёркнутыми заголовками и блоками кода с отступом.</summary>
    public string RenderText()
    {
        var text = new StringBuilder();
        var inCode = false;
        foreach (var line in Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
                text.Append(line.Length == 0 ? "" : "    " + line).Append('\n');
            else if (line.StartsWith("## ", StringComparison.Ordinal))
                text.Append(text.Length == 0 || text.ToString().EndsWith("\n\n", StringComparison.Ordinal) ? "" : "\n").Append(line[3..]).Append('\n').Append('-', line.Length - 3).Append('\n');
            else if (line.StartsWith("# ", StringComparison.Ordinal))
                text.Append(line[2..]).Append('\n').Append('=', line.Length - 2).Append('\n');
            else
                text.Append(line).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Содержимое блоков <c>```bash</c>: справочник считает их командами, которые можно выполнить (тест разбирает каждую по дереву
    /// команд). Пример вывода оформляют блоком <c>```text</c> — его не проверяют.
    /// </summary>
    public IEnumerable<string> ShellBlocks()
    {
        StringBuilder? block = null;
        foreach (var line in Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (block != null)
                {
                    yield return block.ToString();
                    block = null;
                }
                else if (line.TrimEnd() == "```bash")
                    block = new StringBuilder();
            }
            else
                block?.Append(line).Append('\n');
        }
    }
}

/// <summary>Выбранный язык справочника.</summary>
/// <param name="Language">Язык, на котором показывать.</param>
/// <param name="Found">Есть ли страницы на запрошенном языке (нет — это откат на язык по умолчанию).</param>
internal sealed record ManualLanguage(string Language, bool Found);

/// <summary>
/// Страницы справочника <c>tasker manual</c>. Лежат в сборке ресурсами <c>manual/&lt;язык&gt;/&lt;тема&gt;.md</c> (исходники — <c>Manual/&lt;язык&gt;/</c>);
/// код ничего не знает о конкретных языках, кроме <see cref="DefaultLanguage"/>: какие языки есть, определяется по найденным страницам.
/// Чтобы добавить язык, достаточно положить страницы в новую папку. Темы без перевода показываются на языке по умолчанию.
/// </summary>
internal sealed partial class ManualCatalog
{
    /// <summary>Язык, на который откатываются недостающие страницы и неизвестный язык.</summary>
    public const string DefaultLanguage = "ru";

    private const string ResourcePrefix = "manual/";

    public static ManualCatalog Embedded { get; } = FromAssembly(typeof(ManualCatalog).Assembly);

    // Язык → страницы в порядке оглавления (порядок имён файлов).
    private readonly Dictionary<string, List<ManualPage>> _pages = [];

    /// <param name="files">Файлы страниц: язык, имя файла без каталога (<c>01-first-project.md</c>), содержимое.</param>
    /// <exception cref="InvalidDataException">Страница без заголовка или описания, две страницы с одним именем в одном языке.</exception>
    public ManualCatalog(IEnumerable<(string Language, string FileName, string Markdown)> files)
    {
        foreach (var (language, fileName, markdown) in files.OrderBy(x => x.Language, StringComparer.Ordinal).ThenBy(x => x.FileName, StringComparer.Ordinal))
        {
            var page = Parse(language.ToLowerInvariant(), fileName, markdown);
            if (!_pages.TryGetValue(page.Language, out var list))
                _pages[page.Language] = list = [];
            if (list.Any(x => x.Id == page.Id))
                throw new InvalidDataException($"Manual page '{page.Id}' is duplicated in language '{page.Language}'");
            list.Add(page);
        }
    }

    public static ManualCatalog FromAssembly(Assembly assembly)
    {
        var files = new List<(string, string, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // Разделитель в имени ресурса зависит от системы сборки: приводим к «/».
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !normalized.EndsWith(".md", StringComparison.Ordinal))
                continue;

            var path = normalized[ResourcePrefix.Length..].Split('/');
            if (path.Length != 2)
                continue;

            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);
            files.Add((path[0], path[1], reader.ReadToEnd()));
        }

        return new ManualCatalog(files);
    }

    /// <summary>Языки, на которых есть хотя бы одна страница.</summary>
    public IReadOnlyList<string> Languages => [.. _pages.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Язык, на котором показывать: запрошенный, если на нём есть страницы (<c>en-US</c> → <c>en</c>), иначе язык по умолчанию.
    /// Не указан язык — язык по умолчанию, это не откат.
    /// </summary>
    public ManualLanguage Resolve(string? requested)
    {
        var language = requested?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(language))
            return new ManualLanguage(DefaultLanguage, true);
        if (_pages.ContainsKey(language))
            return new ManualLanguage(language, true);
        var dash = language.IndexOf('-');
        return dash > 0 && _pages.ContainsKey(language[..dash])
            ? new ManualLanguage(language[..dash], true)
            : new ManualLanguage(DefaultLanguage, false);
    }

    /// <summary>
    /// Оглавление: все темы (набор и порядок задаёт язык по умолчанию, темы только на других языках идут следом), у каждой — страница
    /// на выбранном языке или, если её нет, на языке по умолчанию.
    /// </summary>
    public IReadOnlyList<ManualPage> Topics(string? requested)
    {
        var language = Resolve(requested).Language;
        var all = _pages.Values.SelectMany(x => x).ToList();
        var ids = (_pages.GetValueOrDefault(DefaultLanguage) ?? []).Select(x => x.Id).Concat(all.Select(x => x.Id)).Distinct();
        return [.. ids.Select(id => Page(language, id) ?? Page(DefaultLanguage, id) ?? all.First(x => x.Id == id))];
    }

    private ManualPage? Page(string language, string id) => _pages.GetValueOrDefault(language)?.FirstOrDefault(x => x.Id == id);

    /// <summary>
    /// Темы по запросу: точное имя или заголовок — одна тема; иначе темы, в имени, заголовке или описании которых есть все слова запроса
    /// (регистр не важен), а если таких нет — темы, где слова встречаются в тексте. Пусто — ничего не найдено.
    /// </summary>
    public IReadOnlyList<ManualPage> Find(string? requested, string query)
    {
        var topics = Topics(requested);
        var text = query.Trim();
        var exact = topics.Where(x => x.Id.Equals(text, StringComparison.OrdinalIgnoreCase) || x.Title.Equals(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0)
            return exact;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];

        bool All(string haystack) => words.All(x => haystack.Contains(x, StringComparison.CurrentCultureIgnoreCase));
        var byHeading = topics.Where(x => All($"{x.Id} {x.Title} {x.Description}")).ToArray();
        return byHeading.Length > 0 ? byHeading : topics.Where(x => All(x.Markdown)).ToArray();
    }

    private static ManualPage Parse(string language, string fileName, string markdown)
    {
        var id = NumberPrefix().Replace(Path.GetFileNameWithoutExtension(fileName), "");
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var first = Array.FindIndex(lines, x => x.Trim().Length > 0);
        if (first < 0 || !lines[first].StartsWith("# ", StringComparison.Ordinal))
            throw new InvalidDataException($"Manual page {language}/{fileName} must start with a title line '# …'");

        var description = lines.Skip(first + 1).SkipWhile(x => x.Trim().Length == 0).TakeWhile(x => x.Trim().Length > 0).ToArray();
        if (description.Length == 0 || description[0].StartsWith('#') || description[0].StartsWith("```", StringComparison.Ordinal))
            throw new InvalidDataException($"Manual page {language}/{fileName} must have a short description paragraph after the title");

        return new ManualPage(language, id, lines[first][2..].Trim(), string.Join(' ', description.Select(x => x.Trim())), markdown);
    }

    [GeneratedRegex(@"^\d+-")]
    private static partial Regex NumberPrefix();
}
