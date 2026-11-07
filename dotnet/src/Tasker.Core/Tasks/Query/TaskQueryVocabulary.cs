using Tasker.Core.Fields;

namespace Tasker.Core.Tasks.Query;

public sealed record QueryNamedItem(Guid Id, string Name);

public sealed record QueryField(string Name, FieldType Type, bool Multiple, Guid? FieldId, FieldEnum? Enumeration);

public sealed record QueryLinkType(Guid Id, string Name, string Outward, string Inward, bool Hierarchical);

public sealed record TaskQueryVocabulary(
    QueryNamedItem[] Statuses,
    QueryNamedItem[] Types,
    QueryNamedItem[] Series,
    QueryField[] Fields,
    QueryLinkType[] LinkTypes,
    IReadOnlyDictionary<Guid, Guid[]> TypesOfField)
{
    public IReadOnlyDictionary<string, OwnFieldChoice> OwnFields { get; init; } = new Dictionary<string, OwnFieldChoice>();
}

public sealed record OwnFieldChoice(QueryField? Field, bool Ambiguous);
