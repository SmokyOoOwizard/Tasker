import Foundation

public enum TaskerAPIError: Error, Sendable, Hashable {
    case badRequest(String)
    case unauthenticated(String)
    case forbidden(String)
    case notFound(String)
    case conflict(Conflict)
    case server(status: Int, message: String)
    case graphQL([GraphQLError])
    case timedOut
    case network(URLError.Code, String)
    case invalidResponse(String)

    public var message: String {
        switch self {
        case .badRequest(let message), .unauthenticated(let message), .forbidden(let message), .notFound(let message):
            return message
        case .conflict(let conflict):
            return conflict.message
        case .server(_, let message):
            return message
        case .graphQL(let errors):
            return errors.map(\.message).joined(separator: "\n")
        case .timedOut:
            return "The request timed out"
        case .network(_, let message), .invalidResponse(let message):
            return message
        }
    }

    public var isRetryable: Bool {
        switch self {
        case .timedOut, .network:
            return true
        case .server(let status, _):
            return status >= 500
        default:
            return false
        }
    }
}

public struct Conflict: Sendable, Hashable {
    public enum Kind: Sendable, Hashable {
        case inUse
        case modified
        case locked
        case unsupportedFormat
        case other(String?)

        init(code: String?) {
            switch code {
            case "in_use": self = .inUse
            case "modified": self = .modified
            case "locked": self = .locked
            case "unsupported_format": self = .unsupportedFormat
            default: self = .other(code)
            }
        }
    }

    public var kind: Kind
    public var message: String
    public var heldBy: LockHolder?

    public init(kind: Kind, message: String, heldBy: LockHolder? = nil) {
        self.kind = kind
        self.message = message
        self.heldBy = heldBy
    }
}

public struct LockHolder: Sendable, Hashable {
    public var name: String
    public var expiresAt: Date?

    public init(name: String, expiresAt: Date?) {
        self.name = name
        self.expiresAt = expiresAt
    }

    init?(_ value: JSONValue?) {
        guard let name = value?["name"]?.stringValue else { return nil }
        self.init(name: name, expiresAt: value?["expiresAt"]?.stringValue.flatMap(TaskerDate.parse))
    }
}

public struct GraphQLError: Sendable, Hashable, Decodable {
    public var message: String
    public var path: [JSONValue]?
    public var extensions: [String: JSONValue]?

    public init(message: String, path: [JSONValue]? = nil, extensions: [String: JSONValue]? = nil) {
        self.message = message
        self.path = path
        self.extensions = extensions
    }

    public var code: String? { extensions?["code"]?.stringValue }
    public var status: Int? { extensions?["status"]?.intValue }
}

extension TaskerAPIError {
    static func from(graphQL errors: [GraphQLError]) -> TaskerAPIError {
        guard let first = errors.first(where: { $0.code != nil }) ?? errors.first else {
            return .invalidResponse("Empty GraphQL error list")
        }
        switch first.code {
        case "UNAUTHENTICATED":
            return .unauthenticated(first.message)
        case "BAD_REQUEST":
            return .badRequest(first.message)
        case "NOT_FOUND":
            return .notFound(first.message)
        case "FORBIDDEN":
            return .forbidden(first.message)
        case "CONFLICT":
            return .conflict(Conflict(
                kind: Conflict.Kind(code: first.extensions?["conflict"]?.stringValue),
                message: first.message,
                heldBy: LockHolder(first.extensions?["heldBy"])))
        default:
            return .graphQL(errors)
        }
    }

    static func from(status: Int, body: Data) -> TaskerAPIError {
        let json = try? JSONDecoder().decode(JSONValue.self, from: body)
        if let errors = json?["errors"], let list = try? TaskerJSON.decode([GraphQLError].self, from: errors), !list.isEmpty {
            return from(graphQL: list)
        }
        let message = json?["error"]?.stringValue
            ?? String(decoding: body.prefix(512), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let text = message.isEmpty ? HTTPURLResponse.localizedString(forStatusCode: status) : message
        switch status {
        case 400: return .badRequest(text)
        case 401: return .unauthenticated(text)
        case 403: return .forbidden(text)
        case 404: return .notFound(text)
        case 409:
            return .conflict(Conflict(kind: Conflict.Kind(code: json?["code"]?.stringValue), message: text, heldBy: LockHolder(json?["heldBy"])))
        default: return .server(status: status, message: text)
        }
    }

    static func wrap(_ error: any Error) -> any Error {
        if let error = error as? TaskerAPIError { return error }
        if error is CancellationError { return error }
        if let error = error as? URLError {
            switch error.code {
            case .cancelled: return CancellationError()
            case .timedOut: return TaskerAPIError.timedOut
            default: return TaskerAPIError.network(error.code, error.localizedDescription)
            }
        }
        return error
    }
}
