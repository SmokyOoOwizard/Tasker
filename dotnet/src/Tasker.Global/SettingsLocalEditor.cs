using Tasker.Core.Locks;

namespace Tasker.Global;

/// <summary>
/// Человек за десктопом или консолью: имя — из настроек (<see cref="GlobalSettings.UserName"/>), без него — имя пользователя ОС.
/// Читается при каждом обращении: имя меняют командой <c>tasker whoami</c> при работающем десктопе.
/// Файл настроек сломан — берём имя ОС, чтобы сбой настроек не останавливал запись.
/// </summary>
/// <param name="key">Один ключ — один держатель: интерфейс и консоль различаются, чтобы блокировка одного останавливала другого.</param>
/// <param name="suffix">Что дописать к имени, чтобы было видно, откуда блокировка: «Ivan (console)».</param>
public sealed class SettingsLocalEditor(SettingsStore settings, string key, string suffix = "") : ILocalEditor
{
    public string Key => key;

    public string Name
    {
        get
        {
            string? configured;
            try
            {
                configured = settings.Load().UserName;
            }
            catch (SettingsException)
            {
                configured = null;
            }

            return (string.IsNullOrWhiteSpace(configured) ? OsUser.Name : configured) + suffix;
        }
    }
}
