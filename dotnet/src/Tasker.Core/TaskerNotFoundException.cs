namespace Tasker.Core;

/// <summary>
/// Сущность, нужная операции, исчезла посреди неё: проект (или его тип задач, набор статусов) удалили параллельно с созданием
/// или правкой задачи. Сервисы бросают её вместо голого <see cref="InvalidOperationException"/>, когда связанная запись «не может
/// не быть», но данные проекта на лету пропали. В API — 404, в MCP — <c>[not_found]</c>, в консоли — <c>Not found: …</c>.
/// </summary>
public class TaskerNotFoundException(string message) : Exception(message);
