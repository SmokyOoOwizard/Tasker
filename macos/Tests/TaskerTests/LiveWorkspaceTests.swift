import Foundation
import Testing
import TaskerAPI
@testable import Tasker

@MainActor
@Suite struct LiveWorkspaceTests {
    private let ru = Localizer(language: .ru)
    private let en = Localizer(language: .en)
    private let projectId = UUID()
    private let todo = UUID()
    private let doing = UUID()
    private let claude = ChangeAuthor(key: "user:1", name: "Claude")
    private let at = Date(timeIntervalSince1970: 1_800_000_000)

    private func task(status: UUID, title: String = "Вход по паролю", version: String, statusName: String? = nil, reference: String? = "TSK-131") -> TaskSnapshot {
        TaskSnapshot(id: UUID(uuidString: "19509c26-992a-475d-8946-91e3cb71f1d2")!, projectId: projectId, title: title, statusId: status, statusName: statusName, reference: reference, version: version)
    }

    private func update(_ change: LiveChange, author: ChangeAuthor?) -> LiveUpdate {
        LiveUpdate(change: change, author: author, receivedAt: at)
    }

    private func workspace() -> LiveWorkspace {
        LiveWorkspace(makeClient: { TaskerAPIClient(endpoint: .local(port: $0.port, workspace: $0.key)) }, toastDuration: .seconds(60))
    }

    @Test func describesStatusMovesLikeTheMockup() throws {
        let moved = try #require(LiveToastMessage(update(.taskUpdated(before: task(status: todo, version: "1"), after: task(status: doing, version: "2", statusName: "В работе")), author: claude), ownAuthorKey: ChangeAuthor.localKey))
        #expect(moved.text(ru) == "Claude перевёл TSK-131 в «В работе»")
        #expect(moved.text(en) == "Claude moved TSK-131 to “В работе”")

        let anonymous = try #require(LiveToastMessage(update(.taskUpdated(before: task(status: todo, version: "1"), after: task(status: doing, version: "2", statusName: "В работе")), author: nil), ownAuthorKey: ChangeAuthor.localKey))
        #expect(anonymous.text(ru) == "TSK-131 → «В работе»")
    }

    @Test func describesOtherTaskChanges() throws {
        let created = try #require(LiveToastMessage(update(.taskCreated(task(status: todo, title: "Тёмная тема", version: "1", reference: "TSK-133")), author: claude), ownAuthorKey: ChangeAuthor.localKey))
        #expect(created.text(ru) == "Claude создал TSK-133 «Тёмная тема»")
        let renamed = try #require(LiveToastMessage(update(.taskUpdated(before: task(status: todo, title: "Вход", version: "1"), after: task(status: todo, version: "2")), author: nil), ownAuthorKey: ChangeAuthor.localKey))
        #expect(renamed.text(ru) == "TSK-131 переименована")
        let unseen = try #require(LiveToastMessage(update(.taskUpdated(before: nil, after: task(status: todo, version: "2", reference: nil)), author: claude), ownAuthorKey: ChangeAuthor.localKey))
        #expect(unseen.text(ru) == "Claude изменил «Вход по паролю»")
        let deleted = try #require(LiveToastMessage(update(.taskDeleted(task(status: todo, version: "1"), id: UUID()), author: claude), ownAuthorKey: ChangeAuthor.localKey))
        #expect(deleted.text(en) == "Claude deleted TSK-131")
        let project = try #require(LiveToastMessage(update(.projectRenamed(from: "Сайт", to: "Лендинг"), author: nil), ownAuthorKey: ChangeAuthor.localKey))
        #expect(project.text(ru) == "Проект переименован в «Лендинг»")
    }

    @Test func staysQuietAboutOwnAndEmptyChanges() {
        let own = ChangeAuthor(key: ChangeAuthor.localKey, name: "Local user")
        #expect(LiveToastMessage(update(.taskCreated(task(status: todo, version: "1")), author: own), ownAuthorKey: ChangeAuthor.localKey) == nil)
        #expect(LiveToastMessage(update(.taskUpdated(before: task(status: todo, version: "1"), after: task(status: todo, version: "1")), author: claude), ownAuthorKey: ChangeAuthor.localKey) == nil)
        #expect(LiveToastMessage(update(.taskDeleted(nil, id: UUID()), author: claude), ownAuthorKey: ChangeAuthor.localKey) == nil)
        #expect(LiveToastMessage(update(.reloaded, author: nil), ownAuthorKey: ChangeAuthor.localKey) == nil)
        #expect(LiveToastMessage(update(.other(entity: "status"), author: claude), ownAuthorKey: ChangeAuthor.localKey) == nil)
    }

    @Test func showsConnectionOnlyWhenItIsNotLive() {
        #expect(LiveStatusText.text(connection: .live, ru) == nil)
        #expect(LiveStatusText.text(connection: .offline, ru) == "нет связи с демоном")
        #expect(LiveStatusText.text(connection: .connecting, en) == "connecting…")
    }

    @Test func tracksConnectionAndAnnouncesReconnection() async {
        let live = workspace()
        let client = TaskerAPIClient(endpoint: .local(port: 1, workspace: "w"))
        await live.handle(.connected(reconnected: false, at: at), client: client)
        #expect(live.connection == .live)
        #expect(live.toasts.isEmpty)

        await live.handle(.disconnected, client: client)
        #expect(live.connection == .offline)

        await live.handle(.connected(reconnected: true, at: at.addingTimeInterval(5)), client: client)
        #expect(live.connection == .live)
        #expect(live.toasts.map(\.message) == [.reconnected])

        await live.handle(.update(LiveUpdate(change: .other(entity: "status"), author: nil, receivedAt: at.addingTimeInterval(9))), client: client)
        #expect(live.revision == 2)
        #expect(live.toasts.count == 1)
    }

    @Test func keepsOnlyTheLatestToasts() {
        let live = workspace()
        for index in 0..<5 {
            live.show(.projectCreated(author: nil, name: "P\(index)"))
        }
        #expect(live.toasts.map(\.message) == [.projectCreated(author: nil, name: "P2"), .projectCreated(author: nil, name: "P3"), .projectCreated(author: nil, name: "P4")])
        live.dismiss(live.toasts[0].id)
        #expect(live.toasts.count == 2)
    }

    @Test func followsTheFirstOpenWorkspaceOfARunningDaemon() {
        let live = workspace()
        let status = DaemonStatus(pid: 1, port: 5719, workers: [], workspaces: [
            DaemonWorkspace(path: "/a/opening", state: "opening", error: nil),
            DaemonWorkspace(path: "/b/Tasker", state: "open", key: "tasker-1", error: nil)
        ])
        live.follow(.starting)
        #expect(live.address == nil)
        live.follow(.running(status))
        #expect(live.address == LiveWorkspaceAddress(port: 5719, key: "tasker-1", name: "Tasker"))
        let client = live.client
        live.follow(.stopped(.notResponding))
        live.follow(.running(status))
        #expect(live.client === client)
        live.follow(.running(DaemonStatus(pid: 1, port: 5719, workers: [], workspaces: [])))
        #expect(live.address == nil)
        #expect(live.client == nil)
        live.stop()
    }
}
