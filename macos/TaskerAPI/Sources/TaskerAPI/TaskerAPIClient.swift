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

    public func workspaceChanges(reconnect: ReconnectPolicy = .default, updatingCache: Bool = true) -> AsyncThrowingStream<SubscriptionEvent<WorkspaceChangesSubscription.Data.WorkspaceChanges>, any Error> {
        let source = graphQL.subscribe(WorkspaceChangesSubscription(), reconnect: reconnect)
        let cache = self.cache
        let (stream, continuation) = AsyncThrowingStream<SubscriptionEvent<WorkspaceChangesSubscription.Data.WorkspaceChanges>, any Error>.makeStream()
        let task = Task {
            var connections = 0
            do {
                for try await event in source {
                    switch event {
                    case .connected:
                        connections += 1
                        if updatingCache, connections > 1 { await cache.evictAll() }
                        continuation.yield(.connected)
                    case .value(let data):
                        let change = data.workspaceChanges
                        if updatingCache { await cache.apply(change: change.entity, projectId: change.projectId, id: change.id) }
                        continuation.yield(.value(change))
                    }
                }
                continuation.finish()
            } catch {
                continuation.finish(throwing: error)
            }
        }
        continuation.onTermination = { _ in task.cancel() }
        return stream
    }
}
