import Foundation

struct TaskerHTTP: Sendable {
    let endpoint: TaskerEndpoint
    let session: URLSession

    func data(for request: URLRequest, timeout: TimeInterval? = nil) async throws -> (Data, HTTPURLResponse) {
        try await authorized(request, timeout: timeout ?? endpoint.requestTimeout) { request in
            let (data, response) = try await session.data(for: request)
            return (data, try http(response))
        } status: { $0.1.statusCode }
    }

    func bytes(for request: URLRequest, timeout: TimeInterval) async throws -> (URLSession.AsyncBytes, HTTPURLResponse) {
        try await authorized(request, timeout: timeout) { request in
            let (bytes, response) = try await session.bytes(for: request)
            return (bytes, try http(response))
        } status: { $0.1.statusCode }
    }

    private func authorized<T>(
        _ request: URLRequest,
        timeout: TimeInterval,
        send: (URLRequest) async throws -> T,
        status: (T) -> Int
    ) async throws -> T {
        var refreshed = false
        while true {
            try Task.checkCancellation()
            var request = request
            request.timeoutInterval = timeout
            let token = try await endpoint.authorization.accessToken()
            if let token {
                request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            }
            let result: T
            do {
                result = try await send(request)
            } catch {
                throw TaskerAPIError.wrap(error)
            }
            if status(result) == 401, let token, !refreshed, try await endpoint.authorization.refresh(rejected: token) {
                refreshed = true
                continue
            }
            return result
        }
    }

    private func http(_ response: URLResponse) throws -> HTTPURLResponse {
        guard let response = response as? HTTPURLResponse else {
            throw TaskerAPIError.invalidResponse("Not an HTTP response")
        }
        return response
    }

    static func makeSession(timeout: TimeInterval) -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }
}
