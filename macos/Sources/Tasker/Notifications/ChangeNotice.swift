import Foundation
import TaskerAPI

struct ChangeNotice: Equatable, Sendable {
    var kind: NotificationKind
    var message: LiveToastMessage
    var taskID: UUID
    var reference: String?

    init?(_ update: LiveUpdate, ownAuthorKey: String) {
        guard let message = LiveToastMessage(update, ownAuthorKey: ownAuthorKey) else { return nil }
        let kind: NotificationKind
        switch message {
        case .taskCreated: kind = .created
        case .taskMoved: kind = .status
        case .taskRenamed, .taskChanged: kind = .changed
        case .taskDeleted: kind = .deleted
        default: return nil
        }
        let snapshot: TaskSnapshot
        switch update.change {
        case .taskCreated(let task): snapshot = task
        case .taskUpdated(_, let after): snapshot = after
        case .taskDeleted(let task?, _): snapshot = task
        default: return nil
        }
        self.kind = kind
        self.message = message
        taskID = snapshot.id
        reference = snapshot.reference
    }

    func target() -> TaskerLinkTarget {
        if kind == .deleted { return .workspace }
        if let reference, TaskerLink.isReference(reference) { return .task(reference: reference) }
        return .taskID(taskID)
    }
}
