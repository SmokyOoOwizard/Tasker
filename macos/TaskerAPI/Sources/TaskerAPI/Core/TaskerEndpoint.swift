import Foundation

public struct TaskerEndpoint: Sendable {
    public var baseURL: URL
    public var authorization: any TaskerAuthorization
    public var requestTimeout: TimeInterval

    public init(baseURL: URL, authorization: any TaskerAuthorization = NoAuthorization(), requestTimeout: TimeInterval = 30) {
        self.baseURL = baseURL
        self.authorization = authorization
        self.requestTimeout = requestTimeout
    }

    public static func local(port: Int, workspace: String, requestTimeout: TimeInterval = 30) -> TaskerEndpoint {
        var components = URLComponents()
        components.scheme = "http"
        components.host = "127.0.0.1"
        components.port = port
        components.path = "/w/" + workspace
        return TaskerEndpoint(baseURL: components.url!, requestTimeout: requestTimeout)
    }

    public static func server(_ url: URL, session: ServerSession, requestTimeout: TimeInterval = 30) -> TaskerEndpoint {
        TaskerEndpoint(baseURL: url, authorization: session, requestTimeout: requestTimeout)
    }

    var graphQLURL: URL { baseURL.appending(path: "api/graphql") }
}

public protocol TaskerAuthorization: Sendable {
    func accessToken() async throws -> String?
    func refresh(rejected token: String) async throws -> Bool
}

public struct NoAuthorization: TaskerAuthorization {
    public init() {}
    public func accessToken() async throws -> String? { nil }
    public func refresh(rejected token: String) async throws -> Bool { false }
}

public actor ServerSession: TaskerAuthorization {
    public struct Tokens: Sendable, Hashable {
        public var accessToken: String
        public var accessTokenExpiresAt: Date?
        public var refreshToken: String
        public var refreshTokenExpiresAt: Date?

        public init(accessToken: String, accessTokenExpiresAt: Date?, refreshToken: String, refreshTokenExpiresAt: Date?) {
            self.accessToken = accessToken
            self.accessTokenExpiresAt = accessTokenExpiresAt
            self.refreshToken = refreshToken
            self.refreshTokenExpiresAt = refreshTokenExpiresAt
        }
    }

    public typealias Refresher = @Sendable (_ refreshToken: String) async throws -> Tokens

    private var tokens: Tokens?
    private var refreshing: Task<Tokens, any Error>?
    private let refresher: Refresher
    private let onChange: @Sendable (Tokens?) -> Void
    private let leeway: TimeInterval

    public init(tokens: Tokens?, leeway: TimeInterval = 30, refresher: @escaping Refresher, onChange: @escaping @Sendable (Tokens?) -> Void = { _ in }) {
        self.tokens = tokens
        self.leeway = leeway
        self.refresher = refresher
        self.onChange = onChange
    }

    public var current: Tokens? { tokens }

    public func update(_ tokens: Tokens?) {
        refreshing?.cancel()
        refreshing = nil
        self.tokens = tokens
        onChange(tokens)
    }

    public func accessToken() async throws -> String? {
        guard let tokens else { return nil }
        if let expires = tokens.accessTokenExpiresAt, expires.timeIntervalSinceNow < leeway {
            return try await refreshed(from: tokens)?.accessToken
        }
        return tokens.accessToken
    }

    public func refresh(rejected token: String) async throws -> Bool {
        guard let tokens else { return false }
        if tokens.accessToken != token { return true }
        return try await refreshed(from: tokens) != nil
    }

    private func refreshed(from old: Tokens) async throws -> Tokens? {
        if let refreshing {
            return try await outcome(of: refreshing)
        }
        let refresher = self.refresher
        let task = Task { try await refresher(old.refreshToken) }
        refreshing = task
        return try await outcome(of: task)
    }

    private func outcome(of task: Task<Tokens, any Error>) async throws -> Tokens? {
        do {
            let fresh = try await task.value
            if refreshing == task {
                refreshing = nil
                tokens = fresh
                onChange(fresh)
            }
            return fresh
        } catch TaskerAPIError.unauthenticated, TaskerAPIError.badRequest {
            if refreshing == task {
                refreshing = nil
                tokens = nil
                onChange(nil)
            }
            return nil
        } catch {
            if refreshing == task { refreshing = nil }
            throw error
        }
    }
}
