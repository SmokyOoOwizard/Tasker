using HotChocolate.Types;
using Tasker.Core.Fields;
using Tasker.Core.Tasks;

namespace Tasker.Web.Graph.Types;

internal sealed class FieldDefinitionType : ObjectType<FieldDefinition>
{
    protected override void Configure(IObjectTypeDescriptor<FieldDefinition> descriptor)
    {
        descriptor.Name("FieldDefinition");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Type);
        descriptor.Field(x => x.Multiple);
        descriptor.Field(x => x.EnumId);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class FieldEnumType : ObjectType<FieldEnum>
{
    protected override void Configure(IObjectTypeDescriptor<FieldEnum> descriptor)
    {
        descriptor.Name("FieldEnum");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Values);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class FieldEnumValueType : ObjectType<FieldEnumValue>
{
    protected override void Configure(IObjectTypeDescriptor<FieldEnumValue> descriptor)
    {
        descriptor.Name("FieldEnumValue");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Name);
    }
}

internal sealed class TaskFieldType : ObjectType<TaskField>
{
    protected override void Configure(IObjectTypeDescriptor<TaskField> descriptor)
    {
        descriptor.Name("TaskField");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.FieldId);
        descriptor.Field(x => x.Values);
        descriptor.Field(x => x.Own);
    }
}

internal sealed class OwnFieldType : ObjectType<OwnField>
{
    protected override void Configure(IObjectTypeDescriptor<OwnField> descriptor)
    {
        descriptor.Name("OwnField");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Type);
        descriptor.Field(x => x.Required);
        descriptor.Field(x => x.Multiple);
        descriptor.Field(x => x.EnumId);
    }
}

internal sealed class TaskFieldViewType : ObjectType<TaskFieldView>
{
    protected override void Configure(IObjectTypeDescriptor<TaskFieldView> descriptor)
    {
        descriptor.Name("TaskFieldView");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.FieldId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Type);
        descriptor.Field(x => x.Multiple);
        descriptor.Field(x => x.EnumId);
        descriptor.Field(x => x.Required);
        descriptor.Field(x => x.Source);
        descriptor.Field(x => x.Values);
        descriptor.Field(x => x.Texts);
    }
}
