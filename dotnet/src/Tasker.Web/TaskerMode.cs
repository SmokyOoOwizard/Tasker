namespace Tasker.Web;

/// <summary>Как запущен Tasker. Задаётся хостом в коде, а не конфигом, — чтобы десктоп нельзя было случайно запустить сервером.</summary>
public enum TaskerMode
{
    /// <summary>
    /// Tasker.Server: веб-сервис для нескольких пользователей. Только хранилище в БД;
    /// вход по JWT (имя или почта + пароль), проекты видят их участники и админы.
    /// </summary>
    Server,

    /// <summary>
    /// Tasker.Desktop: локальное приложение. Хранилище — файлы или БД; пользователи — только имена, входа нет.
    /// </summary>
    Local
}

/// <summary>Режим, в котором запущен хост (доступен из DI).</summary>
public record TaskerHost(TaskerMode Mode);
