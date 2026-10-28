using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace Tasker.Desktop;

/// <summary>
/// Команды окна: меню «Файл» в строке меню macOS и горячие клавиши.
/// <para>
/// На macOS сочетания из меню срабатывают и тогда, когда фокус в WebView: WebView пропускает
/// непонятные ему сочетания в меню приложения. KeyBindings окна WebView до окна не доходят —
/// поэтому всё важное есть в меню. ⌘1…⌘9 — только KeyBindings (как в браузерах, в меню их нет).
/// </para>
/// </summary>
public sealed class WindowCommands(WindowManager manager)
{
    // ⌘ на macOS, Ctrl на Windows и Linux.
    private static readonly KeyModifiers Primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public void Attach(WorkspaceWindow window)
    {
        var menu = new NativeMenu
        {
            Items =
            {
                Item("Новая вкладка", new KeyGesture(Key.T, Primary), () => manager.NewTab(window)),
                Item("Новое окно", new KeyGesture(Key.N, Primary), manager.NewWindow),
                Item("Открыть папку…", new KeyGesture(Key.O, Primary), () => _ = manager.OpenFolder(window)),
                new NativeMenuItemSeparator(),
                Item("Следующая вкладка", new KeyGesture(Key.OemCloseBrackets, Primary | KeyModifiers.Shift), () => window.ActivateNext(1)),
                Item("Предыдущая вкладка", new KeyGesture(Key.OemOpenBrackets, Primary | KeyModifiers.Shift), () => window.ActivateNext(-1)),
                new NativeMenuItemSeparator(),
                Item("Закрыть вкладку", new KeyGesture(Key.W, Primary), () => CloseActiveTab(window)),
                Item("Закрыть окно", new KeyGesture(Key.W, Primary | KeyModifiers.Shift), window.Close)
            }
        };
        NativeMenu.SetMenu(window, new NativeMenu { Items = { new NativeMenuItem("Файл") { Menu = menu } } });

        // Без строки меню (Windows, Linux) — те же сочетания клавишами окна. На macOS их обрабатывает меню:
        // с KeyBindings команда сработала бы дважды.
        if (!OperatingSystem.IsMacOS())
        {
            Bind(window, Key.T, Primary, () => manager.NewTab(window));
            Bind(window, Key.N, Primary, manager.NewWindow);
            Bind(window, Key.O, Primary, () => _ = manager.OpenFolder(window));
            Bind(window, Key.W, Primary, () => CloseActiveTab(window));
        }
        Bind(window, Key.Tab, KeyModifiers.Control, () => window.ActivateNext(1));
        Bind(window, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, () => window.ActivateNext(-1));
        for (var i = 1; i <= 9; i++)
        {
            var number = i;
            // ⌘9 — последняя вкладка, как в браузерах.
            Bind(window, Key.D0 + i, Primary, () =>
            {
                var tabs = window.Tabs;
                if (tabs.Count > 0)
                    window.ActivateTab(number == 9 ? tabs[^1] : tabs[Math.Min(number, tabs.Count) - 1]);
            });
        }
    }

    private static void CloseActiveTab(WorkspaceWindow window)
    {
        if (window.ActiveTab is { } tab)
            window.CloseTab(tab);
    }

    private static NativeMenuItem Item(string header, KeyGesture gesture, Action action)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    private static void Bind(Window window, Key key, KeyModifiers modifiers, Action action) =>
        window.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(key, modifiers),
            Command = new RelayCommand(action)
        });

    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
