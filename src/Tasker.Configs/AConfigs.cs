namespace Tasker.Configs;

/// <summary>
/// Базовый класс группы настроек.
/// <para>
/// Класс-наследник кладётся в папку <c>&lt;Проект&gt;/Configs/Tasker/</c> с пространством имён
/// <c>Tasker.&lt;Проект&gt;.Configs.Tasker</c> — по нему <see cref="ConfigsParser"/> находит класс и строит ключи.
/// </para>
/// <para>
/// Значения берутся из переменных окружения (<c>TASKER_DB_CONFIGS_SQLITE_FILE</c>) и аргументов командной
/// строки (<c>--sqlite=tasker.db</c> по алиасу или <c>--TASKER_DB_CONFIGS_SQLITE_FILE=...</c>); аргументы
/// важнее переменных окружения. Не задано нигде — остаётся значение по умолчанию из класса.
/// </para>
/// </summary>
public abstract class AConfigs;

/// <summary>Короткое имя для аргумента командной строки: <c>--alias=value</c>. Одно имя может задавать несколько настроек.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class ConfigAliasAttribute(string name) : Attribute
{
    public readonly string Name = name;
}

/// <summary>Своё имя свойства в ключе вместо имени, выведенного из названия свойства.</summary>
[AttributeUsage(AttributeTargets.Property)]
public class CustomEnvNameAttribute(string name) : Attribute
{
    public readonly string Name = name;
}
