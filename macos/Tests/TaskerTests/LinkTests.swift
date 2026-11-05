import Foundation
import Testing
@testable import Tasker

@Suite struct TaskerLinkTests {
    static let path = "/tmp/Мой проект"
    let id = WorkspaceLinkID.make(path: path)

    @Test func workspaceIDHasNameAndShortHash() {
        #expect(id.hasPrefix("Мой проект-"))
        #expect(id.count == "Мой проект-".count + WorkspaceLinkID.hashLength)
        #expect(!id.contains("/tmp"))
    }

    @Test func sameNamesDifferInHash() {
        #expect(WorkspaceLinkID.make(path: "/tmp/a/app") != WorkspaceLinkID.make(path: "/tmp/b/app"))
    }

    @Test func roundTripsEveryTarget() {
        let uuid = UUID()
        let targets: [TaskerLinkTarget] = [.workspace, .task(reference: "TSK-123"), .taskID(uuid), .board(uuid), .series(prefix: "TSK")]
        for target in targets {
            let link = TaskerLink(workspace: id, target: target)
            #expect(TaskerLink.parse(link.string) == link, "\(link.string)")
            #expect(link.url != nil)
        }
    }

    @Test func percentEncodesNonAscii() {
        let text = TaskerLink(workspace: id, target: .task(reference: "TSK-1")).string
        #expect(text.unicodeScalars.allSatisfy { $0.isASCII })
        #expect(text.contains("%20"))
    }

    @Test func acceptsTrailingSlashAndUppercaseScheme() {
        #expect(TaskerLink.parse("TASKER://App-abc123/TSK-5/") == TaskerLink(workspace: "App-abc123", target: .task(reference: "TSK-5")))
    }

    @Test(arguments: [
        "http://App-abc123/TSK-5",
        "tasker:/App-abc123/TSK-5",
        "tasker://App-abc123/TSK-5?x=1",
        "tasker://App-abc123/TSK-5#frag",
        "tasker://user@App-abc123/TSK-5",
        "tasker://App-abc123:80/TSK-5",
        "tasker://App-abc123/../TSK-5",
        "tasker://App-abc123/TSK-5/extra/more",
        "tasker://App-abc123/TSK-x",
        "tasker://App-abc123/TSK-",
        "tasker://App-abc123/TSK-1234567890",
        "tasker://App-abc123/board/not-a-uuid",
        "tasker://App-abc123/unknown/x",
        "tasker://App-abc123/series/a%2Fb",
        "tasker://App/TSK-5",
        "tasker://App-zzzzzz/TSK-5",
        "tasker://-abc123/TSK-5",
        "tasker:///TSK-5",
        "tasker://%2Ftmp%2Fapp-abc123/TSK-5",
        "tasker://App-abc123/TSK-5%00",
        "tasker://App-abc123/TSK-5\n",
        "tasker://App-abc123/ TSK-5",
        "tasker://App-abc123\\TSK-5",
        "",
        "tasker://"
    ])
    func rejectsMalformed(_ text: String) {
        #expect(TaskerLink.parse(text) == nil, "\(text)")
    }

    @Test func rejectsOverlong() {
        #expect(TaskerLink.parse("tasker://App-abc123/" + String(repeating: "A", count: 600) + "-1") == nil)
    }

    @Test func matchesOnlyTheSamePath() {
        #expect(WorkspaceLinkID.matches(id, path: Self.path))
        #expect(!WorkspaceLinkID.matches(id, path: "/tmp/other/Мой проект"))
    }

    @Test func matchesDecomposedUnicode() {
        let decomposed = "Й".decomposedStringWithCanonicalMapping
        let precomposed = "Й"
        let link = TaskerLink(workspace: decomposed + "-" + String(WorkspaceLinkID.make(path: "/tmp/" + precomposed).suffix(WorkspaceLinkID.hashLength)), target: .workspace)
        #expect(WorkspaceLinkID.matches(link.workspace, path: "/tmp/" + precomposed))
    }
}

private struct FakeSource: LinkSource {
    var tasksByProject: [UUID: [(id: UUID, title: String)]] = [:]
    var failing: Set<UUID> = []

    func tasks(project: UUID, reference: String) async throws -> [(id: UUID, title: String)] {
        if failing.contains(project) { throw URLError(.cannotConnectToHost) }
        return tasksByProject[project] ?? []
    }

    func task(project: UUID, id: UUID) async throws -> String? {
        tasksByProject[project]?.first { $0.id == id }?.title
    }

    func series(project: UUID, prefix: String) async throws -> (id: UUID, name: String)? { nil }
    func board(project: UUID, id: UUID) async throws -> String? { nil }
}

@Suite struct LinkResolverTests {
    let first = LinkProject(id: UUID(), name: "One")
    let second = LinkProject(id: UUID(), name: "Two")

    @Test func singleMatchResolves() async {
        let task = UUID()
        let source = FakeSource(tasksByProject: [first.id: [(task, "A")]])
        let result = await LinkResolver.resolve(.task(reference: "TSK-1"), projects: [first, second], source: source)
        #expect(result == .found([LinkCandidate(project: first, title: "A", destination: .task(task))]))
    }

    @Test func numberConflictReturnsBothTasks() async {
        let a = UUID(), b = UUID()
        let source = FakeSource(tasksByProject: [first.id: [(a, "A"), (b, "B")]])
        guard case .found(let candidates) = await LinkResolver.resolve(.task(reference: "TSK-1"), projects: [first], source: source) else {
            Issue.record("expected candidates")
            return
        }
        #expect(candidates.map(\.destination) == [.task(a), .task(b)])
    }

    @Test func sameReferenceInTwoProjectsIsAmbiguous() async {
        let source = FakeSource(tasksByProject: [first.id: [(UUID(), "A")], second.id: [(UUID(), "B")]])
        guard case .found(let candidates) = await LinkResolver.resolve(.task(reference: "TSK-1"), projects: [first, second], source: source) else {
            Issue.record("expected candidates")
            return
        }
        #expect(candidates.map(\.project) == [first, second])
    }

    @Test func nothingFound() async {
        let result = await LinkResolver.resolve(.task(reference: "TSK-1"), projects: [first], source: FakeSource())
        #expect(result == .notFound)
    }

    @Test func failureOnlyWhenNothingFound() async {
        let task = UUID()
        let source = FakeSource(tasksByProject: [second.id: [(task, "B")]], failing: [first.id])
        let result = await LinkResolver.resolve(.task(reference: "TSK-1"), projects: [first, second], source: source)
        #expect(result == .found([LinkCandidate(project: second, title: "B", destination: .task(task))]))
        let failed = await LinkResolver.resolve(.task(reference: "TSK-1"), projects: [first], source: source)
        if case .failed = failed {} else { Issue.record("expected failure") }
    }

    @Test func taskByIDIsLookedUpPerProject() async {
        let task = UUID()
        let source = FakeSource(tasksByProject: [second.id: [(task, "B")]])
        let result = await LinkResolver.resolve(.taskID(task), projects: [first, second], source: source)
        #expect(result == .found([LinkCandidate(project: second, title: "B", destination: .task(task))]))
    }
}
