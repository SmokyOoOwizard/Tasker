using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Tasker.Desktop.Tabs;

/// <summary>Содержимое пустой вкладки: кнопка «Выбрать папку» и ошибка последней попытки открыть.</summary>
public partial class StartPage : UserControl
{
    // Для превьюера XAML в IDE.
    public StartPage() : this(() => Task.CompletedTask)
    {
    }

    public StartPage(Func<Task> pickFolder)
    {
        InitializeComponent();
        PickButton.Click += async (_, _) => await pickFolder();
    }

    public void SetBusy(bool busy) => PickButton.IsEnabled = !busy;

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
