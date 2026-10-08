namespace Tasker.Core.Fields;

/// <summary>Тип значения поля.</summary>
public enum FieldType
{
    String,
    Int,
    Float,
    Bool,
    Date,

    /// <summary>Значение — одно из значений перечисления (<see cref="FieldDefinition.EnumId"/>).</summary>
    Enum
}

/// <summary>
/// Определение поля из каталога полей проекта: имя, тип значения, признак «несколько значений» и, для enum, перечисление.
/// Каталог — просто список определений; подключают поля к типам задач и хранят их значения следующие задачи фичи.
/// Тип, множественность и перечисление можно менять и после создания: значения задач при этом преобразуются
/// (<see cref="FieldService.Update"/>, <see cref="FieldConversion"/>).
/// </summary>
public record FieldDefinition
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }

    /// <summary>Имя поля. Уникально в проекте без учёта регистра.</summary>
    public required string Name { get; init; }

    public required FieldType Type { get; init; }

    /// <summary>У поля может быть несколько значений (список).</summary>
    public bool Multiple { get; init; }

    /// <summary>Перечисление проекта — только у поля типа <see cref="FieldType.Enum"/>, у остальных null.</summary>
    public Guid? EnumId { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}

/// <summary>Значение перечисления. Id не меняется при переименовании — по нему значение выбирают, переименовывают и переназначают.</summary>
public record FieldEnumValue(Guid Id, string Name);

/// <summary>
/// Перечисление проекта: имя и список значений в порядке отображения. Одно перечисление могут использовать несколько полей.
/// </summary>
public record FieldEnum
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }

    /// <summary>Имя перечисления. Уникально в проекте без учёта регистра.</summary>
    public required string Name { get; init; }

    /// <summary>Значения в порядке отображения; их названия уникальны в перечислении без учёта регистра.</summary>
    public required FieldEnumValue[] Values { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}
