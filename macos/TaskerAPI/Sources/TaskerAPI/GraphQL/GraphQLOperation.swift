import Foundation

public protocol GraphQLOperation: Sendable {
    associatedtype Data: Decodable & Hashable & Sendable
    static var operationName: String { get }
    static var document: String { get }
    static var selections: [GraphQLField] { get }
    var variables: [String: JSONValue] { get }
}

public protocol GraphQLQuery: GraphQLOperation {}

public protocol GraphQLSubscription: GraphQLOperation {}

public struct GraphQLField: Sendable, Hashable {
    public var name: String
    public var alias: String?
    public var arguments: [String: GraphQLArgument]
    public var selections: [GraphQLField]

    public init(_ name: String, alias: String? = nil, arguments: [String: GraphQLArgument] = [:], selections: [GraphQLField] = []) {
        self.name = name
        self.alias = alias
        self.arguments = arguments
        self.selections = selections
    }

    public var responseKey: String { alias ?? name }

    func storageKey(_ variables: [String: JSONValue]) -> String {
        var resolved: [String: JSONValue] = [:]
        for (key, argument) in arguments {
            if let value = argument.resolve(variables) {
                resolved[key] = value
            }
        }
        return resolved.isEmpty ? name : name + "(" + TaskerJSON.canonical(.object(resolved)) + ")"
    }
}

public indirect enum GraphQLArgument: Sendable, Hashable {
    case variable(String)
    case value(JSONValue)
    case list([GraphQLArgument])
    case object([String: GraphQLArgument])

    func resolve(_ variables: [String: JSONValue]) -> JSONValue? {
        switch self {
        case .variable(let name):
            return variables[name]
        case .value(let value):
            return value
        case .list(let items):
            return .array(items.map { $0.resolve(variables) ?? .null })
        case .object(let fields):
            return .object(fields.compactMapValues { $0.resolve(variables) })
        }
    }
}

public protocol GraphQLVariable: Sendable {
    var graphQLValue: JSONValue { get }
}

public protocol GraphQLEnum: GraphQLVariable, RawRepresentable, Codable, Hashable, Sendable where RawValue == String {}

extension GraphQLEnum {
    public var graphQLValue: JSONValue { .string(rawValue) }
}

extension String: GraphQLVariable {
    public var graphQLValue: JSONValue { .string(self) }
}

extension Int: GraphQLVariable {
    public var graphQLValue: JSONValue { .int(self) }
}

extension Double: GraphQLVariable {
    public var graphQLValue: JSONValue { .double(self) }
}

extension Bool: GraphQLVariable {
    public var graphQLValue: JSONValue { .bool(self) }
}

extension UUID: GraphQLVariable {
    public var graphQLValue: JSONValue { .string(uuidString.lowercased()) }
}

extension Date: GraphQLVariable {
    public var graphQLValue: JSONValue { .string(TaskerDate.format(self)) }
}

extension Array: GraphQLVariable where Element: GraphQLVariable {
    public var graphQLValue: JSONValue { .array(map(\.graphQLValue)) }
}

extension Optional: GraphQLVariable where Wrapped: GraphQLVariable {
    public var graphQLValue: JSONValue { self?.graphQLValue ?? .null }
}

struct GraphQLRequestBody: Encodable {
    let query: String
    let operationName: String
    let variables: [String: JSONValue]

    init<Operation: GraphQLOperation>(_ operation: Operation) {
        query = Operation.document
        operationName = Operation.operationName
        variables = operation.variables
    }
}

struct GraphQLResponseBody: Decodable {
    let data: JSONValue?
    let errors: [GraphQLError]?
}
