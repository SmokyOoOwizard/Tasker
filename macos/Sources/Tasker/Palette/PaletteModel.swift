import Foundation
import Observation

struct PaletteTaskResult: Equatable {
    var query: String
    var reference: [TaskLinkedBrief]
    var matches: [TaskLinkedBrief]
}

@MainActor
@Observable
final class PaletteModel {
    static let taskLimit = 30

    private(set) var sections: [PaletteSection] = []
    private(set) var selected = 0
    private(set) var searching = false
    private(set) var failed = false
    var query = "" {
        didSet { if query != oldValue { queryChanged() } }
    }

    var localizer: Localizer
    var queryProviders: [any PaletteQueryProvider] = []

    @ObservationIgnored private let source: (any PaletteSource)?
    @ObservationIgnored private let project: UUID?
    @ObservationIgnored private let makeCommands: (TaskListVocabulary) -> [PaletteEntry]
    @ObservationIgnored private let openTask: (UUID) -> Void
    @ObservationIgnored private let close: () -> Void
    @ObservationIgnored private var vocabulary = TaskListVocabulary.empty
    @ObservationIgnored private var commands: [PaletteEntry] = []
    @ObservationIgnored private var tasks: PaletteTaskResult?
    @ObservationIgnored private var searchTask: Task<Void, Never>?
    @ObservationIgnored private var generation = 0

    init(
        localizer: Localizer,
        source: (any PaletteSource)?,
        project: UUID?,
        makeCommands: @escaping (TaskListVocabulary) -> [PaletteEntry],
        openTask: @escaping (UUID) -> Void,
        close: @escaping () -> Void
    ) {
        self.localizer = localizer
        self.source = source
        self.project = project
        self.makeCommands = makeCommands
        self.openTask = openTask
        self.close = close
        commands = makeCommands(vocabulary)
        rebuild()
    }

    var entries: [PaletteEntry] { sections.flatMap(\.entries) }

    var isEmpty: Bool { entries.isEmpty }

    func start() {
        guard let source, let project else { return }
        Task { [weak self] in
            guard let value = try? await source.vocabulary(project: project) else { return }
            self?.vocabularyLoaded(value)
        }
    }

    func stop() {
        searchTask?.cancel()
        searchTask = nil
        generation += 1
    }

    func vocabularyLoaded(_ value: TaskListVocabulary) {
        vocabulary = value
        commands = makeCommands(value)
        rebuild()
    }

    func move(_ offset: Int) {
        let count = entries.count
        guard count > 0 else { return }
        selected = min(max(selected + offset, 0), count - 1)
    }

    func select(_ index: Int) {
        guard entries.indices.contains(index) else { return }
        selected = index
    }

    func runSelected() {
        run(at: selected)
    }

    func run(at index: Int) {
        guard entries.indices.contains(index) else { return }
        let entry = entries[index]
        guard entry.isEnabled else { return }
        close()
        entry.run()
    }

    private var trimmed: String { query.trimmingCharacters(in: .whitespaces) }

    private func queryChanged() {
        selected = 0
        searchTask?.cancel()
        searchTask = nil
        generation += 1
        guard !trimmed.isEmpty, let source, let project else {
            tasks = nil
            searching = false
            failed = false
            rebuild()
            return
        }
        rebuild()
        let text = trimmed
        let token = generation
        let reference = referenceText(text)
        searching = true
        searchTask = Task { [weak self] in
            async let found: [TaskLinkedBrief]? = try? await source.search(project: project, text: text, limit: Self.taskLimit)
            async let exact: [TaskLinkedBrief]? = reference == nil ? [] : (try? await source.resolve(project: project, ref: reference ?? ""))
            let matches = await found
            let byReference = await exact ?? []
            guard !Task.isCancelled else { return }
            self?.finish(token: token, text: text, matches: matches, reference: byReference)
        }
    }

    private func finish(token: Int, text: String, matches: [TaskLinkedBrief]?, reference: [TaskLinkedBrief]) {
        guard token == generation else { return }
        searching = false
        failed = matches == nil && reference.isEmpty
        tasks = PaletteTaskResult(query: text, reference: reference, matches: matches ?? [])
        let anchor = entries.indices.contains(selected) ? entries[selected].id : nil
        rebuild()
        if let anchor, let index = entries.firstIndex(where: { $0.id == anchor }) {
            selected = index
        } else {
            selected = 0
        }
    }

    private func referenceText(_ text: String) -> String? {
        guard PaletteMatcher.looksLikeReference(text), let dash = text.lastIndex(of: "-") else { return nil }
        let prefix = String(text[..<dash])
        let number = text[text.index(after: dash)...]
        let known = vocabulary.prefixes.values.first { $0.caseInsensitiveCompare(prefix) == .orderedSame } ?? prefix
        return "\(known)-\(number)"
    }

    private func taskEntry(_ brief: TaskLinkedBrief, detail: String? = nil) -> PaletteEntry {
        let reference = reference(brief)
        let glyph = brief.typeId.map { vocabulary.glyph(status: brief.statusId, type: $0) } ?? vocabulary.glyph(status: brief.statusId)
        let color = vocabulary.status(brief.statusId)?.color ?? DesignTag.palette[0]
        let status = vocabulary.status(brief.statusId)?.name
        return PaletteEntry(
            id: "task:" + brief.id.uuidString, title: brief.title, reference: reference, detail: detail ?? status,
            icon: .status(glyph, color: color), keywords: "",
            run: { [openTask] in openTask(brief.id) })
    }

    private func reference(_ brief: TaskLinkedBrief) -> String {
        for number in brief.seriesNumbers {
            if let prefix = vocabulary.prefixes[number.seriesId] { return "\(prefix)-\(number.number)" }
        }
        return "#" + brief.id.uuidString.lowercased().prefix(8)
    }

    private func rebuild() {
        let l = localizer
        var result: [PaletteSection] = []
        let text = trimmed
        if text.isEmpty {
            result.append(PaletteSection(id: "commands", title: l.string(.paletteCommands), entries: commands))
        } else {
            for provider in queryProviders { result += provider.sections(for: text) }
            let current = tasks
            let exact = current?.reference ?? []
            let conflict = exact.count > 1
            if !exact.isEmpty {
                let title = conflict ? l.string(.paletteRefConflict, text.uppercased()) : l.string(.paletteByReference)
                result.append(PaletteSection(
                    id: "reference", title: title,
                    entries: exact.map { taskEntry($0, detail: conflict ? l.string(.paletteConflictBadge) : nil) }))
            }
            let shownIds = Set(exact.map(\.id))
            let found = (current?.matches ?? []).filter { !shownIds.contains($0.id) }.map { taskEntry($0) }
            let matched = PaletteMatcher.ranked(commands, query: text)
            if !matched.isEmpty { result.append(PaletteSection(id: "commands", title: found.isEmpty ? l.string(.paletteCommands) : l.string(.paletteCommandsAndTasks), entries: matched)) }
            if !found.isEmpty { result.append(PaletteSection(id: "tasks", title: l.string(.paletteTasks), entries: found)) }
        }
        sections = result
        if selected >= entries.count { selected = max(0, entries.count - 1) }
    }
}
