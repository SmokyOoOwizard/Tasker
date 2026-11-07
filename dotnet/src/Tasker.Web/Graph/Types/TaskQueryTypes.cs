using HotChocolate.Types;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Query;

namespace Tasker.Web.Graph.Types;

internal sealed class TaskQueryAnalysisType : ObjectType<TaskQueryAnalysis>
{
    protected override void Configure(IObjectTypeDescriptor<TaskQueryAnalysis> descriptor)
    {
        descriptor.Name("TaskQueryAnalysis");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Valid);
        descriptor.Field(x => x.HasSort);
        descriptor.Field(x => x.Errors).Type<NonNullType<ListType<NonNullType<TaskQueryErrorType>>>>();
        descriptor.Field(x => x.Suggestions).Type<NonNullType<ListType<NonNullType<TaskQuerySuggestionType>>>>();
    }
}

internal sealed class TaskQueryErrorType : ObjectType<TaskQueryError>
{
    protected override void Configure(IObjectTypeDescriptor<TaskQueryError> descriptor)
    {
        descriptor.Name("TaskQueryError");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Code);
        descriptor.Field(x => x.Message);
        descriptor.Field(x => x.Params).Type<NonNullType<ListType<NonNullType<TaskQueryParamType>>>>();
        descriptor.Field(x => x.Start);
        descriptor.Field(x => x.End);
    }
}

internal sealed class TaskQueryParamType : ObjectType<TaskQueryParam>
{
    protected override void Configure(IObjectTypeDescriptor<TaskQueryParam> descriptor)
    {
        descriptor.Name("TaskQueryParam");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Value);
    }
}

internal sealed class TaskQuerySuggestionType : ObjectType<TaskQuerySuggestion>
{
    protected override void Configure(IObjectTypeDescriptor<TaskQuerySuggestion> descriptor)
    {
        descriptor.Name("TaskQuerySuggestion");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Kind);
        descriptor.Field(x => x.Label);
        descriptor.Field(x => x.Insert);
        descriptor.Field(x => x.Start);
        descriptor.Field(x => x.End);
        descriptor.Field(x => x.Caret);
        descriptor.Field(x => x.Detail);
    }
}
