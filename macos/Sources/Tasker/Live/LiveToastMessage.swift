import Foundation
import TaskerAPI

struct TaskLabel: Equatable, Sendable {
    var reference: String?
    var title: String

    init(_ task: TaskSnapshot) {
        reference = task.reference
        title = task.title
    }

    init(reference: String?, title: String) {
        self.reference = reference
        self.title = title
    }

    func text(_ l: Localizer) -> String {
        reference ?? l.string(.toastQuoted, title)
    }
}

enum LiveToastMessage: Equatable, Sendable {
    case taskMoved(author: String?, task: TaskLabel, status: String)
    case taskCreated(author: String?, task: TaskLabel)
    case taskRenamed(author: String?, task: TaskLabel)
    case taskDeleted(author: String?, task: TaskLabel)
    case taskChanged(author: String?, task: TaskLabel)
    case projectCreated(author: String?, name: String)
    case projectRenamed(author: String?, name: String)
    case projectDeleted(author: String?, name: String)
    case reconnected
    case notice(String)
    case failure(String)

    init?(_ update: LiveUpdate, ownAuthorKey: String) {
        if update.author?.key == ownAuthorKey { return nil }
        let author = update.author?.name
        switch update.change {
        case .taskCreated(let task):
            self = .taskCreated(author: author, task: TaskLabel(task))
        case .taskUpdated(let before, let after):
            let label = TaskLabel(after)
            if let before {
                if before.version == after.version { return nil }
                if before.statusId != after.statusId {
                    self = .taskMoved(author: author, task: label, status: after.statusName ?? "")
                } else if before.title != after.title {
                    self = .taskRenamed(author: author, task: label)
                } else {
                    self = .taskChanged(author: author, task: label)
                }
            } else {
                self = .taskChanged(author: author, task: label)
            }
        case .taskDeleted(let task, _):
            guard let task else { return nil }
            self = .taskDeleted(author: author, task: TaskLabel(task))
        case .projectCreated(let name):
            self = .projectCreated(author: author, name: name)
        case .projectRenamed(let from, let to):
            if from == to { return nil }
            self = .projectRenamed(author: author, name: to)
        case .projectDeleted(let name):
            guard let name else { return nil }
            self = .projectDeleted(author: author, name: name)
        case .other, .reloaded:
            return nil
        }
    }

    func text(_ l: Localizer) -> String {
        switch self {
        case .taskMoved(let author?, let task, let status): l.string(.toastMoved, author, task.text(l), status)
        case .taskMoved(nil, let task, let status): l.string(.toastMovedAnonymous, task.text(l), status)
        case .taskCreated(let author?, let task): l.string(.toastCreated, author, task.text(l), task.title)
        case .taskCreated(nil, let task): l.string(.toastCreatedAnonymous, task.text(l))
        case .taskRenamed(let author?, let task): l.string(.toastRenamed, author, task.text(l))
        case .taskRenamed(nil, let task): l.string(.toastRenamedAnonymous, task.text(l))
        case .taskDeleted(let author?, let task): l.string(.toastDeleted, author, task.text(l))
        case .taskDeleted(nil, let task): l.string(.toastDeletedAnonymous, task.text(l))
        case .taskChanged(let author?, let task): l.string(.toastChanged, author, task.text(l))
        case .taskChanged(nil, let task): l.string(.toastChangedAnonymous, task.text(l))
        case .projectCreated(let author?, let name): l.string(.toastProjectCreated, author, name)
        case .projectCreated(nil, let name): l.string(.toastProjectCreatedAnonymous, name)
        case .projectRenamed(let author?, let name): l.string(.toastProjectRenamed, author, name)
        case .projectRenamed(nil, let name): l.string(.toastProjectRenamedAnonymous, name)
        case .projectDeleted(let author?, let name): l.string(.toastProjectDeleted, author, name)
        case .projectDeleted(nil, let name): l.string(.toastProjectDeletedAnonymous, name)
        case .reconnected: l.string(.toastReconnected)
        case .notice(let text), .failure(let text): text
        }
    }

    var isFailure: Bool {
        if case .failure = self { return true }
        return false
    }
}

enum LiveStatusText {
    static func text(connection: LiveConnection, _ l: Localizer) -> String? {
        switch connection {
        case .connecting: l.string(.liveConnecting)
        case .offline: l.string(.liveOffline)
        case .live: nil
        }
    }
}
