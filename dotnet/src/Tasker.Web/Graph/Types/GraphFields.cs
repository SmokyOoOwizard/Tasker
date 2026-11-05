using HotChocolate.Resolvers;
using HotChocolate.Types;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;

namespace Tasker.Web.Graph.Types;

internal static class GraphFields
{
    public static IObjectFieldDescriptor Read(this IObjectTypeDescriptor descriptor, string name) =>
        descriptor.Field(name).Use(GraphScope.Exclusive);

    public static IObjectFieldDescriptor Read<T>(this IObjectTypeDescriptor<T> descriptor, string name) =>
        descriptor.Field(name).Use(GraphScope.Exclusive);

    public static IObjectFieldDescriptor Paged(this IObjectFieldDescriptor field) =>
        field
            .Argument("offset", x => x.Type<IntType>())
            .Argument("limit", x => x.Type<IntType>());

    public static IObjectFieldDescriptor Described(this IObjectFieldDescriptor field) =>
        field.Argument("descriptionLength", x => x.Type<IntType>());

    public static IObjectFieldDescriptor Id(this IObjectFieldDescriptor field, string name = "id") =>
        field.Argument(name, x => x.Type<NonNullType<UuidType>>());

    public static IObjectFieldDescriptor TaskQuery(this IObjectFieldDescriptor field) =>
        field
            .Argument("field", x => x.Type<ListType<NonNullType<StringType>>>())
            .Argument("sort", x => x.Type<StringType>())
            .Paged()
            .Described();

    public static IObjectFieldDescriptor TaskFilters(this IObjectFieldDescriptor field) =>
        field
            .Argument("statusIds", x => x.Type<ListType<NonNullType<UuidType>>>())
            .Argument("typeIds", x => x.Type<ListType<NonNullType<UuidType>>>())
            .Argument("seriesIds", x => x.Type<ListType<NonNullType<UuidType>>>())
            .Argument("search", x => x.Type<StringType>())
            .TaskQuery();

    public static IObjectFieldDescriptor Returns<TType>(this IObjectFieldDescriptor field) where TType : class, IOutputType =>
        field.Type<NonNullType<TType>>();

    public static Page Page(this IResolverContext context) =>
        Tasker.Core.Dto.Page.Of(context.ArgumentValue<int?>("offset"), context.ArgumentValue<int?>("limit"));

    public static int DescriptionLength(this IResolverContext context) =>
        DescriptionPreview.Check(context.ArgumentValue<int?>("descriptionLength"));

    public static Guid Id(this IResolverContext context, string name = "id") => context.ArgumentValue<Guid>(name);

    public static TaskFilter? TaskFilter(this IResolverContext context) =>
        Tasker.Core.Tasks.TaskFilter.Of(
            context.ArgumentValue<Guid[]?>("typeIds"),
            context.ArgumentValue<Guid[]?>("statusIds"),
            context.ArgumentValue<Guid[]?>("seriesIds"),
            context.ArgumentValue<string?>("search"));

    public static string[]? FieldFilters(this IResolverContext context) => context.ArgumentValue<string[]?>("field");

    public static string? Sort(this IResolverContext context) => context.ArgumentValue<string?>("sort");

    public static TaskerMode Mode(this IResolverContext context) => context.Service<TaskerHost>().Mode;

    public static void RequireServer(this IResolverContext context, string field)
    {
        if (context.Mode() != TaskerMode.Server)
            throw new TaskerNotFoundException($"{field} is available only on the server");
    }

    public static void RequireWorkspace(this IResolverContext context, string field)
    {
        if (context.Mode() == TaskerMode.Server)
            throw new TaskerNotFoundException($"{field} is available only in a desktop workspace");
    }

    public static ListDto<T> All<T>(this Page page, IReadOnlyCollection<T> items) => page.Apply(items);
}
