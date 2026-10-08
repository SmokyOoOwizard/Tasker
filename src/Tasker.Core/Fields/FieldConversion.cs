namespace Tasker.Core.Fields;

/// <summary>Что сделать у задачи с несколькими значениями, когда поле становится «с одним значением».</summary>
public enum SeveralValues
{
    /// <summary>Оставить первое значение, остальные убрать.</summary>
    KeepFirst,

    /// <summary>Убрать все значения.</summary>
    Clear
}

/// <summary>Соответствие значения при смене перечисления: старое значение (у enum — id или название, у остальных типов — сам текст) → значение нового перечисления (id или название).</summary>
public record FieldValueMapping(string From, string To);

/// <summary>
/// Явный выбор пользователя при правке определения поля, которая затрагивает значения задач. Нужен только если без него значения
/// нельзя сохранить: иначе игнорируется. Без нужного выбора — ошибка с числом задач.
/// </summary>
/// <param name="ClearUnconvertible">Значения, которые не получилось привести к новому типу или сопоставить значению нового перечисления, убрать у задач
/// (остальные значения задач преобразуются).</param>
/// <param name="Several">Что делать у задач, у которых несколько значений, когда поле становится «с одним значением».</param>
/// <param name="Mapping">Явное соответствие значений для нового перечисления; значения без соответствия сопоставляются по названию (без учёта регистра).
/// Только при новом типе enum.</param>
public record FieldChangeChoice(bool ClearUnconvertible = false, SeveralValues? Several = null, FieldValueMapping[]? Mapping = null);

/// <summary>
/// Преобразование значений задач при правке определения поля (тип, множественность, перечисление). Допустимые смены типа:
/// любой → string (значение становится текстом; у enum — названием значения), int → float, string → int/float/bool/date/enum
/// (значение должно разбираться; enum — по названию или явному соответствию), enum → другое перечисление (по названию или соответствию).
/// Остальные смены (например float → int) теряли бы данные: через string их можно сделать в два шага.
/// </summary>
internal sealed class FieldConversion
{
    private readonly FieldDefinition _old;
    private readonly FieldEnum? _oldEnum;
    private readonly FieldDefinition _new;
    private readonly FieldEnum? _newEnum;
    private readonly FieldChangeChoice? _choice;

    /// <summary>Ключ — каноническое прежнее значение (у enum — id значения), значение — id значения нового перечисления.</summary>
    private readonly Dictionary<string, Guid> _mapping = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Результат для значений одного поля одной задачи.</summary>
    /// <param name="Values">Новые значения (без повторов), с применённым выбором для «нескольких значений».</param>
    /// <param name="Unconvertible">Прежние значения (для показа), которые привести не удалось. В <see cref="Values"/> их нет.</param>
    /// <param name="Several">Значений несколько, а поле станет «с одним значением»; без выбора <see cref="FieldChangeChoice.Several"/> — это проблема.</param>
    public sealed record Result(IReadOnlyList<string> Values, IReadOnlyList<string> Unconvertible, bool Several);

    private FieldConversion(
        FieldDefinition old, FieldEnum? oldEnum, FieldDefinition updated, FieldEnum? newEnum, FieldChangeChoice? choice)
    {
        _old = old;
        _oldEnum = oldEnum;
        _new = updated;
        _newEnum = newEnum;
        _choice = choice;
    }

    /// <summary>Значения задач меняются: другой тип или перечисление, либо «несколько → одно».</summary>
    public bool ChangesValues =>
        _old.Type != _new.Type || _old.EnumId != _new.EnumId || (_old.Multiple && !_new.Multiple);

    /// <exception cref="TaskerValidationException">Смена типа недопустима, неверный выбор или соответствие.</exception>
    public static FieldConversion Create(
        FieldDefinition old, FieldEnum? oldEnum, FieldDefinition updated, FieldEnum? newEnum, FieldChangeChoice? choice)
    {
        if (choice?.Several is { } several && !Enum.IsDefined(several))
            throw new TaskerValidationException($"Choice.Several: unknown value '{several}'");
        EnsureAllowed(old.Type, updated.Type);

        var conversion = new FieldConversion(old, oldEnum, updated, newEnum, choice);
        if (choice?.Mapping is { Length: > 0 } mapping)
            conversion.ReadMapping(mapping);
        return conversion;
    }

    private static void EnsureAllowed(FieldType from, FieldType to)
    {
        var allowed = from == to || to == FieldType.String || (from == FieldType.Int && to == FieldType.Float) ||
                      (from == FieldType.String && to is FieldType.Int or FieldType.Float or FieldType.Bool or FieldType.Date or FieldType.Enum);
        if (!allowed)
            throw new TaskerValidationException(
                $"Type: cannot change a field from {from.ToString().ToLowerInvariant()} to {to.ToString().ToLowerInvariant()} directly " +
                "(it would lose values): change it to string first, then to the type you need");
    }

    private void ReadMapping(FieldValueMapping[] mapping)
    {
        if (_new.Type != FieldType.Enum)
            throw new TaskerValidationException("Choice.Mapping: only when the new type is enum");

        foreach (var item in mapping)
        {
            var from = item?.From?.Trim();
            if (string.IsNullOrEmpty(from))
                throw new TaskerValidationException("Choice.Mapping: 'from' must not be empty");

            var target = Find(_newEnum, item!.To?.Trim())?.Id
                ?? throw new TaskerValidationException($"Choice.Mapping: '{item.To}' is not a value of enum '{_newEnum?.Name}'");
            var key = _old.Type == FieldType.Enum
                ? (Find(_oldEnum, from) ?? throw new TaskerValidationException($"Choice.Mapping: '{from}' is not a value of enum '{_oldEnum?.Name}'"))
                    .Id.ToString("D")
                : from;
            if (!_mapping.TryAdd(key, target))
                throw new TaskerValidationException($"Choice.Mapping: '{from}' is mapped twice");
        }
    }

    private static FieldEnumValue? Find(FieldEnum? enumeration, string? reference)
    {
        if (enumeration == null || string.IsNullOrEmpty(reference))
            return null;
        if (Guid.TryParse(reference, out var id) && enumeration.Values.FirstOrDefault(x => x.Id == id) is { } byId)
            return byId;
        return enumeration.Values.FirstOrDefault(x => string.Equals(x.Name, reference, StringComparison.OrdinalIgnoreCase));
    }

    public Result Convert(IReadOnlyList<string> values)
    {
        var converted = new List<string>();
        var failed = new List<string>();
        foreach (var value in values)
        {
            var result = One(value);
            if (result == null)
                failed.Add(Shown(value));
            else if (!converted.Contains(result, StringComparer.Ordinal))
                converted.Add(result);
        }

        var several = !_new.Multiple && converted.Count > 1;
        if (several)
            converted = _choice?.Several == SeveralValues.KeepFirst ? [converted[0]] : [];
        return new Result(converted, failed, several);
    }

    /// <summary>Значение для показа: у enum — название, у остальных — само значение.</summary>
    private string Shown(string value) => FieldValues.Texts(_old.Type, _oldEnum, [value])[0];

    /// <returns>null — значение привести нельзя.</returns>
    private string? One(string value)
    {
        if (_old.Type == _new.Type && (_new.Type != FieldType.Enum || _old.EnumId == _new.EnumId))
            return value;

        if (_new.Type == FieldType.Enum)
        {
            if (_mapping.TryGetValue(value, out var mapped))
                return mapped.ToString("D");
            // Потерянный id прежнего значения enum сопоставить не с чем.
            if (_old.Type == FieldType.Enum && Find(_oldEnum, value) == null)
                return null;
            return Find(_newEnum, Shown(value))?.Id.ToString("D");
        }

        var text = Shown(value);
        if (_new.Type == FieldType.String)
            return text.Length <= FieldValues.MaxStringLength ? text : null;
        return FieldValues.TryParse(_new.Type, text);
    }
}
