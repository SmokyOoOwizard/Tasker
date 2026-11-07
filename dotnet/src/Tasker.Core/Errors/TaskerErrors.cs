using System.Runtime.CompilerServices;
using Tasker.Core.Locks;
using Tasker.Core.Tasks;
using Tasker.Core.Workspace;

namespace Tasker.Core;

public static class TaskerErrors
{
    private static readonly IReadOnlyList<ErrorUsage> NoUsages = [];

    private static TaskerError Build(string factory, string message, object?[] values, IReadOnlyList<ErrorUsage>? usages = null)
    {
        var definition = ErrorCatalog.ByFactory[factory];
        if (definition.Params.Count != values.Length)
            throw new ArgumentException($"{factory}: {definition.Params.Count} parameter(s) declared, {values.Length} given");
        var parameters = new Dictionary<string, object?>(values.Length);
        for (var i = 0; i < values.Length; i++)
            parameters[definition.Params[i]] = values[i] is ErrorField field ? field.Wire() : values[i];
        return new TaskerError(definition.Code, message, parameters, usages ?? NoUsages);
    }

    private static TaskerValidationException Validation(string message, object?[] values, [CallerMemberName] string factory = "") =>
        new(Build(factory, message, values));

    private static TaskerNotFoundException NotFound(string message, object?[] values, [CallerMemberName] string factory = "") =>
        new(Build(factory, message, values));

    private static TaskerForbiddenException Forbidden(string message, object?[] values, [CallerMemberName] string factory = "") =>
        new(Build(factory, message, values));

    private static TaskerUnauthenticatedException Unauthenticated(string message, object?[] values, [CallerMemberName] string factory = "") =>
        new(Build(factory, message, values));

    private static TaskerConflictException Conflict(string message, object?[] values, Usages? usages = null, [CallerMemberName] string factory = "") =>
        new(Build(factory, message, values, usages?.Items));

    [ErrorCode("validation.invalid")]
    public static TaskerValidationException Invalid(string message) => Validation(message, []);

    [ErrorCode("validation.required")]
    public static TaskerValidationException Required(ErrorField field, string? message = null) =>
        Validation(message ?? $"{field.English()} is required", [field]);

    [ErrorCode("validation.tooLong")]
    public static TaskerValidationException TooLong(ErrorField field, int max, string? message = null) =>
        Validation(message ?? $"{field.English()} must be at most {max} characters", [field, max]);

    [ErrorCode("validation.fieldNameChars")]
    public static TaskerValidationException FieldNameChars(string name) =>
        Validation($"Field name must consist of letters and digits only (no spaces or symbols such as _ - = ! < > :): '{name}'", [name]);

    [ErrorCode("validation.statusIcon")]
    public static TaskerValidationException StatusIcon(string[] allowed) =>
        Validation($"Status icon must be one of: {string.Join(", ", allowed)}", [allowed]);

    [ErrorCode("validation.color")]
    public static TaskerValidationException Color() => Validation("Color must be in #RRGGBB format", []);

    [ErrorCode("validation.username")]
    public static TaskerValidationException Username() =>
        Validation("Username must be 3-50 characters: latin letters, digits, '.', '_' or '-'", []);

    [ErrorCode("validation.email")]
    public static TaskerValidationException Email() => Validation("Email is not valid", []);

    [ErrorCode("validation.password")]
    public static TaskerValidationException Password(int min) => Validation($"Password must be at least {min} characters", [min]);

    [ErrorCode("board.noStatusSets")]
    public static TaskerValidationException BoardNoStatusSets() => Validation("Board must include at least one status set", []);

    [ErrorCode("board.noColumns")]
    public static TaskerValidationException BoardNoColumns() => Validation("Board must have at least one column", []);

    [ErrorCode("board.columnOverlap", "status", "otherColumn", "columnIndex")]
    public static TaskerValidationException BoardColumnOverlap(int columnIndex, string status, string otherColumn, string message) =>
        Validation(message, [status, otherColumn, columnIndex]);

    [ErrorCode("board.dropSetNotOnBoard", "statusSet", "columnIndex")]
    public static TaskerValidationException BoardDropSetNotOnBoard(int columnIndex, string statusSet, string message) =>
        Validation(message, [statusSet, columnIndex]);

    [ErrorCode("board.dropStatusNotInSet", "status", "statusSet", "columnIndex")]
    public static TaskerValidationException BoardDropStatusNotInSet(int columnIndex, string status, string statusSet, string message) =>
        Validation(message, [status, statusSet, columnIndex]);

    [ErrorCode("board.dropStatusNotInColumn", "status", "columnIndex")]
    public static TaskerValidationException BoardDropStatusNotInColumn(int columnIndex, string status, string message) =>
        Validation(message, [status, columnIndex]);

    [ErrorCode("board.taskTypeNotOnBoard")]
    public static TaskerValidationException BoardTaskTypeNotOnBoard(string taskType, string board) =>
        Validation($"Task type '{taskType}' is not on board '{board}'", [taskType, board]);

    [ErrorCode("board.noDropRule")]
    public static TaskerValidationException BoardNoDropRule(string column, string taskType) =>
        Validation($"Column '{column}' has no drop rule for tasks of type '{taskType}'", [column, taskType]);

    [ErrorCode("board.taskNotMatchColumn")]
    public static TaskerValidationException BoardTaskNotMatchColumn(string task, string column, string query) =>
        Validation(
            $"Task '{task}' does not match the query of column '{column}' ({query}): change the task first, a move changes only its status",
            [task, column, query]);

    [ErrorCode("board.columnQueryInvalid", "column", "query")]
    public static TaskerValidationException BoardColumnQueryInvalid(string column, string query, string reason) =>
        Validation($"The query of column '{column}' ({query}) is invalid: {reason}. Fix the column's query in the board settings", [column, query]);

    [ErrorCode("fieldValue.repeated")]
    public static TaskerValidationException FieldValueRepeated(string field, string value) =>
        Validation($"Field '{field}': value '{value}' is repeated", [field, value]);

    [ErrorCode("fieldValue.single")]
    public static TaskerValidationException FieldValueSingle(string field, int count) =>
        Validation($"Field '{field}' holds a single value, but {count} were given", [field, count]);

    [ErrorCode("fieldValue.empty")]
    public static TaskerValidationException FieldValueEmpty(string field) =>
        Validation($"Field '{field}': a value must not be empty (pass no values to clear the field)", [field]);

    [ErrorCode("fieldValue.tooLong")]
    public static TaskerValidationException FieldValueTooLong(string field, int max) =>
        Validation($"Field '{field}': a value must be at most {max} characters", [field, max]);

    [ErrorCode("fieldValue.invalid")]
    public static TaskerValidationException FieldValueInvalid(string field, string value, string type, string message) =>
        Validation(message, [field, value, type]);

    [ErrorCode("fieldValue.notInEnum")]
    public static TaskerValidationException FieldValueNotInEnum(string field, string value, string enumName, string[] values) =>
        Validation($"Field '{field}': '{value}' is not a value of enum '{enumName}' (values: {string.Join(", ", values)})", [field, value, enumName, values]);

    [ErrorCode("task.fieldNameTaken")]
    public static TaskerValidationException TaskFieldNameTaken(string field) =>
        Validation($"Field '{field}': the task already has a field with this name", [field]);

    [ErrorCode("link.self")]
    public static TaskerValidationException LinkSelf() => Validation("TargetId: a task cannot be linked to itself", []);

    [ErrorCode("link.cycle")]
    public static TaskerValidationException LinkCycle(string linkType, string[] tasks) =>
        Validation(
            $"Cycle: {string.Join(" → ", tasks)} (link type '{linkType}' does not allow cycles; remove the opposite link or allow cycles for the type)",
            [linkType, tasks]);

    [ErrorCode("field.enumNotAllowed")]
    public static TaskerValidationException FieldEnumNotAllowed() => Validation("EnumId: only a field of type enum has an enum", []);

    [ErrorCode("field.enumRequired")]
    public static TaskerValidationException FieldEnumRequired() => Validation("EnumId: a field of type enum must refer to an enum of the project", []);

    [ErrorCode("field.typeChangeNotAllowed")]
    public static TaskerValidationException FieldTypeChangeNotAllowed(string from, string to) =>
        Validation(
            $"Type: cannot change a field from {from} to {to} directly (it would lose values): change it to string first, then to the type you need",
            [from, to]);

    [ErrorCode("enum.noValues")]
    public static TaskerValidationException EnumNoValues() => Validation("Enum must contain at least one value", []);

    [ErrorCode("enum.valueRepeated")]
    public static TaskerValidationException EnumValueRepeated(string value) =>
        Validation($"Enum value '{value}' is repeated: values must be unique", [value]);

    [ErrorCode("statusSet.noStatuses")]
    public static TaskerValidationException StatusSetNoStatuses() => Validation("Status set must contain at least one status", []);

    [ErrorCode("linkType.hierarchicalNoCycles")]
    public static TaskerValidationException LinkTypeHierarchicalNoCycles() =>
        Validation("A hierarchical link type does not allow cycles: AllowCycles cannot be true", []);

    [ErrorCode("linkType.hierarchicalSameNames")]
    public static TaskerValidationException LinkTypeHierarchicalSameNames() =>
        Validation("A hierarchical link type needs two different side names (e.g. 'includes' / 'is part of'): the parent and the child must be told apart", []);

    [ErrorCode("series.prefixInvalid")]
    public static TaskerValidationException SeriesPrefixInvalid(int max) =>
        Validation($"Series prefix must be 1-{max} Latin letters or digits", [max]);

    [ErrorCode("user.currentPasswordIncorrect")]
    public static TaskerValidationException CurrentPasswordIncorrect() => Validation("Current password is incorrect", []);

    [ErrorCode("agent.expiresAtPast")]
    public static TaskerValidationException AgentExpiresAtPast() => Validation("ExpiresAt must be in the future", []);

    [ErrorCode("task.requiredFields")]
    public static TaskerRequiredFieldsException RequiredFields(string[] names) =>
        new(Build(nameof(RequiredFields), $"Required fields have no value: {string.Join(", ", names.Select(x => $"'{x}'"))}", [names]), names);

    [ErrorCode("entity.modified", "name", "entity")]
    public static TaskerConflictException Modified(ErrorSubject subject) =>
        new(Build(nameof(Modified), $"{subject} was changed by someone else; reload it and try again", [subject.Name, subject.Entity]), ConflictCode.Modified);

    [ErrorCode("entity.locked", "name", "holder", "entity", "expiresAt")]
    public static TaskerLockedException Locked(ErrorSubject subject, EditLock held) =>
        new(Build(nameof(Locked), $"{subject} is being edited by {held.Holder.Name}", [subject.Name, held.Holder.Name, subject.Entity, held.ExpiresAt]), held);

    [ErrorCode("format.unsupported")]
    public static UnsupportedFormatException UnsupportedFormat(string file, string message) =>
        new(Build(nameof(UnsupportedFormat), message, [file]));

    [ErrorCode("series.planChanged")]
    public static TaskerConflictException SeriesPlanChanged() =>
        new(Build(nameof(SeriesPlanChanged), "The conflicts changed since the plan was built or the plan was already applied; reload the plan and confirm it again", []), ConflictCode.Modified);

    [ErrorCode("status.inUse")]
    public static TaskerConflictException StatusInUse(string name, Usages usages) =>
        Conflict(usages.Message("deleted"), [name], usages);

    [ErrorCode("statusSet.inUse")]
    public static TaskerConflictException StatusSetInUse(string name, Usages usages) =>
        Conflict(usages.Message("deleted"), [name], usages);

    [ErrorCode("statusSet.statusInUse")]
    public static TaskerConflictException StatusSetStatusInUse(string name, string set, Usages usages) =>
        Conflict(usages.Message($"removed from status set '{set}'"), [name, set], usages);

    [ErrorCode("taskType.inUse")]
    public static TaskerConflictException TaskTypeInUse(string name, Usages usages) =>
        Conflict(usages.Message("deleted"), [name], usages);

    [ErrorCode("taskType.statusesNotInSet")]
    public static TaskerConflictException TaskTypeStatusesNotInSet(string name, string set, int count, string message) =>
        Conflict(message, [name, set, count]);

    [ErrorCode("taskType.fieldsHaveValues")]
    public static TaskerConflictException TaskTypeFieldsHaveValues(string name, string[] fields, int count, string message) =>
        Conflict(message, [name, fields, count]);

    [ErrorCode("linkType.inUse")]
    public static TaskerConflictException LinkTypeInUse(string name, Usages usages) =>
        Conflict(usages.Message("deleted"), [name], usages);

    [ErrorCode("linkType.nameTaken")]
    public static TaskerConflictException LinkTypeNameTaken(string name) =>
        Conflict($"Link type '{name}' already exists in the project", [name]);

    [ErrorCode("enum.inUse")]
    public static TaskerConflictException EnumInUse(string name, Usages usages) =>
        Conflict(usages.Message("deleted"), [name], usages);

    [ErrorCode("enum.nameTaken")]
    public static TaskerConflictException EnumNameTaken(string name) =>
        Conflict($"Enum '{name}' already exists in the project", [name]);

    [ErrorCode("enum.valuesInUse")]
    public static TaskerConflictException EnumValuesInUse(string name, string[] values, string message) =>
        Conflict(message, [name, values]);

    [ErrorCode("field.inUse")]
    public static TaskerConflictException FieldInUse(string name, Usages usages) =>
        Conflict(usages.Message("deleted"), [name], usages);

    [ErrorCode("field.conversionBlocked")]
    public static TaskerConflictException FieldConversionBlocked(string message) => Conflict(message, []);

    [ErrorCode("field.nameTaken")]
    public static TaskerConflictException FieldNameTaken(string name) =>
        Conflict($"Field '{name}' already exists in the project", [name]);

    [ErrorCode("series.prefixTaken")]
    public static TaskerConflictException SeriesPrefixTaken(string prefix) =>
        Conflict($"Series prefix '{prefix}' is already used in the project", [prefix]);

    [ErrorCode("task.numberInvalid")]
    public static TaskerConflictException TaskNumberInvalid(int number) =>
        Conflict($"Series number must be 1 or greater, got {number}", [number]);

    [ErrorCode("task.numberTaken")]
    public static TaskerConflictException TaskNumberTaken(int number) =>
        Conflict($"Number {number} is already taken in the series", [number]);

    [ErrorCode("user.usernameTaken")]
    public static TaskerConflictException UsernameTaken(string username) =>
        Conflict($"Username '{username}' is already taken", [username]);

    [ErrorCode("user.usernameOrEmailTaken")]
    public static TaskerConflictException UsernameOrEmailTaken() => Conflict("Username or email is already taken", []);

    [ErrorCode("user.emailTaken")]
    public static TaskerConflictException EmailTaken(string email) =>
        Conflict($"Email '{email}' is already taken", [email]);

    [ErrorCode("user.cannotDeleteSelf")]
    public static TaskerConflictException CannotDeleteSelf() => Conflict("You cannot delete your own account", []);

    [ErrorCode("user.lastAdmin")]
    public static TaskerConflictException LastAdmin(string username, string action) =>
        Conflict($"'{username}' is the last administrator and cannot {action}", [username, action]);

    [ErrorCode("project.lastPerson")]
    public static TaskerConflictException LastPerson() => Conflict("The last person cannot be removed from the project", []);

    [ErrorCode("project.agentOfAnotherUser")]
    public static TaskerForbiddenException AgentOfAnotherUser(string name) =>
        Forbidden($"Agent '{name}' belongs to another user", [name]);

    [ErrorCode("user.registrationClosed")]
    public static TaskerForbiddenException RegistrationClosed() =>
        Forbidden("Registration is closed: ask an administrator to create an account", []);

    [ErrorCode("user.adminRightsAdminOnly")]
    public static TaskerForbiddenException AdminRightsAdminOnly() => Forbidden("Only an administrator can change admin rights", []);

    [ErrorCode("user.adminOnly")]
    public static TaskerForbiddenException AdminOnly() => Forbidden("Only an administrator can do this", []);

    [ErrorCode("user.ownAccountOnly")]
    public static TaskerForbiddenException OwnAccountOnly() => Forbidden("You can change only your own account", []);

    [ErrorCode("user.signInRequired")]
    public static TaskerForbiddenException SignInRequired() => Forbidden("Sign in required", []);

    [ErrorCode("comment.notAuthor")]
    public static TaskerForbiddenException CommentNotAuthor() => Forbidden("You can change only your own comments", []);

    [ErrorCode("comment.parentNotFound")]
    public static TaskerValidationException CommentParentNotFound(Guid parentId) =>
        Validation($"ParentId: comment not found in this task: {parentId}", [parentId]);

    [ErrorCode("comment.parentDeleted")]
    public static TaskerValidationException CommentParentDeleted(Guid parentId) =>
        Validation($"ParentId: the comment is deleted, it cannot be replied to: {parentId}", [parentId]);

    [ErrorCode("user.personOnly")]
    public static TaskerForbiddenException PersonOnly() => Forbidden("Agents cannot do this: only a person can", []);

    [ErrorCode("auth.localOnly")]
    public static TaskerForbiddenException LocalOnly() => Forbidden("Only local requests are allowed", []);

    [ErrorCode("auth.required")]
    public static TaskerUnauthenticatedException AuthRequired(string message = "Sign in to read data") => Unauthenticated(message, []);

    [ErrorCode("auth.invalidCredentials")]
    public static TaskerUnauthenticatedException InvalidCredentials() => Unauthenticated("Invalid username/email or password", []);

    [ErrorCode("auth.sessionExpired")]
    public static TaskerUnauthenticatedException SessionExpired() =>
        Unauthenticated("Refresh token is invalid, expired or already used: sign in again", []);

    [ErrorCode("query.freeText")]
    public static TaskerValidationException QueryFreeText(string text) =>
        Validation($"'{text}' is not a condition: conditions start with a dot; to search titles use .Title.Contains(\"{text}\")", [text]);

    [ErrorCode("query.unexpected")]
    public static TaskerValidationException QueryUnexpected(string text) =>
        Validation($"Unexpected '{text}'", [text]);

    [ErrorCode("query.expectedCondition")]
    public static TaskerValidationException QueryExpectedCondition() =>
        Validation("Expected a condition, for example .Status == \"Todo\"", []);

    [ErrorCode("query.expectedProperty")]
    public static TaskerValidationException QueryExpectedProperty() =>
        Validation("Expected a property name after the dot", []);

    [ErrorCode("query.expectedOperator")]
    public static TaskerValidationException QueryExpectedOperator(string path) =>
        Validation($"Expected an operator or a function after {path}", [path]);

    [ErrorCode("query.expectedValue")]
    public static TaskerValidationException QueryExpectedValue() =>
        Validation("Expected a value", []);

    [ErrorCode("query.valueNeedsQuotes")]
    public static TaskerValidationException QueryValueNeedsQuotes(string text) =>
        Validation($"Text values are written in quotes: \"{text}\"", [text]);

    [ErrorCode("query.unclosedString")]
    public static TaskerValidationException QueryUnclosedString() =>
        Validation("The closing quote is missing", []);

    [ErrorCode("query.unclosedParen")]
    public static TaskerValidationException QueryUnclosedParen() =>
        Validation("The closing parenthesis is missing", []);

    [ErrorCode("query.unclosedBracket")]
    public static TaskerValidationException QueryUnclosedBracket() =>
        Validation("The closing bracket is missing", []);

    [ErrorCode("query.emptyArray")]
    public static TaskerValidationException QueryEmptyArray() =>
        Validation("The array is empty", []);

    [ErrorCode("query.expectedSort")]
    public static TaskerValidationException QueryExpectedSort() =>
        Validation("Expected sort after |; use || for OR", []);

    [ErrorCode("query.expectedSortKey")]
    public static TaskerValidationException QueryExpectedSortKey() =>
        Validation("Expected a sort key, for example .Updated or -.Updated", []);

    [ErrorCode("query.unknownProperty")]
    public static TaskerValidationException QueryUnknownProperty(string name) =>
        Validation($"Unknown property {name}: use .Status, .Type, .Title, .Series, .Field.Name, .Link.Name or .Parent", [name]);

    [ErrorCode("query.unknownStatus")]
    public static TaskerValidationException QueryUnknownStatus(string name) =>
        Validation($"No status '{name}' in the project", [name]);

    [ErrorCode("query.unknownType")]
    public static TaskerValidationException QueryUnknownType(string name) =>
        Validation($"No task type '{name}' in the project", [name]);

    [ErrorCode("query.unknownSeries")]
    public static TaskerValidationException QueryUnknownSeries(string name) =>
        Validation($"No series with the prefix '{name}' in the project", [name]);

    [ErrorCode("query.unknownField")]
    public static TaskerValidationException QueryUnknownField(string name) =>
        Validation($"No field '{name}' in the catalog or in the tasks", [name]);

    [ErrorCode("query.ambiguousField")]
    public static TaskerValidationException QueryAmbiguousField(string name) =>
        Validation($"Tasks have own fields '{name}' of several types", [name]);

    [ErrorCode("query.unknownLinkType")]
    public static TaskerValidationException QueryUnknownLinkType(string name) =>
        Validation($"No link type '{name}' in the project", [name]);

    [ErrorCode("query.unknownEnumValue")]
    public static TaskerValidationException QueryUnknownEnumValue(string field, string value) =>
        Validation($"Field '{field}' has no value '{value}'", [field, value]);

    [ErrorCode("query.badValue")]
    public static TaskerValidationException QueryBadValue(string field, string value) =>
        Validation($"'{value}' does not fit the type of field '{field}'", [field, value]);

    [ErrorCode("query.unknownFunction")]
    public static TaskerValidationException QueryUnknownFunction(string name, string target) =>
        Validation($"{target} has no function {name}()", [name, target]);

    [ErrorCode("query.functionArgument")]
    public static TaskerValidationException QueryFunctionArgument(string name) =>
        Validation($"Wrong argument of {name}(): Contains takes a value, ContainsAny and ContainsAll take an array, checks take nothing", [name]);

    [ErrorCode("query.operatorNotAllowed")]
    public static TaskerValidationException QueryOperatorNotAllowed(string op, string target) =>
        Validation($"{target} does not support {op}", [op, target]);

    [ErrorCode("query.arrayNotAllowed")]
    public static TaskerValidationException QueryArrayNotAllowed(string op) =>
        Validation($"{op} does not take an array", [op]);

    [ErrorCode("query.badDate")]
    public static TaskerValidationException QueryBadDate(string name, string value) =>
        Validation($"'{value}' is not a date for {name}: write \"2026-11-05\", \"2026-11-05T14:30\", \"today\", \"now\" or an offset like \"-7d\"", [name, value]);

    [ErrorCode("query.sortRepeated")]
    public static TaskerValidationException QuerySortRepeated(string name) =>
        Validation($"Sort key {name} is listed twice", [name]);

    [ErrorCode("query.notSortable")]
    public static TaskerValidationException QueryNotSortable(string name) =>
        Validation($"Cannot sort by {name}", [name]);

    [ErrorCode("query.tooComplex")]
    public static TaskerValidationException QueryTooComplex() =>
        Validation("The query is too long or too deeply nested", []);

    [ErrorCode("entity.notFound")]
    public static TaskerNotFoundException EntityNotFound(string message = "Not found") => NotFound(message, []);

    [ErrorCode("project.notFound")]
    public static TaskerNotFoundException ProjectNotFound(Guid id) => NotFound($"Project {id} not found", [id]);

    [ErrorCode("workspace.notInAddress")]
    public static TaskerNotFoundException WorkspaceNotInAddress(string message) => NotFound(message, []);

    [ErrorCode("workspace.notOpen")]
    public static TaskerNotFoundException WorkspaceNotOpen(string id) => NotFound($"Workspace '{id}' is not open", [id]);

    [ErrorCode("internal.unexpected")]
    public static TaskerInternalException Unexpected() => new(Build(nameof(Unexpected), "Unexpected server error", []));
}
