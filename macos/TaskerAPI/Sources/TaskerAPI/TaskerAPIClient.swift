import Foundation

public final class TaskerAPIClient: Sendable {
    public let endpoint: TaskerEndpoint
    public let graphQL: GraphQLClient
    public let rest: RESTClient

    public init(endpoint: TaskerEndpoint, session: URLSession? = nil, cache: NormalizedCache = NormalizedCache()) {
        let session = session ?? TaskerHTTP.makeSession(timeout: endpoint.requestTimeout)
        self.endpoint = endpoint
        graphQL = GraphQLClient(http: TaskerHTTP(endpoint: endpoint, session: session), cache: cache)
        rest = RESTClient(endpoint: endpoint, session: session)
    }

    public var cache: NormalizedCache { graphQL.cache }

    public func liveUpdates(reconnect: ReconnectPolicy = .default, clock: @escaping @Sendable () -> Date = Date.init) -> AsyncThrowingStream<LiveEvent, any Error> {
        WorkspaceLiveUpdates(client: self, clock: clock).events(reconnect: reconnect)
    }
}
