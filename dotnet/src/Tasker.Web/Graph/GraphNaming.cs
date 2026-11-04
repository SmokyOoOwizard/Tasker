using System.Text.Json;
using HotChocolate.Types.Descriptors;

namespace Tasker.Web.Graph;

internal sealed class GraphNaming : DefaultNamingConventions
{
    public override string GetEnumValueName(object value) => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()!);
}
