import Foundation
import TaskerAPI
import Testing
import UserNotifications
@testable import Tasker

@MainActor
final class FakeCenter: NotificationCenterClient {
    var status: UNAuthorizationStatus = .authorized
    var grants = true
    var requested = 0
    var added: [UNNotificationRequest] = []

    func authorizationStatus() async -> UNAuthorizationStatus { status }

    func requestAuthorization() async -> Bool {
        requested += 1
        status = grants ? .authorized : .denied
        return grants
    }

    func add(_ request: UNNotificationRequest) async throws { added.append(request) }
}

private func isolated() -> UserDefaults {
    UserDefaults(suiteName: "tasker-notification-tests-\(UUID().uuidString)")!
}

private func snapshot(_ id: UUID = UUID(), status: UUID = UUID(), reference: String? = "TSK-7", title: String = "Fix", version: String = "1") -> TaskSnapshot {
    TaskSnapshot(id: id, projectId: UUID(), title: title, statusId: status, statusName: "Done", reference: reference, version: version)
}

private func update(_ change: LiveChange, author: String = "agent") -> LiveUpdate {
    LiveUpdate(change: change, author: ChangeAuthor(key: author, name: "Claude"), receivedAt: Date())
}

@MainActor
@Suite struct TaskNotifierTests {
    let center = FakeCenter()
    let defaults = isolated()
    var foreground = false

    private func make(foreground: Bool = false) -> TaskNotifier {
        TaskNotifier(client: center, defaults: defaults, isForeground: { foreground })
    }

    @Test func postsForAnotherAuthorWithLink() async throws {
        let notifier = make()
        await notifier.handle(update(.taskCreated(snapshot())), path: "/tmp/Мой проект")
        let request = try #require(center.added.first)
        let text = try #require(request.content.userInfo[TaskNotifier.urlKey] as? String)
        let link = try #require(TaskerLink.parse(text))
        #expect(link.target == .task(reference: "TSK-7"))
        #expect(WorkspaceLinkID.matches(link.workspace, path: "/tmp/Мой проект"))
        #expect(request.content.title == "Мой проект")
    }

    @Test func ignoresOwnChanges() async {
        await make().handle(update(.taskCreated(snapshot()), author: ChangeAuthor.localKey), path: "/tmp/a")
        #expect(center.added.isEmpty)
    }

    @Test func staysQuietWhileAppIsInFront() async {
        await make(foreground: true).handle(update(.taskCreated(snapshot())), path: "/tmp/a")
        #expect(center.added.isEmpty)
    }

    @Test func respectsMasterSwitchAndKinds() async {
        defaults.set(false, forKey: NotificationKind.created.storageKey)
        await make().handle(update(.taskCreated(snapshot())), path: "/tmp/a")
        #expect(center.added.isEmpty)
        defaults.set(false, forKey: NotificationPreferences.enabledKey)
        await make().handle(update(.taskUpdated(before: snapshot(version: "1"), after: snapshot(status: UUID(), version: "2"))), path: "/tmp/a")
        #expect(center.added.isEmpty)
    }

    @Test func deletedAndRenamedAreOffByDefault() async {
        let task = snapshot()
        await make().handle(update(.taskDeleted(task, id: task.id)), path: "/tmp/a")
        await make().handle(update(.taskUpdated(before: task, after: snapshot(task.id, status: task.statusId, title: "New", version: "2"))), path: "/tmp/a")
        #expect(center.added.isEmpty)
    }

    @Test func deletedOpensOnlyTheWorkspace() async throws {
        defaults.set(true, forKey: NotificationKind.deleted.storageKey)
        let task = snapshot()
        await make().handle(update(.taskDeleted(task, id: task.id)), path: "/tmp/a")
        let text = try #require(center.added.first?.content.userInfo[TaskNotifier.urlKey] as? String)
        #expect(TaskerLink.parse(text)?.target == .workspace)
    }

    @Test func taskWithoutReferenceUsesItsID() async throws {
        let task = snapshot(reference: nil)
        await make().handle(update(.taskCreated(task)), path: "/tmp/a")
        let text = try #require(center.added.first?.content.userInfo[TaskNotifier.urlKey] as? String)
        #expect(TaskerLink.parse(text)?.target == .taskID(task.id))
    }

    @Test func nothingWhenSystemDenied() async {
        center.status = .denied
        await make().handle(update(.taskCreated(snapshot())), path: "/tmp/a")
        #expect(center.added.isEmpty)
    }

    @Test func sameTaskReplacesItsNotification() async throws {
        let task = snapshot()
        await make().handle(update(.taskCreated(task)), path: "/tmp/a")
        await make().handle(update(.taskUpdated(before: task, after: snapshot(task.id, status: UUID(), version: "2"))), path: "/tmp/a")
        #expect(Set(center.added.map(\.identifier)).count == 1)
        #expect(center.added.count == 2)
    }

    @Test func preparingAsksOnlyWhenUndecidedAndEnabled() async {
        center.status = .notDetermined
        defaults.set(false, forKey: NotificationPreferences.enabledKey)
        await make().prepare()
        #expect(center.requested == 0)
        defaults.set(true, forKey: NotificationPreferences.enabledKey)
        await make().prepare()
        await make().prepare()
        #expect(center.requested == 1)
    }
}

@Suite struct CopyLinkCommandTests {
    @Test func needsALinkTarget() {
        #expect(!AppCommand.copyLink.isEnabled(CommandState(hasWindow: true, hasTab: true, hasProject: true)))
        #expect(AppCommand.copyLink.isEnabled(CommandState(hasWindow: true, hasTab: true, hasProject: true, hasLink: true)))
    }
}
