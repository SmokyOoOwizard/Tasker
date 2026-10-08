namespace Tasker.Core;

/// <summary>Действие запрещено текущему пользователю (например, создание пользователей не админом). В API — 403.</summary>
public class TaskerForbiddenException(string message) : Exception(message);
