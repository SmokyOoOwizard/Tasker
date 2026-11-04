import Foundation

public struct ChangeAuthor: Hashable, Sendable {
    public var key: String
    public var name: String

    public init(key: String, name: String) {
        self.key = key
        self.name = name
    }

    public static let localKey = "local"
}

public struct TaskSnapshot: Hashable, Sendable {
    public var id: UUID
    public var projectId: UUID
    public var title: String
    public var statusId: UUID
    public var statusName: String?
    public var reference: String?
    public var version: String

    public init(id: UUID, projectId: UUID, title: String, statusId: UUID, statusName: String? = nil, reference: String? = nil, version: String) {
        self.id = id
        self.projectId = projectId
        self.title = title
        self.statusId = statusId
        self.statusName = statusName
        self.reference = reference
        self.version = version
    }
}

public enum LiveChange: Hashable, Sendable {
    case taskCreated(TaskSnapshot)
    case taskUpdated(before: TaskSnapshot?, after: TaskSnapshot)
    case taskDeleted(TaskSnapshot?, id: UUID)
    case projectCreated(name: String)
    case projectRenamed(from: String?, to: String)
    case projectDeleted(name: String?)
    case other(entity: String)
    case reloaded
}

public struct LiveUpdate: Hashable, Sendable {
    public var change: LiveChange
    public var author: ChangeAuthor?
    public var receivedAt: Date

    public init(change: LiveChange, author: ChangeAuthor?, receivedAt: Date) {
        self.change = change
        self.author = author
        self.receivedAt = receivedAt
    }
}

public enum LiveEvent: Hashable, Sendable {
    case connected(reconnected: Bool, at: Date)
    case disconnected
    case update(LiveUpdate)
}
