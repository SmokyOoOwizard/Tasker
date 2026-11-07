import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubPaletteSource: PaletteSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _searches: [String] = []
    var tasks: [TaskLinkedBrief]
    var hits: [String: [PaletteTaskHit]]
    let vocab: TaskListVocabulary
    var delay: Duration = .zero

    init(vocabulary: TaskListVocabulary, tasks: [TaskLinkedBrief] = [], hits: [String: [PaletteTaskHit]] = [:]) {
        vocab = vocabulary
        self.tasks = tasks
        self.hits = hits
    }

    var searches: [String] { lock.withLock { _searches } }

    func vocabulary(project: UUID) async throws -> TaskListVocabulary { vocab }

    func search(project: UUID, text: String, limit: Int) async throws -> [PaletteTaskHit] {
        lock.withLock { _searches.append(text) }
        if delay > .zero { try await Task.sleep(for: delay) }
        if let exact = hits[text] { return exact }
        let folded = PaletteMatcher.normalize(text)
        return tasks.filter { PaletteMatcher.normalize($0.title).contains(folded) }.prefix(limit).map {
            PaletteTaskHit(brief: $0, score: 0.7, isExactNumber: false, snippet: nil)
        }
    }
}

@MainActor
struct PaletteTests {
    let project = UUID()
    let series = UUID()
    let otherSeries = UUID()
    let todo = UUID()
    let done = UUID()
    let set = UUID()
    let type = UUID()
    let l = Localizer(language: .en)

    var vocabulary: TaskListVocabulary {
        var value = TaskListVocabulary()
        value.statuses = [.init(id: todo, name: "Todo", color: "#8993a4"), .init(id: done, name: "Done", color: "#36b37e")]
        value.statusSets = [set: [todo, done]]
        value.types = [type: .init(name: "Task", statusSetId: set)]
        value.typeOrder = [type]
        value.prefixes = [series: "TSK", otherSeries: "PRJ"]
        value.seriesNames = [series: "Tasks", otherSeries: "Projects"]
        value.seriesOrder = [series, otherSeries]
        return value
    }

    func brief(_ title: String, number: Int, in seriesId: UUID? = nil) -> TaskLinkedBrief {
        TaskLinkedBrief(id: UUID(), title: title, statusId: todo, typeId: type, seriesNumbers: [TaskSeriesRef(seriesId: seriesId ?? series, number: number)])
    }

    final class Recorder {
        var calls: [String] = []
        var closed = 0
        var opened: [UUID] = []
    }

    func actions(_ recorder: Recorder) -> PaletteActions {
        PaletteActions(
            goTo: { recorder.calls.append("go:\($0.rawValue)") },
            createTask: { recorder.calls.append("create") },
            createBoard: { recorder.calls.append("create-board") },
            editBoard: { recorder.calls.append("edit-board:\($0)") },
            showSeries: { recorder.calls.append("series:\($0)") },
            openSettings: { recorder.calls.append("settings:\($0.rawValue)") },
            chooseProject: { recorder.calls.append("project:\($0)") },
            openWorkspace: { recorder.calls.append("workspace:\($0)") },
            openFolder: { recorder.calls.append("folder") },
            setTheme: { recorder.calls.append("theme:\($0.rawValue)") },
            setLanguage: { recorder.calls.append("language:\($0.rawValue)") })
    }

    func context(hasTab: Bool = true, hasProject: Bool = true, projects: [ProjectFields] = [], current: UUID? = nil, theme: AppTheme = .system, dark: Bool = false, language: AppLanguage = .en) -> PaletteContext {
        PaletteContext(
            hasTab: hasTab, hasProject: hasProject, projects: projects, currentProject: current,
            workspaces: [PaletteWorkspace(path: "/tmp/one", current: true), PaletteWorkspace(path: "/tmp/two", current: false)],
            theme: theme, isDark: dark, language: language)
    }

    func model(_ source: StubPaletteSource, recorder: Recorder, context: PaletteContext? = nil) -> PaletteModel {
        let context = context ?? self.context()
        let actions = actions(recorder)
        let l = l
        return PaletteModel(
            localizer: l, source: source, project: project,
            makeCommands: { PaletteCommands.build(context, vocabulary: $0, actions: actions, l: l) },
            openTask: { recorder.opened.append($0) },
            close: { recorder.closed += 1 })
    }

    func settle(_ model: PaletteModel) async {
        for _ in 0..<200 {
            if !model.searching { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    @Test func matcherScoresSubstringsAboveSubsequences() {
        #expect(PaletteMatcher.score("task", in: "Create task") > PaletteMatcher.score("ctk", in: "Create task"))
        #expect(PaletteMatcher.score("zzz", in: "Create task") == 0)
        #expect(PaletteMatcher.score("", in: "anything") > 0)
        #expect(PaletteMatcher.score("созд зад", in: "Создать задачу") > 0)
        #expect(PaletteMatcher.score("ЯБЛОКО", in: "яблоко") > 0)
        #expect(PaletteMatcher.score("task create", in: "Create task") > 0)
    }

    @Test func commandsCoverScreensAndActions() {
        let ids = PaletteCommands.build(context(), vocabulary: vocabulary, actions: actions(Recorder()), l: l).map(\.id)
        #expect(ids.contains("create-task"))
        #expect(ids.contains("create-board"))
        for section in AppSection.allCases { #expect(ids.contains("go:" + section.rawValue)) }
        #expect(ids.contains("series:" + series.uuidString))
        #expect(ids.contains("series:" + otherSeries.uuidString))
        #expect(ids.contains("theme-switch"))
        #expect(ids.contains("open-folder"))
        #expect(ids.contains("workspace:/tmp/two"))
        #expect(!ids.contains("workspace:/tmp/one"))
        #expect(ids.contains("language:ru"))
        #expect(ids.contains("language:system"))
        #expect(!ids.contains("language:en"))
        #expect(!ids.contains("theme-system"))
    }

    @Test func createBoardRunsWhenProjectIsChosen() {
        let recorder = Recorder()
        let command = PaletteCommands.build(context(), vocabulary: vocabulary, actions: actions(recorder), l: l).first { $0.id == "create-board" }
        #expect(command?.isEnabled == true)
        command?.run()
        #expect(recorder.calls == ["create-board"])
        let without = PaletteCommands.build(context(hasProject: false), vocabulary: vocabulary, actions: actions(Recorder()), l: l).first { $0.id == "create-board" }
        #expect(without?.isEnabled == false)
    }

    @Test func existingBoardsCanBeEdited() {
        let board = UUID()
        var value = vocabulary
        value.boardNames = [board: "Main"]
        value.boardOrder = [board]
        let recorder = Recorder()
        let entry = PaletteCommands.build(context(), vocabulary: value, actions: actions(recorder), l: l).first { $0.id == "board:" + board.uuidString }
        #expect(entry?.title == "Edit board: Main")
        entry?.run()
        #expect(recorder.calls == ["edit-board:\(board)"])
    }

    @Test func createTaskNeedsProject() {
        let entries = PaletteCommands.build(context(hasProject: false), vocabulary: vocabulary, actions: actions(Recorder()), l: l)
        #expect(entries.first { $0.id == "create-task" }?.isEnabled == false)
    }

    @Test func emptyWindowOffersOnlyGlobalCommands() {
        let ids = PaletteCommands.build(context(hasTab: false), vocabulary: vocabulary, actions: actions(Recorder()), l: l).map(\.id)
        #expect(!ids.contains("create-task"))
        #expect(!ids.contains { $0.hasPrefix("go:") })
        #expect(ids.contains("theme-switch"))
        #expect(ids.contains("open-folder"))
    }

    @Test func themeSwitchFollowsEffectiveAppearance() {
        let light = PaletteCommands.build(context(dark: false), vocabulary: vocabulary, actions: actions(Recorder()), l: l)
        #expect(light.first { $0.id == "theme-switch" }?.title == "Switch theme: dark")
        let dark = PaletteCommands.build(context(theme: .dark, dark: true), vocabulary: vocabulary, actions: actions(Recorder()), l: l)
        #expect(dark.first { $0.id == "theme-switch" }?.title == "Switch theme: light")
        #expect(dark.contains { $0.id == "theme-system" })
    }

    @Test func projectsExcludeCurrent() {
        let first = ProjectFields(id: UUID(), name: "Alpha", createdAt: .now, version: "1")
        let second = ProjectFields(id: UUID(), name: "Beta", createdAt: .now, version: "1")
        let ids = PaletteCommands.build(context(projects: [first, second], current: first.id), vocabulary: vocabulary, actions: actions(Recorder()), l: l).map(\.id)
        #expect(!ids.contains("project:" + first.id.uuidString))
        #expect(ids.contains("project:" + second.id.uuidString))
    }

    @Test func emptyQueryListsCommandsAndFiltersInstantly() {
        let recorder = Recorder()
        let model = model(StubPaletteSource(vocabulary: vocabulary), recorder: recorder)
        #expect(model.sections.count == 1)
        #expect(model.sections[0].entries.count > 5)
        model.query = "theme"
        #expect(model.entries.allSatisfy { $0.id.hasPrefix("theme") })
        #expect(!model.entries.isEmpty)
        model.query = "qqqqq"
        #expect(model.entries.isEmpty)
        model.stop()
    }

    @Test func runningCommandClosesPaletteAndRunsAction() {
        let recorder = Recorder()
        let model = model(StubPaletteSource(vocabulary: vocabulary), recorder: recorder)
        model.query = "go to: boards"
        model.runSelected()
        #expect(recorder.closed == 1)
        #expect(recorder.calls == ["go:boards"])
        model.stop()
    }

    @Test func disabledCommandDoesNothing() {
        let recorder = Recorder()
        let model = model(StubPaletteSource(vocabulary: vocabulary), recorder: recorder, context: context(hasProject: false))
        model.query = "new board"
        model.runSelected()
        #expect(recorder.closed == 0)
        #expect(recorder.calls.isEmpty)
        model.stop()
    }

    @Test func searchFindsTasksByTitle() async {
        let source = StubPaletteSource(vocabulary: vocabulary, tasks: [brief("Fix login page", number: 7), brief("Write docs", number: 8)])
        let recorder = Recorder()
        let model = model(source, recorder: recorder)
        model.vocabularyLoaded(vocabulary)
        model.query = "login"
        await settle(model)
        let tasks = model.sections.first { $0.id == "results" }
        #expect(tasks?.entries.count == 1)
        #expect(tasks?.entries.first?.title == "Fix login page")
        #expect(tasks?.entries.first?.reference == "TSK-7")
        model.stop()
    }

    @Test func exactNumberOpensTaskFirst() async {
        let target = brief("Palette", number: 133)
        let hit = PaletteTaskHit(brief: target, score: 1, isExactNumber: true, snippet: nil)
        let source = StubPaletteSource(vocabulary: vocabulary, hits: ["133": [hit]])
        let recorder = Recorder()
        let model = model(source, recorder: recorder)
        model.vocabularyLoaded(vocabulary)
        model.query = "133"
        await settle(model)
        #expect(model.entries.first?.reference == "TSK-133")
        model.runSelected()
        #expect(recorder.opened == [target.id])
        #expect(recorder.closed == 1)
        model.stop()
    }

    @Test func commandsAndTasksMixByScore() async {
        let strong = PaletteTaskHit(brief: brief("Theme of the title", number: 1), score: 0.8, isExactNumber: false, snippet: nil)
        let weak = PaletteTaskHit(brief: brief("Mentioned in text", number: 2), score: 0.01, isExactNumber: false, snippet: nil)
        let source = StubPaletteSource(vocabulary: vocabulary, hits: ["theme": [strong, weak]])
        let model = model(source, recorder: Recorder())
        model.vocabularyLoaded(vocabulary)
        model.query = "theme"
        await settle(model)
        #expect(model.sections.count == 1)
        let ids = model.entries.map(\.id)
        let strongIndex = ids.firstIndex(of: "task:" + strong.brief.id.uuidString)
        let weakIndex = ids.firstIndex(of: "task:" + weak.brief.id.uuidString)
        let commandIndex = ids.firstIndex { $0.hasPrefix("theme") }
        #expect(weakIndex == ids.count - 1)
        #expect(strongIndex != nil && commandIndex != nil)
        model.stop()
    }

    @Test func snippetReachesEntry() async {
        let snippet = PaletteSnippet(text: "…редкое слово…", ranges: [1..<7])
        let hit = PaletteTaskHit(brief: brief("Long", number: 3), score: 0.5, isExactNumber: false, snippet: snippet)
        let source = StubPaletteSource(vocabulary: vocabulary, hits: ["редкое": [hit]])
        let model = model(source, recorder: Recorder())
        model.vocabularyLoaded(vocabulary)
        model.query = "редкое"
        await settle(model)
        #expect(model.entries.first?.snippet == snippet)
        #expect(PaletteMetrics.listHeight(model.sections) > PaletteMetrics.rowHeight + PaletteMetrics.sectionHeight)
        model.stop()
    }

    @Test func conflictingNumbersShowAllTasks() async {
        let first = PaletteTaskHit(brief: brief("First", number: 5), score: 1, isExactNumber: true, snippet: nil)
        let second = PaletteTaskHit(brief: brief("Second", number: 5), score: 1, isExactNumber: true, snippet: nil)
        let other = PaletteTaskHit(brief: brief("Other series", number: 5, in: otherSeries), score: 1, isExactNumber: true, snippet: nil)
        let source = StubPaletteSource(vocabulary: vocabulary, hits: ["5": [first, second, other]])
        let model = model(source, recorder: Recorder())
        model.vocabularyLoaded(vocabulary)
        model.query = "5"
        await settle(model)
        let entries = model.entries.filter { $0.id.hasPrefix("task:") }
        #expect(entries.map(\.title) == ["First", "Second", "Other series"])
        #expect(entries.map(\.detail) == ["number conflict", "number conflict", "Todo"])
        model.stop()
    }

    @Test func staleResponseIsDropped() async {
        let source = StubPaletteSource(vocabulary: vocabulary, tasks: [brief("alpha one", number: 1), brief("beta two", number: 2)])
        source.delay = .milliseconds(60)
        let model = model(source, recorder: Recorder())
        model.vocabularyLoaded(vocabulary)
        model.query = "alpha"
        model.query = "beta"
        await settle(model)
        let titles = model.entries.filter { $0.id.hasPrefix("task:") }.map(\.title)
        #expect(titles == ["beta two"])
        model.stop()
    }

    @Test func keyboardSelectionStaysInRange() {
        let model = model(StubPaletteSource(vocabulary: vocabulary), recorder: Recorder())
        model.move(-1)
        #expect(model.selected == 0)
        model.move(2)
        #expect(model.selected == 2)
        model.move(10_000)
        #expect(model.selected == model.entries.count - 1)
        model.stop()
    }

    @Test func queryProviderSectionsComeFirst() {
        struct Provider: PaletteQueryProvider {
            func sections(for text: String) -> [PaletteSection] {
                [PaletteSection(id: "query", title: nil, entries: [PaletteEntry(id: "open-query", title: "Open " + text, icon: .mockup(.list), run: {})])]
            }
        }
        let model = model(StubPaletteSource(vocabulary: vocabulary), recorder: Recorder())
        model.queryProviders = [Provider()]
        model.query = "status: Done"
        #expect(model.sections.first?.id == "query")
        model.stop()
    }

    @Test func rankingOfLargeCommandListIsFast() {
        var entries: [PaletteEntry] = []
        for index in 0..<5_000 {
            entries.append(PaletteEntry(id: "c\(index)", title: "Command number \(index) with a long title", icon: .mockup(.list), run: {}))
        }
        let start = ContinuousClock.now
        let ranked = PaletteMatcher.ranked(entries, query: "number 4999")
        let elapsed = ContinuousClock.now - start
        #expect(ranked.first?.id == "c4999")
        #expect(elapsed < .milliseconds(500))
    }
}
