import Foundation

public actor NormalizedCache {
    public static let rootKey = "QUERY_ROOT"

    indirect enum Value: Sendable, Hashable {
        case scalar(JSONValue)
        case reference(String)
        case list([Value])
    }

    private var records: [String: [String: Value]] = [:]
    private var observers: [UUID: AsyncStream<Set<String>>.Continuation] = [:]

    public init() {}

    public var recordCount: Int { records.count }

    public func keys() -> Set<String> { Set(records.keys) }

    @discardableResult
    public func write(_ data: JSONValue, selections: [GraphQLField], variables: [String: JSONValue]) -> Set<String> {
        guard case .object(let object) = data else { return [] }
        var changed: Set<String> = []
        normalize(object, selections: selections, variables: variables, key: Self.rootKey, changed: &changed)
        notify(changed)
        return changed
    }

    public func read(selections: [GraphQLField], variables: [String: JSONValue]) -> (data: JSONValue, dependencies: Set<String>)? {
        var dependencies: Set<String> = []
        guard let object = denormalize(key: Self.rootKey, selections: selections, variables: variables, dependencies: &dependencies) else {
            return nil
        }
        return (.object(object), dependencies)
    }

    public func read<Query: GraphQLQuery>(_ query: Query) -> Query.Data? {
        guard let (data, _) = read(selections: Query.selections, variables: query.variables) else { return nil }
        return try? TaskerJSON.decode(Query.Data.self, from: data)
    }

    public func evict(id: UUID) {
        let suffix = ":" + id.uuidString.lowercased()
        let removed = Set(records.keys.filter { $0.hasSuffix(suffix) })
        for key in removed { records[key] = nil }
        notify(removed)
    }

    public func evict(key: String) {
        guard records.removeValue(forKey: key) != nil else { return }
        notify([key])
    }

    public func evictAll() {
        let removed = Set(records.keys).union([Self.rootKey])
        records.removeAll()
        notify(removed)
    }

    public func record(typename: String, id: UUID, selections: [GraphQLField]) -> JSONValue? {
        var dependencies: Set<String> = []
        return denormalize(key: Self.key(typename: typename, id: id.uuidString), selections: selections, variables: [:], dependencies: &dependencies).map(JSONValue.object)
    }

    public func contains(typename: String, id: UUID) -> Bool {
        records[Self.key(typename: typename, id: id.uuidString)] != nil
    }

    @discardableResult
    public func patch(typename: String, id: UUID, with data: JSONValue, selections: [GraphQLField]) -> Set<String> {
        let key = Self.key(typename: typename, id: id.uuidString)
        guard records[key] != nil, case .object(let object) = data else { return [] }
        var changed: Set<String> = []
        normalize(object, selections: selections, variables: [:], key: key, changed: &changed)
        notify(changed)
        return changed
    }

    public func invalidate(key: String, fields names: Set<String>) {
        guard var record = records[key] else { return }
        let removed = record.keys.filter { names.contains(Self.fieldName($0)) }
        guard !removed.isEmpty else { return }
        for field in removed { record[field] = nil }
        records[key] = record
        notify([key])
    }

    public func invalidate(projectId: UUID, fields names: Set<String>) {
        invalidate(key: Self.key(typename: "Project", id: projectId.uuidString), fields: names)
    }

    public func invalidateRoot(fields names: Set<String>) {
        invalidate(key: Self.rootKey, fields: names)
    }

    private static func fieldName(_ storageKey: String) -> String {
        storageKey.firstIndex(of: "(").map { String(storageKey[..<$0]) } ?? storageKey
    }

    public func changes() -> AsyncStream<Set<String>> {
        let (stream, continuation) = AsyncStream<Set<String>>.makeStream(bufferingPolicy: .unbounded)
        let token = UUID()
        observers[token] = continuation
        continuation.onTermination = { [weak self] _ in
            Task { await self?.removeObserver(token) }
        }
        return stream
    }

    private func removeObserver(_ token: UUID) {
        observers[token] = nil
    }

    private func notify(_ keys: Set<String>) {
        guard !keys.isEmpty else { return }
        for observer in observers.values { observer.yield(keys) }
    }

    static func key(typename: String, id: String) -> String {
        typename + ":" + id.lowercased()
    }

    private func normalize(_ object: [String: JSONValue], selections: [GraphQLField], variables: [String: JSONValue], key: String, changed: inout Set<String>) {
        var record = records[key] ?? [:]
        var dirty = false
        for field in selections {
            guard let value = object[field.responseKey] else { continue }
            let storageKey = field.storageKey(variables)
            let normalized = normalizeValue(value, field: field, variables: variables, path: key + "." + storageKey, changed: &changed)
            if record[storageKey] != normalized {
                record[storageKey] = normalized
                dirty = true
            }
        }
        if dirty || records[key] == nil {
            records[key] = record
            changed.insert(key)
        }
    }

    private func normalizeValue(_ value: JSONValue, field: GraphQLField, variables: [String: JSONValue], path: String, changed: inout Set<String>) -> Value {
        if field.selections.isEmpty { return .scalar(value) }
        switch value {
        case .array(let items):
            return .list(items.enumerated().map { index, item in
                normalizeValue(item, field: field, variables: variables, path: path + "." + String(index), changed: &changed)
            })
        case .object(let object):
            let key = Self.cacheKey(object) ?? path
            normalize(object, selections: field.selections, variables: variables, key: key, changed: &changed)
            return .reference(key)
        default:
            return .scalar(value)
        }
    }

    private static func cacheKey(_ object: [String: JSONValue]) -> String? {
        guard let typename = object["__typename"]?.stringValue, let id = object["id"]?.stringValue else { return nil }
        return key(typename: typename, id: id)
    }

    private func denormalize(key: String, selections: [GraphQLField], variables: [String: JSONValue], dependencies: inout Set<String>) -> [String: JSONValue]? {
        dependencies.insert(key)
        guard let record = records[key] else { return nil }
        var result: [String: JSONValue] = [:]
        for field in selections {
            guard let value = record[field.storageKey(variables)],
                  let resolved = resolve(value, field: field, variables: variables, dependencies: &dependencies) else { return nil }
            result[field.responseKey] = resolved
        }
        return result
    }

    private func resolve(_ value: Value, field: GraphQLField, variables: [String: JSONValue], dependencies: inout Set<String>) -> JSONValue? {
        switch value {
        case .scalar(let scalar):
            return scalar
        case .reference(let key):
            return denormalize(key: key, selections: field.selections, variables: variables, dependencies: &dependencies).map(JSONValue.object)
        case .list(let items):
            var result: [JSONValue] = []
            result.reserveCapacity(items.count)
            for item in items {
                guard let resolved = resolve(item, field: field, variables: variables, dependencies: &dependencies) else { return nil }
                result.append(resolved)
            }
            return .array(result)
        }
    }
}
