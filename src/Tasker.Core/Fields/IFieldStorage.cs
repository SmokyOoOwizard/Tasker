using Tasker.Core.Dto;

namespace Tasker.Core.Fields;

/// <summary>Хранилище каталога полей проекта. Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.</summary>
public interface IFieldStorage
{
    Task<FieldDefinition?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все поля проекта по имени — для проверок. Для списков — <see cref="GetRange"/>.</summary>
    Task<FieldDefinition[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<FieldDefinition>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    /// <summary>Сколько файлов полей проекта не удалось прочитать; нечитаемое поле выглядело бы несуществующим. В БД — 0.</summary>
    Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default);

    Task<string> Add(FieldDefinition field, CancellationToken ct = default);
    Task<string?> Update(FieldDefinition field, string expectedVersion, CancellationToken ct = default);
    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}

/// <summary>Хранилище перечислений проекта. Запись — с проверкой версии.</summary>
public interface IFieldEnumStorage
{
    Task<FieldEnum?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все перечисления проекта по имени — для проверок. Для списков — <see cref="GetRange"/>.</summary>
    Task<FieldEnum[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<FieldEnum>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    /// <summary>Сколько файлов перечислений проекта не удалось прочитать. В БД — 0.</summary>
    Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default);

    Task<string> Add(FieldEnum value, CancellationToken ct = default);

    /// <summary>Заменяет имя и список значений целиком.</summary>
    Task<string?> Update(FieldEnum value, string expectedVersion, CancellationToken ct = default);

    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
