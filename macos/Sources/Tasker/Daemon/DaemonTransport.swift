import Foundation

protocol DaemonTransport: Sendable {
    func status(of info: DaemonInfo) async throws -> DaemonStatus
    func stop(info: DaemonInfo) async throws
}

struct DaemonTransportError: Error, Equatable {
    var statusCode: Int
}

struct URLSessionDaemonTransport: DaemonTransport {
    static let controlHeader = "X-Tasker-Control"

    func status(of info: DaemonInfo) async throws -> DaemonStatus {
        let (data, _) = try await send(info: info, path: "/daemon/status", method: "GET", timeout: 5)
        return try JSONDecoder().decode(DaemonStatus.self, from: data)
    }

    func stop(info: DaemonInfo) async throws {
        _ = try await send(info: info, path: "/daemon/stop", method: "POST", timeout: 5)
    }

    private func send(info: DaemonInfo, path: String, method: String, timeout: TimeInterval) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(info.port)\(path)")!, timeoutInterval: timeout)
        request.httpMethod = method
        request.setValue(info.token, forHTTPHeaderField: Self.controlHeader)
        let (data, response) = try await URLSession(configuration: .ephemeral).data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        guard (200..<300).contains(http.statusCode) else { throw DaemonTransportError(statusCode: http.statusCode) }
        return (data, http)
    }
}
