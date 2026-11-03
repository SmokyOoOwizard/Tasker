using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Tasker.Cli.Interactive;

internal static class InteractiveMode
{
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromSeconds(10);

    public const string NotATerminal =
        "Interactive mode needs a terminal: run 'tasker -i' in a terminal, not with redirected input or output";

    public static async Task<int> Run(string? workspace, string? project, TextWriter error)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            error.WriteLine($"Error: {NotATerminal}");
            return 1;
        }

        Session? session = null;
        try
        {
            var settings = new WorkspaceSettings(workspace);
            var location = Session.Locate(settings);
            session = await Session.Open(settings, CancellationToken.None);
            var context = new Context(session, TextWriter.Null, TextWriter.Null, false, null, CancellationToken.None);
            var chosen = string.IsNullOrWhiteSpace(project)
                ? (await context.AllProjects()) is { Count: 1 } projects ? projects[0] : null
                : await context.FindProject(project);
            var app = await App.Create(session, location, chosen, RefreshInterval(), CancellationToken.None);
            try
            {
                await Drive(app);
                return 0;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                error.WriteLine($"Error: {e.Message}");
                return 1;
            }
        }
        catch (Exception e) when (GlobalOptions.IsCommandError(e))
        {
            error.WriteLine(GlobalOptions.ErrorText(e));
            return 1;
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }

    private static TimeSpan RefreshInterval() =>
        double.TryParse(Environment.GetEnvironmentVariable("TASKER_REFRESH_SECONDS")?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var seconds) && double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultRefresh;

    private static void Enter()
    {
        Console.TreatControlCAsInput = true;
        Console.Out.Write("\x1b[?1049h\x1b[2J");
        Console.Out.Flush();
    }

    private static void Leave()
    {
        Console.Out.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
        Console.TreatControlCAsInput = false;
    }

    private static async Task Drive(App app)
    {
        var output = Console.OpenStandardOutput();
        Enter();
        try
        {
            await EventLoop(app, output);
        }
        finally
        {
            Leave();
        }
    }

    private static void Draw(App app, Stream output)
    {
        var screen = new Screen(Console.WindowWidth, Console.WindowHeight);
        Ui.Draw(screen, app);
        var bytes = Encoding.UTF8.GetBytes(screen.Render());
        output.Write(bytes);
        output.Flush();
    }

    private static async Task EventLoop(App app, Stream output)
    {
        while (true)
        {
            Draw(app, output);
            Key? key;
            if (app.RefreshDue() is { } left)
            {
                key = WaitKey(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
                if (key == null)
                {
                    if (app.RefreshDue() is { } now && now == TimeSpan.Zero)
                    {
                        if (app.Result != null)
                        {
                            if (await app.RefreshArgv() is { } argv)
                                await Execute(app, argv);
                        }
                        else
                            await app.RefreshStep();
                    }
                    continue;
                }
            }
            else
                key = ReadKey();

            if (key is not { } pressed)
                continue;
            switch (await app.Handle(pressed))
            {
                case QuitEffect:
                    return;
                case RunEffect run:
                    await Execute(app, run.Argv);
                    break;
                case OpenEditorEffect editor:
                {
                    Leave();
                    var edited = EditInEditor(editor.Text);
                    Enter();
                    app.EditorDone(edited);
                    break;
                }
            }
        }
    }

    private static Key? WaitKey(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (Console.KeyAvailable)
                return ReadKey();
            Thread.Sleep(20);
        }
        return null;
    }

    private static Key? ReadKey()
    {
        var info = Console.ReadKey(intercept: true);
        return KeyOf(info);
    }

    internal static Key? KeyOf(ConsoleKeyInfo info)
    {
        var control = (info.Modifiers & ConsoleModifiers.Control) != 0;
        switch (info.Key)
        {
            case ConsoleKey.C when control:
                return Key.CtrlC;
            case ConsoleKey.F when control:
                return Key.CtrlF;
            case ConsoleKey.L when control:
                return Key.CtrlL;
            case ConsoleKey.Enter:
                return Key.Enter;
            case ConsoleKey.Escape:
                return Key.Esc;
            case ConsoleKey.UpArrow:
                return Key.Up;
            case ConsoleKey.DownArrow:
                return Key.Down;
            case ConsoleKey.LeftArrow:
                return Key.Left;
            case ConsoleKey.RightArrow:
                return Key.Right;
            case ConsoleKey.PageUp:
                return Key.PageUp;
            case ConsoleKey.PageDown:
                return Key.PageDown;
            case ConsoleKey.Home:
                return Key.Home;
            case ConsoleKey.End:
                return Key.End;
            case ConsoleKey.Backspace:
                return Key.Backspace;
            case ConsoleKey.Delete:
                return Key.Delete;
            case ConsoleKey.Tab:
                return Key.Tab;
        }
        if (info.KeyChar == '\x03')
            return Key.CtrlC;
        if (info.KeyChar == '\x06')
            return Key.CtrlF;
        if (info.KeyChar == '\x0c')
            return Key.CtrlL;
        return info.KeyChar != '\0' && !char.IsControl(info.KeyChar) ? Key.Of(info.KeyChar) : null;
    }

    private static async Task Execute(App app, List<string> argv)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.Run([.. argv], output, error, TextReader.Null, interactive: false);
        await app.SetResult(output.ToString(), error.ToString(), code);
    }

    public static string EditorCommand()
    {
        foreach (var name in new[] { "VISUAL", "EDITOR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value && value.Trim().Length > 0)
                return value;
        }
        return OperatingSystem.IsWindows() ? "notepad" : "vi";
    }

    private static string? EditInEditor(string text)
    {
        var file = Path.Combine(Path.GetTempPath(), $"tasker-{Environment.ProcessId}-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllText(file, text);
            var words = ShellWords(EditorCommand());
            if (words.Count == 0)
                return null;
            var start = new ProcessStartInfo(words[0]) { UseShellExecute = false };
            foreach (var word in words.Skip(1))
                start.ArgumentList.Add(word);
            start.ArgumentList.Add(file);
            using var process = Process.Start(start);
            if (process == null)
                return null;
            process.WaitForExit();
            return process.ExitCode == 0 ? File.ReadAllText(file).Replace("\r\n", "\n").TrimEnd() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static List<string> ShellWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is { } q)
            {
                if (c == q)
                    quote = null;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
                current.Append(c);
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }
}
