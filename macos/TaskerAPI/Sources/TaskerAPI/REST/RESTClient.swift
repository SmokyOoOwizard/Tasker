import Foundation
import HTTPTypes
import OpenAPIRuntime
import OpenAPIURLSession

public struct RESTClient: Sendable {
    public let client: Client
    let session: URLSession

    init(endpoint: TaskerEndpoint, session: URLSession) {
        self.session = session
        client = Client(
            serverURL: endpoint.baseURL,
            configuration: Configuration(dateTranscoder: TaskerDateTranscoder()),
            transport: URLSessionTransport(configuration: .init(session: session)),
            middlewares: [
                ErrorMiddleware(),
                AuthorizationMiddleware(authorization: endpoint.authorization)
            ]
        )
    }

    public func perform<T: Sendable>(_ call: (Client) async throws -> T) async throws -> T {
        do {
            return try await call(client)
        } catch let error as ClientError {
            throw TaskerAPIError.wrap(error.underlyingError)
        } catch {
            throw TaskerAPIError.wrap(error)
        }
    }

    public static func signIn(
        baseURL: URL,
        login: String,
        password: String,
        session urlSession: URLSession? = nil,
        onChange: @escaping @Sendable (ServerSession.Tokens?) -> Void = { _ in }
    ) async throws -> ServerSession {
        let rest = RESTClient(endpoint: TaskerEndpoint(baseURL: baseURL), session: urlSession ?? TaskerHTTP.makeSession(timeout: 30))
        let session = try await rest.perform { client in
            try await client.login(body: .json(.init(login: login, password: password))).ok.body.json
        }
        let tokens = ServerSession.Tokens(session)
        onChange(tokens)
        return ServerSession(tokens: tokens, refresher: refresher(baseURL: baseURL, session: rest.session), onChange: onChange)
    }

    public static func refresher(baseURL: URL, session: URLSession? = nil) -> ServerSession.Refresher {
        let rest = RESTClient(endpoint: TaskerEndpoint(baseURL: baseURL), session: session ?? TaskerHTTP.makeSession(timeout: 30))
        return { refreshToken in
            let session = try await rest.perform { client in
                try await client.refreshSession(body: .json(.init(refreshToken: refreshToken))).ok.body.json
            }
            return ServerSession.Tokens(session)
        }
    }
}

extension ServerSession.Tokens {
    init(_ session: Components.Schemas.AuthSession) {
        self.init(
            accessToken: session.accessToken,
            accessTokenExpiresAt: session.accessTokenExpiresAt,
            refreshToken: session.refreshToken,
            refreshTokenExpiresAt: session.refreshTokenExpiresAt)
    }
}

struct TaskerDateTranscoder: DateTranscoder {
    func encode(_ date: Date) throws -> String {
        TaskerDate.format(date)
    }

    func decode(_ text: String) throws -> Date {
        guard let date = TaskerDate.parse(text) else {
            throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "Invalid date: \(text)"))
        }
        return date
    }
}

struct AuthorizationMiddleware: ClientMiddleware {
    let authorization: any TaskerAuthorization

    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: @Sendable (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let buffered: HTTPBody?
        if let body {
            buffered = HTTPBody(try await Data(collecting: body, upTo: 16 << 20))
        } else {
            buffered = nil
        }
        var refreshed = false
        while true {
            var request = request
            let token = try await authorization.accessToken()
            if let token {
                request.headerFields[.authorization] = "Bearer \(token)"
            }
            let (response, responseBody) = try await next(request, buffered, baseURL)
            if response.status.code == 401, let token, !refreshed, try await authorization.refresh(rejected: token) {
                refreshed = true
                continue
            }
            return (response, responseBody)
        }
    }
}

struct ErrorMiddleware: ClientMiddleware {
    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: @Sendable (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let (response, responseBody) = try await next(request, body, baseURL)
        guard response.status.code >= 400 else { return (response, responseBody) }
        let data: Data
        if let responseBody {
            data = (try? await Data(collecting: responseBody, upTo: 1 << 20)) ?? Data()
        } else {
            data = Data()
        }
        throw TaskerAPIError.from(status: response.status.code, body: data)
    }
}
