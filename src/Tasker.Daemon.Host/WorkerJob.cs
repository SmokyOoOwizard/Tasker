using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tasker.Daemon.Host;

/// <summary>
/// Привязка рабочих процессов к супервизору: упал или убит супервизор — рабочие процессы не остаются жить без него и без блокировки
/// <c>daemon.lock</c> (держали бы порт). На Unix рабочий процесс выходит сам, когда закрывается канал (stdin); на Windows канал закрывается
/// тоже, но надёжнее объект «задание» (Job Object) с признаком «убить всех при закрытии»: срабатывает и когда супервизор снят через
/// <c>Process.Kill</c>. Закрытие консоли и выход пользователя задание не затрагивают — супервизор запущен без консоли.
/// </summary>
internal interface IWorkerJob : IDisposable
{
    void Assign(Process process);
}

internal static class WorkerJob
{
    public static IWorkerJob Create()
    {
        if (!OperatingSystem.IsWindows())
            return new NoJob();

        try
        {
            return new WindowsJob();
        }
        catch (Win32Exception)
        {
            // Задания нет (запрещено окружением): работаем без него, рабочие процессы выйдут по закрытию канала.
            return new NoJob();
        }
    }

    private sealed class NoJob : IWorkerJob
    {
        public void Assign(Process process)
        {
        }

        public void Dispose()
        {
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsJob : IWorkerJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimitInformationStruct
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformationStruct information, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _job;

    public WindowsJob()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var information = new ExtendedLimitInformationStruct { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(_job, ExtendedLimitInformation, ref information, Marshal.SizeOf<ExtendedLimitInformationStruct>()))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed");
        }
    }

    public void Assign(Process process)
    {
        // Не вышло (процесс уже в другом задании без вложенности, на старой Windows) — рабочий процесс всё равно выйдет по закрытию канала.
        try
        {
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }
}
