import Foundation

public enum CachePolicy: Sendable, Hashable {
    case cacheFirst
    case networkOnly
    case cacheOnly
}

public enum SubscriptionEvent<Value: Sendable>: Sendable {
    case connected
    case value(Value)
    case disconnected
}

extension SubscriptionEvent: Equatable where Value: Equatable {}

public struct ReconnectPolicy: Sendable, Hashable {
    public var initialDelay: Duration
    public var maxDelay: Duration
    public var maxAttempts: Int?
    public var idleTimeout: TimeInterval

    public init(initialDelay: Duration = .seconds(1), maxDelay: Duration = .seconds(30), maxAttempts: Int? = nil, idleTimeout: TimeInterval = 60) {
        self.initialDelay = initialDelay
        self.maxDelay = maxDelay
        self.maxAttempts = maxAttempts
        self.idleTimeout = idleTimeout
    }

    public static let `default` = ReconnectPolicy()
    public static let never = ReconnectPolicy(maxAttempts: 0)
}

public final class GraphQLClient: Sendable {
    let http: TaskerHTTP
    public let cache: NormalizedCache

    init(http: TaskerHTTP, cache: NormalizedCache) {
        self.http = http
        self.cache = cache
    }

    public func fetch<Query: GraphQLQuery>(_ query: Query, cachePolicy: CachePolicy = .networkOnly) async throws -> Query.Data {
        if cachePolicy != .networkOnly, let cached = await cache.read(query) {
            return cached
        }
        if cachePolicy == .cacheOnly {
            throw TaskerAPIError.notFound("\(Query.operationName) is not in the cache")
        }
        let data = try await execute(query)
        await cache.write(data, selections: Query.selections, variables: query.variables)
        return try decode(Query.Data.self, from: data)
    }

    public func watch<Query: GraphQLQuery>(_ query: Query, cachePolicy: CachePolicy = .cacheFirst) -> AsyncThrowingStream<Query.Data, any Error> {
        let (stream, continuation) = AsyncThrowingStream<Query.Data, any Error>.makeStream(bufferingPolicy: .bufferingNewest(1))
        let task = Task { [cache] in
            let changes = await cache.changes()
            var last: Query.Data?
            var dependencies: Set<String> = []

            func emitCached() async -> Bool {
                guard let (json, keys) = await cache.read(selections: Query.selections, variables: query.variables),
                      let value = try? TaskerJSON.decode(Query.Data.self, from: json) else { return false }
                dependencies = keys
                if value != last {
                    last = value
                    continuation.yield(value)
                }
                return true
            }

            func load() async throws {
                let json = try await self.execute(query)
                await cache.write(json, selections: Query.selections, variables: query.variables)
                if await !emitCached() {
                    let value = try self.decode(Query.Data.self, from: json)
                    if value != last {
                        last = value
                        continuation.yield(value)
                    }
                }
            }

            do {
                let cached = cachePolicy != .networkOnly ? await emitCached() : false
                if !cached {
                    if cachePolicy == .cacheOnly {
                        dependencies = [NormalizedCache.rootKey]
                    } else {
                        try await load()
                    }
                }
                for await keys in changes {
                    guard !keys.isDisjoint(with: dependencies) else { continue }
                    if await emitCached() { continue }
                    if cachePolicy == .cacheOnly { continue }
                    try await load()
                }
                continuation.finish()
            } catch {
                continuation.finish(throwing: TaskerAPIError.wrap(error))
            }
        }
        continuation.onTermination = { _ in task.cancel() }
        return stream
    }

    public func subscribe<Subscription: GraphQLSubscription>(
        _ subscription: Subscription,
        reconnect: ReconnectPolicy = .default
    ) -> AsyncThrowingStream<SubscriptionEvent<Subscription.Data>, any Error> {
        let (stream, continuation) = AsyncThrowingStream<SubscriptionEvent<Subscription.Data>, any Error>.makeStream()
        let task = Task {
            var delay = reconnect.initialDelay
            var failures = 0
            while !Task.isCancelled {
                do {
                    try await self.stream(subscription, idleTimeout: reconnect.idleTimeout) { event in
                        if case .connected = event {
                            delay = reconnect.initialDelay
                            failures = 0
                        }
                        continuation.yield(event)
                    }
                    continuation.finish()
                    return
                } catch {
                    let error = TaskerAPIError.wrap(error)
                    if error is CancellationError {
                        continuation.finish()
                        return
                    }
                    let retryable = (error as? TaskerAPIError)?.isRetryable ?? false
                    failures += 1
                    if !retryable || (reconnect.maxAttempts.map { failures > $0 } ?? false) {
                        continuation.finish(throwing: error)
                        return
                    }
                    continuation.yield(.disconnected)
                    do {
                        try await Task.sleep(for: delay)
                    } catch {
                        continuation.finish()
                        return
                    }
                    delay = min(delay * 2, reconnect.maxDelay)
                }
            }
            continuation.finish()
        }
        continuation.onTermination = { _ in task.cancel() }
        return stream
    }

    func execute<Operation: GraphQLOperation>(_ operation: Operation) async throws -> JSONValue {
        let request = try makeRequest(operation, accept: "application/json")
        let (body, response) = try await http.data(for: request)
        return try Self.parse(status: response.statusCode, body: body)
    }

    static func parse(status: Int, body: Data) throws -> JSONValue {
        let decoded: GraphQLResponseBody
        do {
            decoded = try JSONDecoder().decode(GraphQLResponseBody.self, from: body)
        } catch {
            if status >= 400 { throw TaskerAPIError.from(status: status, body: body) }
            throw TaskerAPIError.invalidResponse("Invalid GraphQL response: \(error.localizedDescription)")
        }
        if let errors = decoded.errors, !errors.isEmpty {
            throw TaskerAPIError.from(graphQL: errors)
        }
        guard status < 400 else { throw TaskerAPIError.from(status: status, body: body) }
        guard let data = decoded.data, case .object = data else {
            throw TaskerAPIError.invalidResponse("GraphQL response has no data")
        }
        return data
    }

    private func stream<Subscription: GraphQLSubscription>(
        _ subscription: Subscription,
        idleTimeout: TimeInterval,
        yield: (SubscriptionEvent<Subscription.Data>) -> Void
    ) async throws {
        let request = try makeRequest(subscription, accept: "text/event-stream")
        let (bytes, response) = try await http.bytes(for: request, timeout: idleTimeout)
        let contentType = response.value(forHTTPHeaderField: "Content-Type") ?? ""
        guard response.statusCode == 200, contentType.hasPrefix("text/event-stream") else {
            var body = Data()
            for try await byte in bytes {
                body.append(byte)
                if body.count > 1 << 20 { break }
            }
            _ = try Self.parse(status: response.statusCode, body: body)
            throw TaskerAPIError.invalidResponse("Subscription did not return an event stream")
        }
        yield(.connected)
        var parser = ServerSentEventParser()
        do {
            for try await byte in bytes {
                guard let event = parser.consume(byte) else { continue }
                switch event.event {
                case "next":
                    let data = try Self.parse(status: 200, body: Data(event.data.utf8))
                    yield(.value(try decode(Subscription.Data.self, from: data)))
                case "complete":
                    return
                default:
                    continue
                }
            }
        } catch {
            throw TaskerAPIError.wrap(error)
        }
        try Task.checkCancellation()
        throw TaskerAPIError.network(.networkConnectionLost, "The event stream ended")
    }

    private func makeRequest<Operation: GraphQLOperation>(_ operation: Operation, accept: String) throws -> URLRequest {
        var request = URLRequest(url: http.endpoint.graphQLURL)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(accept, forHTTPHeaderField: "Accept")
        request.httpBody = try TaskerJSON.encoder().encode(GraphQLRequestBody(operation))
        return request
    }

    private func decode<T: Decodable>(_ type: T.Type, from data: JSONValue) throws -> T {
        do {
            return try TaskerJSON.decode(type, from: data)
        } catch {
            throw TaskerAPIError.invalidResponse("Cannot decode \(T.self): \(error)")
        }
    }
}
