import Foundation
import os
@testable import TaskerAPI

struct StubResponse: Sendable {
    var status: Int = 200
    var contentType = "application/json"
    var chunks: [Data] = []
    var delay: Duration = .zero
    var chunkDelay: Duration = .zero

    static func json(_ text: String, status: Int = 200) -> StubResponse {
        StubResponse(status: status, chunks: [Data(text.utf8)])
    }

    static func fixture(_ name: String, status: Int = 200) -> StubResponse {
        StubResponse(status: status, contentType: name.hasSuffix(".sse") ? "text/event-stream" : "application/json", chunks: [Fixture.data(name)])
    }
}

struct RecordedRequest: Sendable {
    var method: String
    var url: URL
    var headers: [String: String]
    var body: Data

    var json: JSONValue? { try? JSONDecoder().decode(JSONValue.self, from: body) }
    var authorization: String? { headers.first { $0.key.lowercased() == "authorization" }?.value }
}

final class StubServer: Sendable {
    typealias Handler = @Sendable (RecordedRequest, Int) async -> StubResponse

    let id = UUID().uuidString.lowercased()
    private let state = OSAllocatedUnfairLock(initialState: (requests: [RecordedRequest](), handler: Optional<Handler>.none))

    init(_ handler: @escaping Handler) {
        state.withLock { $0.handler = handler }
        StubRegistry.shared.add(self)
    }

    deinit {
        StubRegistry.shared.remove(id)
    }

    var requests: [RecordedRequest] { state.withLock { $0.requests } }

    func endpoint(authorization: any TaskerAuthorization = NoAuthorization(), timeout: TimeInterval = 5) -> TaskerEndpoint {
        TaskerEndpoint(baseURL: URL(string: "http://stub.test/w/\(id)")!, authorization: authorization, requestTimeout: timeout)
    }

    func client(authorization: any TaskerAuthorization = NoAuthorization(), timeout: TimeInterval = 5) -> TaskerAPIClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        configuration.timeoutIntervalForRequest = timeout
        return TaskerAPIClient(endpoint: endpoint(authorization: authorization, timeout: timeout), session: URLSession(configuration: configuration))
    }

    func respond(to request: RecordedRequest) async -> StubResponse {
        let (handler, index) = state.withLock { state -> (Handler?, Int) in
            state.requests.append(request)
            return (state.handler, state.requests.count - 1)
        }
        return await handler?(request, index) ?? StubResponse(status: 500)
    }
}

final class StubRegistry: Sendable {
    static let shared = StubRegistry()
    private let servers = OSAllocatedUnfairLock(initialState: [String: StubServer]())

    func add(_ server: StubServer) {
        servers.withLock { $0[server.id] = server }
    }

    func remove(_ id: String) {
        _ = servers.withLock { $0.removeValue(forKey: id) }
    }

    func server(for url: URL) -> StubServer? {
        let components = url.pathComponents
        guard let index = components.firstIndex(of: "w"), index + 1 < components.count else { return nil }
        return servers.withLock { $0[components[index + 1]] }
    }
}

final class StubProtocol: URLProtocol, @unchecked Sendable {
    private let loading = OSAllocatedUnfairLock<Task<Void, Never>?>(initialState: nil)

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let request = self.request
        let client = UncheckedBox(self.client)
        guard let url = request.url, let server = StubRegistry.shared.server(for: url) else {
            client.value?.urlProtocol(self, didFailWithError: URLError(.cannotFindHost))
            return
        }
        let recorded = RecordedRequest(
            method: request.httpMethod ?? "GET",
            url: url,
            headers: request.allHTTPHeaderFields ?? [:],
            body: request.httpBody ?? Self.read(request.httpBodyStream))
        let protocolInstance = UncheckedBox(self)
        loading.withLock {
            $0 = Task {
                let response = await server.respond(to: recorded)
                do {
                    try await Task.sleep(for: response.delay)
                    let http = HTTPURLResponse(url: url, statusCode: response.status, httpVersion: "HTTP/1.1", headerFields: ["Content-Type": response.contentType])!
                    client.value?.urlProtocol(protocolInstance.value, didReceive: http, cacheStoragePolicy: .notAllowed)
                    for chunk in response.chunks {
                        try await Task.sleep(for: response.chunkDelay)
                        client.value?.urlProtocol(protocolInstance.value, didLoad: chunk)
                    }
                    client.value?.urlProtocolDidFinishLoading(protocolInstance.value)
                } catch {
                    return
                }
            }
        }
    }

    override func stopLoading() {
        loading.withLock { $0?.cancel() }
    }

    private static func read(_ stream: InputStream?) -> Data {
        guard let stream else { return Data() }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            if count <= 0 { break }
            data.append(buffer, count: count)
        }
        return data
    }
}

struct UncheckedBox<Value>: @unchecked Sendable {
    let value: Value
    init(_ value: Value) { self.value = value }
}

enum Fixture {
    static func data(_ name: String) -> Data {
        let url = Bundle.module.url(forResource: name, withExtension: nil, subdirectory: "Fixtures")!
        return try! Data(contentsOf: url)
    }

    static func text(_ name: String) -> String {
        String(decoding: data(name), as: UTF8.self)
    }
}
