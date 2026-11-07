import Foundation
import Observation

struct PaletteTaskHit: Equatable {
    var brief: TaskLinkedBrief
    var score: Double
    var isExactNumber: Bool
    var snippet: PaletteSnippet?
}

struct PaletteTaskResult: Equatable {
    var query: String
    var hits: [PaletteTaskHit]
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
        searching = true
        searchTask = Task { [weak self] in
            let hits = try? await source.search(project: project, text: text, limit: Self.taskLimit)
            guard !Task.isCancelled else { return }
            self?.finish(token: token, text: text, hits: hits)
        }
    }

    private func finish(token: Int, text: String, hits: [PaletteTaskHit]?) {
        guard token == generation else { return }
        searching = false
        failed = hits == nil
        tasks = PaletteTaskResult(query: text, hits: hits ?? [])
        let anchor = entries.indices.contains(selected) ? entries[selected].id : nil
        rebuild()
        if let anchor, let index = entries.firstIndex(where: { $0.id == anchor }) {
            selected = index
        } else {
            selected = 0
        }
    }

    private func taskEntry(_ hit: PaletteTaskHit, detail: String? = nil) -> PaletteEntry {
        let brief = hit.brief
        let reference = reference(brief)
        let glyph = brief.typeId.map { vocabulary.glyph(status: brief.statusId, type: $0) } ?? vocabulary.glyph(status: brief.statusId)
        let color = vocabulary.status(brief.statusId)?.color ?? DesignTag.palette[0]
        let status = vocabulary.status(brief.statusId)?.name
        return PaletteEntry(
            id: "task:" + brief.id.uuidString, title: brief.title, reference: reference, detail: detail ?? status,
            icon: .status(glyph, color: color), keywords: "", snippet: hit.snippet,
            run: { [openTask] in openTask(brief.id) })
    }

    private func reference(_ brief: TaskLinkedBrief) -> String {
        for number in brief.seriesNumbers {
            if let prefix = vocabulary.prefixes[number.seriesId] { return "\(prefix)-\(number.number)" }
        }
        return "#" + brief.id.uuidString.lowercased().prefix(8)
    }

    private func conflictingNumbers(_ hits: [PaletteTaskHit]) -> Set<UUID> {
        var owners: [String: [UUID]] = [:]
        for hit in hits where hit.isExactNumber {
            for number in hit.brief.seriesNumbers {
                owners["\(number.seriesId)-\(number.number)", default: []].append(hit.brief.id)
            }
        }
        return Set(owners.values.filter { $0.count > 1 }.flatMap { $0 })
    }

    private func rebuild() {
        let l = localizer
        var result: [PaletteSection] = []
        let text = trimmed
        if text.isEmpty {
            result.append(PaletteSection(id: "commands", title: l.string(.paletteCommands), entries: commands))
        } else {
            for provider in queryProviders { result += provider.sections(for: text) }
            let hits = tasks?.hits ?? []
            let conflicted = conflictingNumbers(hits)
            let found = hits.map { hit in
                (entry: taskEntry(hit, detail: conflicted.contains(hit.brief.id) ? l.string(.paletteConflictBadge) : nil), score: hit.score)
            }
            let matched = PaletteMatcher.scored(commands, query: text)
            let merged = (found + matched.map { (entry: $0.entry, score: $0.score) }).enumerated()
                .sorted { $0.element.score == $1.element.score ? $0.offset < $1.offset : $0.element.score > $1.element.score }
                .map(\.element.entry)
            if !merged.isEmpty {
                let title = found.isEmpty ? l.string(.paletteCommands) : matched.isEmpty ? l.string(.paletteTasks) : l.string(.paletteCommandsAndTasks)
                result.append(PaletteSection(id: "results", title: title, entries: merged))
            }
        }
        sections = result
        if selected >= entries.count { selected = max(0, entries.count - 1) }
    }
}
