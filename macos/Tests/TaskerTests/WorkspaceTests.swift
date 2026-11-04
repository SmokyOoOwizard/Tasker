import Foundation
import Testing
import TaskerAPI
@testable import Tasker

private func temporaryDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appending(path: "tasker-ws-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url.resolvingSymlinksInPath()
}

private func isolatedDefaults() -> UserDefaults {
    UserDefaults(suiteName: "tasker-tests-\(UUID().uuidString)")!
}

@Suite struct WorkspacePathTests {
    @Test func stripsDataFolderAndResolvesSymlinks() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let project = root.appending(path: "Проект")
        try FileManager.default.createDirectory(at: project.appending(path: ".tasker"), withIntermediateDirectories: true)
        let link = root.appending(path: "link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: project)

        let canonical = WorkspacePath.canonical(project.path)
        #expect(canonical == WorkspacePath.canonical(project.appending(path: ".tasker").path))
        #expect(canonical == WorkspacePath.canonical(link.path))
        #expect(WorkspacePath.same(project.path + "/", link.path))
        #expect(WorkspacePath.name(canonical) == "Проект")
        #expect(WorkspacePath.hasData(canonical))
        #expect(!WorkspacePath.hasData(root.path))
    }

    @Test func keepsMissingPathsAsTheyAre() {
        #expect(WorkspacePath.canonical("/definitely/missing/Folder/") == "/definitely/missing/Folder")
        #expect(WorkspacePath.name("/") == "/")
    }
}

@Suite struct WorkspaceSettingsFileTests {
    @Test func createsFileWithTheWorkspace() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let folder = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let settings = WorkspaceSettingsFile(directory: directory)

        #expect(try settings.workspacePaths().isEmpty)
        #expect(try settings.add(folder.path))
        #expect(try settings.workspacePaths() == [WorkspacePath.canonical(folder.path)])

        let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: settings.file)) as? [String: Any])
        let entry = try #require(((root["mcp"] as? [String: Any])?["workspaces"] as? [[String: Any]])?.first)
        #expect(entry["kind"] as? String == "files")
    }

    @Test func keepsOtherSettingsAndSkipsDuplicates() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let folder = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: folder) }
        let settings = WorkspaceSettingsFile(directory: directory)
        let existing = """
        {"userName": "Иван", "mcp": {"port": 6001, "workspaces": [{"kind": "sqlite", "path": "/data/x.db"}]}, "extra": [1, 2]}
        """
        try Data(existing.utf8).write(to: settings.file)

        #expect(try settings.add(folder.path))
        #expect(try !settings.add(folder.path + "/"))

        let root = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: settings.file)) as? [String: Any])
        #expect(root["userName"] as? String == "Иван")
        #expect(root["extra"] as? [Int] == [1, 2])
        let mcp = try #require(root["mcp"] as? [String: Any])
        #expect(mcp["port"] as? Int == 6001)
        #expect((mcp["workspaces"] as? [[String: Any]])?.count == 2)
    }

    @Test func refusesToOverwriteBrokenFile() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let settings = WorkspaceSettingsFile(directory: directory)
        try Data("{ broken".utf8).write(to: settings.file)

        #expect(throws: WorkspaceSettingsError.self) { try settings.add("/tmp") }
        #expect(try String(contentsOf: settings.file, encoding: .utf8) == "{ broken")
    }

    @Test func concurrentAddsAreAllKept() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let settings = WorkspaceSettingsFile(directory: directory)
        let folders = try (0..<8).map { _ in try temporaryDirectory() }
        defer { folders.forEach { try? FileManager.default.removeItem(at: $0) } }

        try await withThrowingTaskGroup(of: Void.self) { group in
            for folder in folders {
                group.addTask { try settings.add(folder.path) }
            }
            try await group.waitForAll()
        }
        #expect(try Set(settings.workspacePaths()) == Set(folders.map { WorkspacePath.canonical($0.path) }))
    }
}

@MainActor
@Suite struct WorkspaceTabsTests {
    @Test func seedsOnlyOnce() {
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        tabs.seed(from: ["/a", "/b", "/a/"])
        #expect(tabs.open == ["/a", "/b"])
        #expect(tabs.selected == "/a")
        tabs.close("/a")
        tabs.seed(from: ["/a", "/b", "/c"])
        #expect(tabs.open == ["/b"])
    }

    @Test func opensSelectsAndClosesNeighbours() {
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        tabs.open("/a")
        tabs.open("/b")
        tabs.open("/c")
        #expect(tabs.selected == "/c")
        tabs.open("/a")
        #expect(tabs.open == ["/a", "/b", "/c"])
        #expect(tabs.selected == "/a")

        tabs.select("/b")
        tabs.close("/b")
        #expect(tabs.selected == "/c")
        tabs.close("/c")
        #expect(tabs.selected == "/a")
        tabs.close("/a")
        #expect(tabs.selected == nil)
        #expect(tabs.open.isEmpty)
    }

    @Test func ignoresSelectionOfClosedTab() {
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        tabs.open("/a")
        tabs.select("/zzz")
        #expect(tabs.selected == "/a")
    }

    @Test func remembersRecentsWithoutOpenOnes() {
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        tabs.open("/a")
        tabs.open("/b")
        tabs.close("/a")
        #expect(tabs.recents == ["/a"])
        tabs.open("/a")
        #expect(tabs.recents.isEmpty)
        tabs.close("/b")
        tabs.forget(recent: "/b")
        #expect(tabs.recents.isEmpty)
    }

    @Test func limitsRecents() {
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        for index in 0..<12 {
            tabs.open("/w\(index)")
            tabs.close("/w\(index)")
        }
        #expect(tabs.recents.count == WorkspaceTabs.maxRecents)
        #expect(tabs.recents.first == "/w11")
    }

    @Test func restoresStateAfterRestart() {
        let defaults = isolatedDefaults()
        let first = WorkspaceTabs(defaults: defaults)
        let project = UUID()
        first.open("/a")
        first.open("/b")
        first.setProject(project, in: "/b")
        first.close("/a")

        let second = WorkspaceTabs(defaults: defaults)
        #expect(second.open == ["/b"])
        #expect(second.selected == "/b")
        #expect(second.recents == ["/a"])
        #expect(second.projectId(in: "/b") == project)
        #expect(second.projectId(in: "/a") == nil)
    }
}

@MainActor
@Suite struct WorkspaceControllerTests {
    @Test func offersClosedAndDaemonOnlyFolders() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        let controller = WorkspaceController(tabs: tabs, settings: WorkspaceSettingsFile(directory: directory))
        tabs.open("/a")
        tabs.open("/b")
        tabs.close("/a")
        let status = DaemonStatus(pid: 1, port: 1, workers: [], workspaces: [
            DaemonWorkspace(path: "/a", state: "open", key: "a", error: nil),
            DaemonWorkspace(path: "/b", state: "open", key: "b", error: nil),
            DaemonWorkspace(path: "/c", state: "open", key: "c", error: nil)
        ])
        #expect(controller.reopenable(in: status) == ["/a", "/c"])
        #expect(controller.reopenable(in: nil) == ["/a"])
    }

    @Test func addsFolderToSettingsAndOpensTab() async throws {
        let directory = try temporaryDirectory()
        let folder = try temporaryDirectory()
        defer {
            try? FileManager.default.removeItem(at: directory)
            try? FileManager.default.removeItem(at: folder)
        }
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        let settings = WorkspaceSettingsFile(directory: directory)
        let controller = WorkspaceController(tabs: tabs, settings: settings)

        #expect(controller.needsData(folder.path))
        await controller.add(folder.path, createData: true)
        #expect(!controller.needsData(folder.path))
        #expect(controller.failure == nil)
        #expect(tabs.selected == WorkspacePath.canonical(folder.path))
        #expect(try settings.workspacePaths() == [WorkspacePath.canonical(folder.path)])
    }

    @Test func reportsBrokenSettingsWithoutOpeningTheTab() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let settings = WorkspaceSettingsFile(directory: directory)
        try Data("nope".utf8).write(to: settings.file)
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        let controller = WorkspaceController(tabs: tabs, settings: settings)

        await controller.add("/tmp", createData: false)
        #expect(controller.failure != nil)
        #expect(tabs.open.isEmpty)
    }

    @Test func forgetsMissingRecentFolder() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let tabs = WorkspaceTabs(defaults: isolatedDefaults())
        let controller = WorkspaceController(tabs: tabs, settings: WorkspaceSettingsFile(directory: directory))
        tabs.open("/definitely/missing")
        tabs.close("/definitely/missing")
        await controller.reopen("/definitely/missing")
        #expect(tabs.recents.isEmpty)
        #expect(controller.failure == "/definitely/missing")
    }
}

@Suite struct WorkspaceStatusTests {
    private let status = DaemonStatus(pid: 1, port: 5719, workers: [], workspaces: [
        DaemonWorkspace(path: "/w/open", state: "open", key: "open-1", error: nil),
        DaemonWorkspace(path: "/w/opening", state: "opening", key: nil, error: nil),
        DaemonWorkspace(path: "/w/failed", state: "failed", key: nil, error: "нет диска")
    ])

    @Test func mapsDaemonStates() {
        #expect(WorkspaceTabStatus(path: "/w/open", in: status) == .open(key: "open-1"))
        #expect(WorkspaceTabStatus(path: "/w/opening", in: status) == .opening)
        #expect(WorkspaceTabStatus(path: "/w/failed", in: status) == .failed("нет диска"))
        #expect(WorkspaceTabStatus(path: "/w/other", in: status) == .absent)
        #expect(WorkspaceTabStatus(path: "/w/open", in: nil) == .absent)
    }

    @Test func addressFollowsTheChosenWorkspace() {
        #expect(LiveWorkspaceAddress(status: status, path: "/w/open") == LiveWorkspaceAddress(port: 5719, key: "open-1", name: "open"))
        #expect(LiveWorkspaceAddress(status: status, path: "/w/opening") == nil)
        #expect(LiveWorkspaceAddress(status: status, path: "/w/other") == nil)
        #expect(LiveWorkspaceAddress(status: status)?.key == "open-1")
    }
}

@MainActor
@Suite struct ProjectFlowTests {
    private func project(_ name: String) -> ProjectFields {
        ProjectFields(id: UUID(), name: name, createdAt: Date(), version: "1")
    }

    @Test func choosesStoredProjectOrFirst() {
        let first = project("Один")
        let second = project("Два")
        #expect(ProjectChoice.resolve([first, second], stored: second.id)?.id == second.id)
        #expect(ProjectChoice.resolve([first, second], stored: UUID())?.id == first.id)
        #expect(ProjectChoice.resolve([first, second], stored: nil)?.id == first.id)
        #expect(ProjectChoice.resolve([], stored: nil) == nil)
    }

    @Test func validatesProjectNames() {
        #expect(ProjectNameCheck("  ", existing: []) == .empty)
        #expect(ProjectNameCheck(" Сайт ", existing: ["сайт"]) == .duplicate)
        #expect(ProjectNameCheck(" Сайт ", existing: ["Tasker"]) == .ok("Сайт"))
    }

    @Test func onboardingFollowsProjectStats() {
        func stats(tasks: Int = 0, statuses: Int = 0, sets: Int = 0, types: Int = 0, boards: Int = 0) -> ProjectQuery.Data.Project.Stats {
            .init(tasks: tasks, boards: boards, statuses: statuses, statusSets: sets, taskTypes: types, series: 0, linkTypes: 0)
        }
        #expect(OnboardingStep.isEmpty(stats()))
        #expect(OnboardingStep.completed(stats(statuses: 3, sets: 1)) == 2)
        #expect(OnboardingStep.isEmpty(stats(statuses: 3, sets: 1, types: 1)))
        #expect(!OnboardingStep.isEmpty(stats(statuses: 3, sets: 1, types: 1, boards: 1)))
        #expect(!OnboardingStep.isEmpty(stats(tasks: 5)))
        #expect(OnboardingStep.statuses.isUnlocked(stats()))
        #expect(!OnboardingStep.statusSet.isUnlocked(stats()))
        #expect(OnboardingStep.statusSet.isUnlocked(stats(statuses: 1)))
        #expect(!OnboardingStep.taskType.isUnlocked(stats(statuses: 1)))
        #expect(OnboardingStep.board.isUnlocked(stats(statuses: 1, sets: 1)))
    }

    @Test func localizesOnboardingTexts() {
        let ru = Localizer(language: .ru)
        let en = Localizer(language: .en)
        #expect(ru.string(.onboardingHeading, "Сайт") == "Проект «Сайт» пока пуст")
        #expect(en.string(.onboardingProgress, "2") == "Four steps and you can create tasks. 2 of 4.")
        #expect(OnboardingNames(ru).doing == "В работе")
        #expect(OnboardingNames(en).board == "Main")
        #expect(LiveToastMessage.notice("Готово").text(ru) == "Готово")
    }
}
