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
@Suite struct WorkspaceLayoutTests {
    private func paths(_ window: WorkspaceWindow?) -> [String] {
        window?.tabs.map(\.path) ?? []
    }

    private func selectedPath(_ layout: WorkspaceLayout, _ window: UUID) -> String? {
        layout.window(window)?.selectedTab?.path
    }

    private func tabId(_ layout: WorkspaceLayout, _ window: UUID, _ path: String) -> UUID {
        layout.window(window)!.tabs.first { $0.path == path }!.id
    }

    @Test func seedsOnlyOnce() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.seed(from: ["/a", "/b", "/a/"])
        #expect(paths(layout.window(window)) == ["/a", "/b"])
        #expect(selectedPath(layout, window) == "/a")
        layout.close(tabId(layout, window, "/a"))
        layout.seed(from: ["/a", "/b", "/c"])
        #expect(paths(layout.window(window)) == ["/b"])
    }

    @Test func opensSelectsAndClosesNeighbours() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.open("/a", in: window)
        layout.open("/b", in: window)
        layout.open("/c", in: window)
        #expect(selectedPath(layout, window) == "/c")
        layout.open("/a", in: window)
        #expect(paths(layout.window(window)) == ["/a", "/b", "/c"])
        #expect(selectedPath(layout, window) == "/a")

        layout.select(tabId(layout, window, "/b"))
        layout.close(tabId(layout, window, "/b"))
        #expect(selectedPath(layout, window) == "/c")
        layout.close(tabId(layout, window, "/c"))
        #expect(selectedPath(layout, window) == "/a")
        layout.close(tabId(layout, window, "/a"))
        #expect(layout.window(window)?.selected == nil)
        #expect(layout.window(window)?.tabs.isEmpty == true)
    }

    @Test func ignoresSelectionOfUnknownTab() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.open("/a", in: window)
        layout.select(UUID())
        #expect(selectedPath(layout, window) == "/a")
    }

    @Test func remembersRecentsWithoutOpenOnes() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.open("/a", in: window)
        layout.open("/b", in: window)
        layout.close(tabId(layout, window, "/a"))
        #expect(layout.recents == ["/a"])
        layout.open("/a", in: window)
        #expect(layout.recents.isEmpty)
        layout.close(tabId(layout, window, "/b"))
        layout.forget(recent: "/b")
        #expect(layout.recents.isEmpty)
    }

    @Test func limitsRecents() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        for index in 0..<12 {
            layout.open("/w\(index)", in: window)
            layout.close(tabId(layout, window, "/w\(index)"))
        }
        #expect(layout.recents.count == WorkspaceLayout.maxRecents)
        #expect(layout.recents.first == "/w11")
    }

    @Test func sameWorkspaceInTwoTabsKeepsProjectsApart() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.open("/a", in: window)
        let first = layout.window(window)!.selected!
        let second = layout.newTab("/a", in: window)!
        let one = UUID()
        let two = UUID()
        layout.setProject(one, tab: first)
        layout.setProject(two, tab: second)
        #expect(paths(layout.window(window)) == ["/a", "/a"])
        #expect(layout.tab(first)?.project == one)
        #expect(layout.tab(second)?.project == two)
        layout.close(first)
        #expect(layout.openPaths == ["/a"])
        #expect(layout.recents.isEmpty)
    }

    @Test func duplicateCopiesProjectAndSectionButNotView() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.open("/a", in: window)
        let first = layout.window(window)!.selected!
        let project = UUID()
        layout.setProject(project, tab: first)
        layout.setSection(.boards, tab: first)
        let copy = layout.duplicate(first)!
        #expect(copy != first)
        #expect(layout.tab(copy)?.project == project)
        #expect(layout.tab(copy)?.section == AppSection.boards.rawValue)
        #expect(layout.window(window)?.selected == copy)
    }

    @Test func detachesAndReturnsTab() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        layout.open("/a", in: window)
        layout.open("/b", in: window)
        let moving = tabId(layout, window, "/b")

        let second = layout.detach(moving)!
        #expect(paths(layout.window(window)) == ["/a"])
        #expect(paths(layout.window(second)) == ["/b"])
        #expect(selectedPath(layout, window) == "/a")
        #expect(selectedPath(layout, second) == "/b")
        #expect(layout.detach(moving) == nil)

        layout.move(moving, to: window, at: 0)
        #expect(paths(layout.window(window)) == ["/b", "/a"])
        #expect(selectedPath(layout, window) == "/b")
        #expect(layout.window(second) == nil)
    }

    @Test func movesTabWithinWindowToDropPosition() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        for path in ["/a", "/b", "/c"] { layout.open(path, in: window) }
        layout.move(tabId(layout, window, "/a"), to: window, at: 2)
        #expect(paths(layout.window(window)) == ["/b", "/a", "/c"])
        layout.move(tabId(layout, window, "/c"), to: window, at: 0)
        #expect(paths(layout.window(window)) == ["/c", "/b", "/a"])
        layout.move(tabId(layout, window, "/c"), to: window, at: 3)
        #expect(paths(layout.window(window)) == ["/b", "/a", "/c"])
    }

    @Test func closingLastTabClosesWindowOnlyWhenOthersExist() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let first = layout.claimWindow(requested: nil)
        layout.open("/a", in: first)
        layout.close(tabId(layout, first, "/a"))
        #expect(layout.window(first)?.tabs.isEmpty == true)

        layout.open("/a", in: first)
        let second = layout.createWindow()
        layout.open("/b", in: second)
        layout.close(tabId(layout, second, "/b"))
        #expect(layout.window(second) == nil)
        #expect(layout.windows.count == 1)
    }

    @Test func closingWindowDropsItsTabsUnlessItIsTheLastOne() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let first = layout.claimWindow(requested: nil)
        layout.open("/a", in: first)
        let second = layout.createWindow()
        layout.open("/b", in: second)

        layout.windowClosed(second)
        #expect(layout.window(second) == nil)
        #expect(layout.recents == ["/b"])

        layout.windowClosed(first)
        #expect(paths(layout.window(first)) == ["/a"])
        #expect(layout.createWindow() == first)
        layout.windowClosed(first)
        #expect(layout.claimWindow(requested: nil) == first)
    }

    @Test func quittingKeepsAllWindows() {
        let defaults = isolatedDefaults()
        let layout = WorkspaceLayout(defaults: defaults)
        let first = layout.claimWindow(requested: nil)
        layout.open("/a", in: first)
        let second = layout.createWindow()
        layout.open("/b", in: second)
        layout.prepareToTerminate()
        layout.windowClosed(first)
        layout.windowClosed(second)
        #expect(WorkspaceLayout(defaults: defaults).windows.count == 2)
    }

    @Test func mergesAllWindows() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let first = layout.claimWindow(requested: nil)
        layout.open("/a", in: first)
        let second = layout.createWindow()
        layout.open("/b", in: second)
        let third = layout.createWindow()
        layout.open("/c", in: third)
        layout.mergeAll(into: second)
        #expect(layout.windows.map(\.id) == [second])
        #expect(Set(paths(layout.window(second))) == ["/a", "/b", "/c"])
        #expect(selectedPath(layout, second) == "/b")
    }

    @Test func cyclesTabs() {
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        for path in ["/a", "/b", "/c"] { layout.open(path, in: window) }
        layout.selectAdjacent(in: window, offset: 1)
        #expect(selectedPath(layout, window) == "/a")
        layout.selectAdjacent(in: window, offset: -1)
        #expect(selectedPath(layout, window) == "/c")
    }

    @Test func restoresWindowsAndOpensActiveLast() {
        let defaults = isolatedDefaults()
        let first = WorkspaceLayout(defaults: defaults)
        let one = first.claimWindow(requested: nil)
        first.open("/a", in: one)
        first.open("/b", in: one)
        let two = first.createWindow()
        first.open("/a", in: two)
        let project = UUID()
        first.setProject(project, tab: first.window(two)!.selected!)
        first.setSection(.boards, tab: first.window(two)!.selected!)
        first.setSidebar(false, in: two)
        first.setActive(one)
        first.flush()

        let second = WorkspaceLayout(defaults: defaults)
        #expect(second.windows.map(\.id) == [one, two])
        let claimed = second.claimWindow(requested: nil)
        #expect(claimed == two)
        #expect(second.takePendingWindows() == [one])
        #expect(second.takePendingWindows().isEmpty)
        #expect(paths(second.window(one)) == ["/a", "/b"])
        #expect(selectedPath(second, one) == "/b")
        #expect(second.window(two)?.selectedTab?.project == project)
        #expect(second.window(two)?.selectedTab?.section == AppSection.boards.rawValue)
        #expect(second.window(two)?.sidebarVisible == false)
    }

    @Test func migratesOldTabs() throws {
        let defaults = isolatedDefaults()
        let project = UUID()
        let legacy = LegacyWorkspaceTabsState(initialized: true, open: ["/a", "/b"], selected: "/b", recents: ["/c"], projects: ["/b": project.uuidString])
        defaults.set(try JSONEncoder().encode(legacy), forKey: WorkspaceLayout.legacyStorageKey)
        defaults.set(AppSection.boards.rawValue, forKey: AppSection.storageKey)
        defaults.set(false, forKey: SidebarState.storageKey)

        let layout = WorkspaceLayout(defaults: defaults)
        let window = try #require(layout.windows.first)
        #expect(layout.windows.count == 1)
        #expect(paths(window) == ["/a", "/b"])
        #expect(window.selectedTab?.path == "/b")
        #expect(window.selectedTab?.project == project)
        #expect(window.selectedTab?.section == AppSection.boards.rawValue)
        #expect(window.sidebarVisible == false)
        #expect(window.frameName == WorkspaceLayout.legacyFrameName)
        #expect(layout.recents == ["/c"])
    }
}

@Suite struct WorkspaceTabDropPositionTests {
    @Test func picksSlotByMidpoints() {
        #expect(WorkspaceTabDropPosition.resolve(x: 10, midpoints: [50, 150, 250], gap: 96, current: nil) == 0)
        #expect(WorkspaceTabDropPosition.resolve(x: 100, midpoints: [50, 150, 250], gap: 96, current: nil) == 1)
        #expect(WorkspaceTabDropPosition.resolve(x: 300, midpoints: [50, 150, 250], gap: 96, current: nil) == 3)
    }

    @Test func ignoresTheOpenGapSoTheSlotDoesNotJump() {
        let shifted: [CGFloat] = [50, 246, 346]
        #expect(WorkspaceTabDropPosition.resolve(x: 100, midpoints: shifted, gap: 96, current: 1) == 1)
        #expect(WorkspaceTabDropPosition.resolve(x: 200, midpoints: shifted, gap: 96, current: 1) == 2)
    }
}

@MainActor
@Suite struct WorkspaceSessionTests {
    private let status = DaemonStatus(pid: 1, port: 5719, workers: [], workspaces: [
        DaemonWorkspace(path: "/w/a", state: "open", key: "a", error: nil),
        DaemonWorkspace(path: "/w/b", state: "open", key: "b", error: nil)
    ])

    private func fixture() -> (WorkspaceLayout, WorkspaceSessions, UUID, UserDefaults) {
        let defaults = isolatedDefaults()
        let layout = WorkspaceLayout(defaults: defaults)
        let sessions = WorkspaceSessions(layout: layout, defaults: defaults)
        return (layout, sessions, layout.claimWindow(requested: nil), defaults)
    }

    @Test func oneSubscriptionPerWorkspace() {
        let (layout, sessions, window, _) = fixture()
        layout.open("/w/a", in: window)
        let first = layout.window(window)!.selectedTab!
        let second = layout.tab(layout.newTab("/w/a", in: window)!)!
        layout.newTab("/w/b", in: window)
        let other = layout.createWindow()
        let third = layout.tab(layout.newTab("/w/a", in: other)!)!
        sessions.sync(with: .running(status))

        #expect(sessions.session(for: first).live === sessions.session(for: second).live)
        #expect(sessions.session(for: first).live === sessions.session(for: third).live)
        #expect(sessions.session(for: first).live.address?.key == "a")
        #expect(Set(sessions.livePaths) == ["/w/a", "/w/b"])
        #expect(sessions.session(for: first).id != sessions.session(for: second).id)
        #expect(sessions.session(for: first).navigator !== sessions.session(for: second).navigator)
        #expect(sessions.session(for: first).settings !== sessions.session(for: second).settings)
        #expect(sessions.session(for: first).creation !== sessions.session(for: second).creation)
        sessions.session(for: first).live.stop()
        sessions.live(for: "/w/b").stop()
    }

    @Test func closingLastTabOfWorkspaceDropsItsSubscription() {
        let (layout, sessions, window, _) = fixture()
        layout.open("/w/a", in: window)
        layout.open("/w/b", in: window)
        sessions.sync(with: .running(status))
        let stale = layout.window(window)!.tabs.first { $0.path == "/w/a" }!
        layout.close(stale.id)
        sessions.sync(with: .running(status))
        #expect(sessions.livePaths == ["/w/b"])
        sessions.live(for: "/w/b").stop()
    }

    @Test func movingTabKeepsItsSession() {
        let (layout, sessions, window, _) = fixture()
        layout.open("/w/a", in: window)
        layout.newTab("/w/a", in: window)
        let tab = layout.window(window)!.selectedTab!
        let session = sessions.session(for: tab)
        let card = UUID()
        session.navigator.open(card)
        _ = layout.detach(tab.id)
        #expect(sessions.session(for: layout.tab(tab.id)!) === session)
        #expect(session.navigator.card == card)
    }

    @Test func viewStateSurvivesRestart() {
        let (layout, sessions, window, defaults) = fixture()
        layout.open("/w/a", in: window)
        let session = sessions.session(for: layout.window(window)!.selectedTab!)
        let parent = UUID()
        let card = UUID()
        session.navigator.open(parent)
        session.navigator.open(card)
        layout.flush()

        let restored = WorkspaceLayout(defaults: defaults)
        let sessionsAfter = WorkspaceSessions(layout: restored, defaults: defaults)
        let after = sessionsAfter.session(for: restored.window(window)!.selectedTab!)
        #expect(after.navigator.card == card)
        #expect(after.navigator.history == [parent])
    }

    @Test func changingProjectResetsTabButFirstResolveDoesNot() {
        let (layout, sessions, window, _) = fixture()
        layout.open("/w/a", in: window)
        let session = sessions.session(for: layout.window(window)!.selectedTab!)
        let card = UUID()
        session.navigator.open(card)
        session.noteProject(UUID())
        #expect(session.navigator.card == card)
        session.noteProject(UUID())
        #expect(session.navigator.card == nil)
    }

    @Test func sectionLivesOnTheTab() {
        let (layout, sessions, window, _) = fixture()
        layout.open("/w/a", in: window)
        let first = sessions.session(for: layout.window(window)!.selectedTab!)
        let second = sessions.session(for: layout.tab(layout.newTab("/w/a", in: window)!)!)
        first.setSection(.boards)
        #expect(first.section == .boards)
        #expect(second.section == .default)
    }

    @Test func noticesStayInTheirTab() {
        let (layout, sessions, window, _) = fixture()
        layout.open("/w/a", in: window)
        let first = sessions.session(for: layout.window(window)!.selectedTab!)
        let second = sessions.session(for: layout.tab(layout.newTab("/w/a", in: window)!)!)
        first.notice("Готово")
        #expect(first.toasts.toasts.count == 1)
        #expect(second.toasts.toasts.isEmpty)
        #expect(first.live.toasts.isEmpty)
    }

    @Test func listSettingsAreIndependentAndSeedNewTabs() {
        let defaults = isolatedDefaults()
        let project = UUID()
        let first = TaskListSettings(defaults: defaults, tab: UUID())
        let secondTab = UUID()
        let second = TaskListSettings(defaults: defaults, tab: secondTab)
        _ = second.preferences(for: project)
        first.update(project) { $0.mode = .flat }
        second.update(project) { $0.mode = .tree }
        first.update(project) { $0.sort = [TaskSortKey(key: "title", descending: false)] }

        #expect(first.preferences(for: project).mode == .flat)
        #expect(second.preferences(for: project).mode == .tree)
        #expect(second.preferences(for: project).sort.isEmpty)

        let reopened = TaskListSettings(defaults: defaults, tab: secondTab)
        #expect(reopened.preferences(for: project).mode == .tree)

        let fresh = TaskListSettings(defaults: defaults, tab: UUID())
        #expect(fresh.preferences(for: project).mode == .flat)
        #expect(fresh.preferences(for: project).sort.first?.key == "title")

        reopened.removeTabData()
        #expect(TaskListSettings(defaults: defaults, tab: secondTab).preferences(for: project).mode == .flat)
    }
}

@MainActor
@Suite struct WorkspaceControllerTests {
    @Test func offersClosedAndDaemonOnlyFolders() throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        let controller = WorkspaceController(layout: layout, settings: WorkspaceSettingsFile(directory: directory))
        layout.open("/a", in: window)
        layout.open("/b", in: window)
        layout.close(layout.window(window)!.tabs.first { $0.path == "/a" }!.id)
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
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        let settings = WorkspaceSettingsFile(directory: directory)
        let controller = WorkspaceController(layout: layout, settings: settings)

        #expect(controller.needsData(folder.path))
        await controller.add(folder.path, createData: true, window: window)
        #expect(!controller.needsData(folder.path))
        #expect(controller.failure == nil)
        #expect(layout.window(window)?.selectedTab?.path == WorkspacePath.canonical(folder.path))
        #expect(try settings.workspacePaths() == [WorkspacePath.canonical(folder.path)])
    }

    @Test func reportsBrokenSettingsWithoutOpeningTheTab() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let settings = WorkspaceSettingsFile(directory: directory)
        try Data("nope".utf8).write(to: settings.file)
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        let controller = WorkspaceController(layout: layout, settings: settings)

        await controller.add("/tmp", createData: false, window: window)
        #expect(controller.failure != nil)
        #expect(layout.openPaths.isEmpty)
    }

    @Test func forgetsMissingRecentFolder() async throws {
        let directory = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let layout = WorkspaceLayout(defaults: isolatedDefaults())
        let window = layout.claimWindow(requested: nil)
        let controller = WorkspaceController(layout: layout, settings: WorkspaceSettingsFile(directory: directory))
        layout.open("/definitely/missing", in: window)
        layout.close(layout.window(window)!.selected!)
        await controller.reopen("/definitely/missing", window: window)
        #expect(layout.recents.isEmpty)
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
