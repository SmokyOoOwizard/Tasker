namespace Tasker.Configs.Sources;

/// <summary>Источник значений: заполняет свойства уже созданных групп настроек.</summary>
public interface IConfigSource
{
    void Apply(IReadOnlyList<AConfigs> configs);
}
