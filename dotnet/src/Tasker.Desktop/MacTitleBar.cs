using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Serilog;

namespace Tasker.Desktop;

/// <summary>
/// macOS: высокая строка заголовка, как у браузеров. Avalonia 12 не поднимает системные кнопки окна
/// («светофор») под <see cref="Window.ExtendClientAreaTitleBarHeightHint"/> — они остаются прижаты к верху
/// полосы вкладок. Пустая панель инструментов (NSToolbar) в стиле unified compact делает заголовок выше,
/// и система сама ставит кнопки по его центру.
/// </summary>
internal static class MacTitleBar
{
    // NSWindowToolbarStyleUnifiedCompact.
    private const long UnifiedCompact = 4;

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle)
            return;

        try
        {
            var toolbar = Send(Send(objc_getClass("NSToolbar"), Sel("alloc")), Sel("init"));
            SendPtr(handle.Handle, Sel("setToolbar:"), toolbar);
            SendLong(handle.Handle, Sel("setToolbarStyle:"), UnifiedCompact);
            // Toolbar держит окно; наша ссылка из alloc больше не нужна.
            Send(toolbar, Sel("release"));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Cannot set up the macOS title bar");
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendLong(IntPtr receiver, IntPtr selector, long argument);
}
