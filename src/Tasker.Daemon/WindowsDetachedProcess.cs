using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>
/// Запуск демона в фоне на Windows: без окна и консоли родителя, в своей группе процессов (Ctrl+C и закрытие консоли, из которой
/// позвали <c>tasker mcp start</c>, его не задевают), без унаследованных дескрипторов (иначе канал вывода вызвавшей программы,
/// например <c>$x = tasker mcp start</c> в PowerShell, не закрылся бы, пока жив демон). <see cref="Process.Start()"/> не умеет ни того,
/// ни другого — отсюда прямой <c>CreateProcessW</c>. Всё, что не требует ОС, вынесено в <see cref="WindowsCommandLine"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDetachedProcess
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char[] environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Запускает программу и не ждёт её. Окружение — текущее с переопределениями <see cref="AppEnvironment"/>.</summary>
    public static Process Start(string file, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file);
        AppEnvironment.Apply(info);
        var environment = WindowsCommandLine.EnvironmentBlock(info.Environment).ToCharArray();
        var commandLine = (WindowsCommandLine.Build(file, arguments) + "\0").ToCharArray();
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };

        // Выйти из задания родителя (Планировщик задач и некоторые оболочки убивают всё задание), если оно это разрешает.
        var flags = CreateNoWindow | CreateNewProcessGroup | CreateUnicodeEnvironment;
        if (!CreateProcessW(file, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags | CreateBreakawayFromJob, environment, null, ref startup, out var created))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorAccessDenied || !CreateProcessW(file, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment, null, ref startup, out created))
                throw new Win32Exception(error == ErrorAccessDenied ? Marshal.GetLastWin32Error() : error, $"Cannot start {file}");
        }

        CloseHandle(created.hThread);
        CloseHandle(created.hProcess);
        return Process.GetProcessById(created.dwProcessId);
    }
}
