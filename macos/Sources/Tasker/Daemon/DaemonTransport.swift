import Foundation

protocol DaemonTransport: Sendable {
    func status(of info: DaemonInfo) async throws -> DaemonStatus
    func upgrade(info: DaemonInfo, program: String, timeoutSeconds: Int) async throws -> DaemonUpgradeResult
    func stop(info: DaemonInfo) async throws
}

struct DaemonTransportError: Error, Equatable {
    var statusCode: Int
}

struct URLSessionDaemonTransport: DaemonTransport {
    static let controlHeader = "X-Tasker-Control"

    func status(of info: DaemonInfo) async throws -> DaemonStatus {
        let (data, _) = try await send(info: info, path: "/daemon/status", method: "GET", body: nil, timeout: 5)
        return try JSONDecoder().decode(DaemonStatus.self, from: data)
    }

    func upgrade(info: DaemonInfo, program: String, timeoutSeconds: Int) async throws -> DaemonUpgradeResult {
        let body = try JSONSerialization.data(withJSONObject: ["file": program, "arguments": [String](), "timeoutSeconds": timeoutSeconds])
        let (data, _) = try await send(info: info, path: "/daemon/upgrade", method: "POST", body: body, timeout: TimeInterval(timeoutSeconds + 30))
        return try JSONDecoder().decode(DaemonUpgradeResult.self, from: data)
    }

    func stop(info: DaemonInfo) async throws {
        _ = try await send(info: info, path: "/daemon/stop", method: "POST", body: nil, timeout: 5)
    }

    private func send(info: DaemonInfo, path: String, method: String, body: Data?, timeout: TimeInterval) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(info.port)\(path)")!, timeoutInterval: timeout)
        request.httpMethod = method
        request.setValue(info.token, forHTTPHeaderField: Self.controlHeader)
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await URLSession(configuration: .ephemeral).data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        guard (200..<300).contains(http.statusCode) else { throw DaemonTransportError(statusCode: http.statusCode) }
        return (data, http)
    }
}
