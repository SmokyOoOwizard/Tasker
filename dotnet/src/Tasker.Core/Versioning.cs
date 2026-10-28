namespace Tasker.Core;

/// <summary>
/// Защита от одновременной правки (оптимистическая блокировка).
/// <para>
/// У каждой сущности есть <c>Version</c> — непрозрачная строка, которая меняется при каждой записи.
/// Изменение и удаление принимают версию, которую видел клиент; если с тех пор запись изменил кто-то
/// другой, версии не совпадут и операция отклоняется (<see cref="ConflictCode.Modified"/>, 409).
/// </para>
/// <para>
/// Сравнение и запись атомарны на уровне хранилища: в БД — <c>UPDATE … WHERE Version = @expected</c>,
/// в файлах — под блокировкой файла. Формат версии у хранилищ разный (счётчик в БД, хэш содержимого
/// в файлах), поэтому клиент не должен её разбирать — только передавать обратно.
/// </para>
/// </summary>
public static class Versioning
{
    /// <summary>Версия ещё не назначена — сущность только создаётся, хранилище вернёт настоящую.</summary>
    public const string New = "";

    /// <summary>Сущность уже изменена кем-то другим.</summary>
    public static TaskerConflictException Modified(string subject) =>
        new($"{subject} was changed by someone else; reload it and try again", ConflictCode.Modified);

    /// <summary>
    /// Версия от клиента обязательна и должна совпадать с текущей. Вызывается после того, как сущность
    /// найдена: для несуществующей сущности ответ — 404, а не ошибка версии.
    /// Это быстрая проверка до записи; окончательная — атомарно в хранилище.
    /// </summary>
    /// <returns>Ожидаемая версия — для передачи в хранилище.</returns>
    public static string Check(string current, string? expected, string subject)
    {
        if (string.IsNullOrWhiteSpace(expected))
            throw new TaskerValidationException("Version is required: pass the version of the entity you are changing");
        if (current != expected)
            throw Modified(subject);
        return expected;
    }
}
