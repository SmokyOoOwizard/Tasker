namespace Tasker.Core;

/// <summary>
/// Запуск и остановка хранилища: схема БД, первая сверка индекса файлов и слежение за папкой.
/// Сервер запускает его вместе с хостом, десктоп — при открытии рабочей области и останавливает при её закрытии.
/// </summary>
public interface IStorageLifecycle
{
    Task Start(CancellationToken ct);

    Task Stop(CancellationToken ct) => Task.CompletedTask;
}
