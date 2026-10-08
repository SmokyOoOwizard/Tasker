using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace Tasker.Cli.Completion;

/// <summary>
/// Директива <c>[suggest]</c> — то, что вызывают скрипты автодополнения оболочек (<c>tasker completion</c>):
/// <c>tasker "[suggest:ПОЗИЦИЯ]" "строка без имени программы"</c> печатает варианты, по одному в строке.
/// Своя, а не из System.CommandLine, чтобы дополнение было тихим и ограниченным: любая ошибка — пустой ответ без
/// вывода в stderr, лишнее (запасные ключи справки вроде <c>/?</c>) не предлагается, вариантов не больше <see cref="Completer.Limit"/>.
/// </summary>
internal sealed class CompleteDirective : Directive
{
    public CompleteDirective() : base("suggest") => Action = new Suggest(this);

    private sealed class Suggest(CompleteDirective directive) : SynchronousCommandLineAction
    {
        // Незавершённая строка команды — последний аргумент после директивы (оболочка передаёт её одним аргументом).
        public override int Invoke(ParseResult parseResult)
        {
            try
            {
                var text = parseResult.Tokens.LastOrDefault(x => x.Type != TokenType.Directive)?.Value ?? "";
                // Значение директивы: «ПОЗИЦИЯ» или «ПОЗИЦИЯ:pwsh» (строка набрана в PowerShell).
                var argument = parseResult.GetResult(directive)?.Values is [var value] ? value.Split(':') : [];
                var dialect = argument is [_, "pwsh"] ? ShellDialect.PowerShell : ShellDialect.Posix;
                var position = argument.Length > 0 && int.TryParse(argument[0], out var parsed)
                    ? Math.Clamp(parsed, 0, text.Length)
                    : text.Length;

                // Оболочка присылает строку до курсора; разбираем её по правилам оболочки (кавычки, обратная косая черта), курсор — в конце.
                var line = ShellWords.Normalize(text[..position], dialect);
                var typed = parseResult.RootCommandResult.Command.Parse(line);
                var hidden = (typed.CommandResult.Command as WorkspaceFreeCommand)?.HiddenNames ?? [];
                var all = typed.GetCompletions(line.Length)
                    .Select(x => x.InsertText ?? x.Label)
                    .Where(x => x.Length > 0 && !IsNoise(x) && !hidden.Contains(x))
                    .Distinct()
                    .ToArray();

                // Параметры предлагаются, когда слово начато с «-» (или предлагать больше нечего): иначе список значений тонет в --json, -w...
                var options = all.Where(x => x.StartsWith('-')).ToArray();
                var rest = all.Except(options).ToArray();
                var (preceding, word) = ShellWords.SplitLast(line);
                var dashed = word.StartsWith('-');
                var wanted = dashed || rest.Length == 0 ? all : rest;

                // Слово с «-» после параметра, принимающего несколько значений (--status A --al), парсер считает ещё одним его
                // значением и предлагает только значения: параметры команды собираем сами по строке до этого слова.
                if (dashed && !word.Contains('='))
                    wanted = wanted.Concat(OptionsFor(parseResult.RootCommandResult.Command, preceding, word)).Where(x => !hidden.Contains(x)).Distinct().ToArray();

                foreach (var label in wanted.OrderBy(x => x, NaturalOrder.Instance).Take(Completer.Limit))
                    parseResult.InvocationConfiguration.Output.WriteLine(label);
            }
            catch (Exception)
            {
                // Дополнение ничего не пишет в stderr и не падает: нет вариантов — оболочка предложит файлы.
            }

            return 0;
        }

        // Параметры команды из строки до слова и всех родительских (общие живут в корне), начинающиеся с word; уже набранные
        // предлагаются снова, только если их можно повторять (несколько значений).
        private static IEnumerable<string> OptionsFor(Command root, string preceding, string word)
        {
            var before = root.Parse(preceding);
            var used = before.CommandResult.Children.OfType<OptionResult>().Where(x => !x.Implicit).Select(x => x.Option).ToHashSet();
            for (Command? c = before.CommandResult.Command; c != null; c = c.Parents.OfType<Command>().FirstOrDefault())
                foreach (var option in c.Options)
                {
                    if (option.Hidden || used.Contains(option) && option.Arity.MaximumNumberOfValues <= 1 && !option.AllowMultipleArgumentsPerToken)
                        continue;
                    foreach (var name in option.Aliases.Prepend(option.Name))
                        if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !IsNoise(name))
                            yield return name;
                }
        }

        // Запасные ключи справки (/?, /h, -?) в Unix-оболочке не нужны и только засоряют список (а путь — «/…» — не ключ).
        private static bool IsNoise(string label) => label is "/?" or "/h" or "-?";
    }
}
