using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;

namespace Tasker.Desktop;

public partial class App(WindowManager? windows, IHostApplicationLifetime? hostLifetime) : Application
{
    // Для превьюера XAML в IDE.
    public App() : this(null, null)
    {
    }

    /// <summary>Что открыть при запуске (аргументы <c>--files</c>, <c>--sqlite</c>). Задаёт Program до старта UI.</summary>
    public StartupRequest Startup { get; set; } = StartupRequest.Empty;

    /// <summary>Запрос от следующего запуска приложения (см. <see cref="SingleInstance"/>): из любого потока.</summary>
    public void OnAnotherLaunch(StartupRequest request) =>
        Dispatcher.UIThread.Post(() => _ = windows?.Open(request));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Заголовок в строке меню macOS. Атрибут Name в App.axaml до Application.Name
        // не доходит, и Avalonia показывает «Avalonia Application».
        Name = "Tasker";
        CreateApplicationMenu();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ = windows?.Open(Startup);

            CreateTrayIcon(desktop);

            // Клик по значку в доке macOS, когда окно спрятано, — показываем окно.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                        ShowWindow();
                };
            }

            // SIGTERM / Ctrl+C останавливают хост — вместе с ним закрываем и UI.
            hostLifetime?.ApplicationStopping.Register(() => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowWindow();

        var newWindow = new NativeMenuItem("Новое окно");
        newWindow.Click += (_, _) => windows?.NewWindow();

        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => desktop.Shutdown();

        // Иконку и подсказку задаём сразу: на macOS трей падает на пустом ToolTipText.
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Tasker/Assets/tray.png"))),
            ToolTipText = "Tasker",
            Menu = new NativeMenu
            {
                Items = { open, newWindow, new NativeMenuItemSeparator(), exit }
            }
        };
        // Монохромная иконка: macOS сама перекрашивает её под светлую/тёмную строку меню.
        MacOSProperties.SetIsTemplateIcon(trayIcon, true);
        trayIcon.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
    }

    // Меню приложения в строке меню macOS. Без своего меню Avalonia подставляет
    // стандартное с «About Avalonia» — причём ещё до OnFrameworkInitializationCompleted
    // и больше его не перечитывает, поэтому задаём меню здесь, из Initialize.
    private void CreateApplicationMenu()
    {
        var open = new NativeMenuItem("Открыть окно Tasker");
        open.Click += (_, _) => ShowWindow();

        var exit = new NativeMenuItem("Выйти из Tasker")
        {
            Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta)
        };
        exit.Click += (_, _) =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };

        NativeMenu.SetMenu(this, new NativeMenu
        {
            Items = { open, new NativeMenuItemSeparator(), exit }
        });
    }

    private void ShowWindow() => windows?.ShowTopWindow();
}
