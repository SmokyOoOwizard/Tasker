import Foundation

public enum TaskerAPIError: Error, Sendable, Hashable {
    case badRequest(Refusal)
    case unauthenticated(Refusal)
    case forbidden(Refusal)
    case notFound(Refusal)
    case conflict(Conflict)
    case server(status: Int, refusal: Refusal)
    case graphQL([GraphQLError])
    case timedOut
    case network(URLError.Code, String)
    case invalidResponse(String)

    public var refusal: Refusal? {
        switch self {
        case .badRequest(let refusal), .unauthenticated(let refusal), .forbidden(let refusal), .notFound(let refusal):
            return refusal
        case .conflict(let conflict):
            return conflict.refusal
        case .server(_, let refusal):
            return refusal
        default:
            return nil
        }
    }

    public var message: String {
        switch self {
        case .graphQL(let errors):
            return errors.map(\.message).joined(separator: "\n")
        case .timedOut:
            return "The request timed out"
        case .network(_, let message), .invalidResponse(let message):
            return message
        default:
            return refusal?.message ?? ""
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

public struct RefusalUsage: Sendable, Hashable {
    public var kind: String
    public var name: String?
    public var count: Int?
    public var tasks: [String]?
    public var board: String?
    public var column: String?

    public init(kind: String, name: String? = nil, count: Int? = nil, tasks: [String]? = nil, board: String? = nil, column: String? = nil) {
        self.kind = kind
        self.name = name
        self.count = count
        self.tasks = tasks
        self.board = board
        self.column = column
    }

    init?(_ value: JSONValue) {
        guard let kind = value["kind"]?.stringValue else { return nil }
        self.init(
            kind: kind,
            name: value["name"]?.stringValue,
            count: value["count"]?.intValue,
            tasks: value["tasks"]?.arrayValue?.compactMap(\.stringValue),
            board: value["board"]?.stringValue,
            column: value["column"]?.stringValue)
    }
}

public struct Refusal: Sendable, Hashable {
    public var code: String
    public var message: String
    public var params: [String: JSONValue]
    public var usages: [RefusalUsage]

    public init(code: String = "", message: String, params: [String: JSONValue] = [:], usages: [RefusalUsage] = []) {
        self.code = code
        self.message = message
        self.params = params
        self.usages = usages
    }

    init(message: String, code: String?, params: JSONValue?, usages: JSONValue?) {
        self.init(
            code: code ?? "",
            message: message,
            params: params?.objectValue ?? [:],
            usages: usages?.arrayValue?.compactMap(RefusalUsage.init) ?? [])
    }

    public var columnIndex: Int? { params["columnIndex"]?.intValue }
}

public struct Conflict: Sendable, Hashable {
    public enum Kind: Sendable, Hashable {
        case inUse
        case modified
        case locked
        case unsupportedFormat
        case other(String)

        init(refusal: Refusal) {
            let code = refusal.code
            switch code {
            case "entity.modified", "series.planChanged": self = .modified
            case "entity.locked": self = .locked
            case "format.unsupported": self = .unsupportedFormat
            default:
                if !refusal.usages.isEmpty || code.hasSuffix(".inUse") || code.hasSuffix("InUse") {
                    self = .inUse
                } else {
                    self = .other(code)
                }
            }
        }
    }

    public var kind: Kind
    public var refusal: Refusal

    public init(kind: Kind, refusal: Refusal) {
        self.kind = kind
        self.refusal = refusal
    }

    public init(refusal: Refusal) {
        self.init(kind: Kind(refusal: refusal), refusal: refusal)
    }

    public var message: String { refusal.message }
    public var code: String { refusal.code }
    public var params: [String: JSONValue] { refusal.params }
    public var usages: [RefusalUsage] { refusal.usages }

    public var heldBy: LockHolder? {
        guard kind == .locked, let name = refusal.params["holder"]?.stringValue else { return nil }
        return LockHolder(name: name, expiresAt: refusal.params["expiresAt"]?.stringValue.flatMap(TaskerDate.parse))
    }
}

public struct LockHolder: Sendable, Hashable {
    public var name: String
    public var expiresAt: Date?

    public init(name: String, expiresAt: Date?) {
        self.name = name
        self.expiresAt = expiresAt
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
    static func make(status: Int, refusal: Refusal) -> TaskerAPIError {
        switch status {
        case 400: return .badRequest(refusal)
        case 401: return .unauthenticated(refusal)
        case 403: return .forbidden(refusal)
        case 404: return .notFound(refusal)
        case 409: return .conflict(Conflict(refusal: refusal))
        default: return .server(status: status, refusal: refusal)
        }
    }

    static func from(graphQL errors: [GraphQLError]) -> TaskerAPIError {
        guard let first = errors.first(where: { $0.code != nil }) ?? errors.first else {
            return .invalidResponse("Empty GraphQL error list")
        }
        guard let code = first.code, let status = first.status else {
            return .graphQL(errors)
        }
        let refusal = Refusal(message: first.message, code: code, params: first.extensions?["params"], usages: first.extensions?["usages"])
        return make(status: status, refusal: refusal)
    }

    static func from(status: Int, body: Data) -> TaskerAPIError {
        let json = try? JSONDecoder().decode(JSONValue.self, from: body)
        if let errors = json?["errors"], let list = try? TaskerJSON.decode([GraphQLError].self, from: errors), !list.isEmpty {
            return from(graphQL: list)
        }
        let message = json?["error"]?.stringValue
            ?? String(decoding: body.prefix(512), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let text = message.isEmpty ? HTTPURLResponse.localizedString(forStatusCode: status) : message
        let refusal = Refusal(message: text, code: json?["code"]?.stringValue, params: json?["params"], usages: json?["usages"])
        return make(status: status, refusal: refusal)
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
