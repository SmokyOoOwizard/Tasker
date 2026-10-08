namespace Tasker.Core;

/// <summary>Неверные входные данные (пустое имя, ссылка на чужой или несуществующий статус и т.п.). В API — 400.</summary>
public class TaskerValidationException(string message) : Exception(message);
