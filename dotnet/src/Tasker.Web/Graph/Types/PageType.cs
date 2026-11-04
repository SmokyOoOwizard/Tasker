using HotChocolate.Types;
using Tasker.Core.Dto;

namespace Tasker.Web.Graph.Types;

internal sealed class PageType<T> : ObjectType<ListDto<T>>
{
    protected override void Configure(IObjectTypeDescriptor<ListDto<T>> descriptor)
    {
        descriptor.Name($"{typeof(T).Name}Page");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TotalCount);
        descriptor.Field(x => x.Offset);
        descriptor.Field(x => x.Limit);
        descriptor.Field(x => x.Data);
    }
}
