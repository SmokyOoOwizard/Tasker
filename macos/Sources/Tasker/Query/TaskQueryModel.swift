import Foundation
import Observation
import TaskerAPI

struct TaskQueryIssue: Hashable, Sendable {
    var code: String
    var message: String
    var params: [String: String]
    var start: Int
    var end: Int

    var refusal: Refusal {
        Refusal(code: code, message: message, params: params.mapValues { .string($0) })
    }
}

struct TaskQueryHint: Hashable, Sendable, Identifiable {
    var kind: TaskQuerySuggestionKind
    var label: String
    var insert: String
    var start: Int
    var end: Int
    var caret: Int
    var detail: String?

    var id: String { label + "\u{0}" + insert }
}

struct TaskQueryReport: Hashable, Sendable {
    var valid: Bool
    var hasSort: Bool
    var errors: [TaskQueryIssue]
    var hints: [TaskQueryHint]
}

protocol TaskQuerySource: Sendable {
    func analyze(project: UUID, text: String, cursor: Int) async throws -> TaskQueryReport
}

struct GraphQLTaskQuerySource: TaskQuerySource {
    let client: TaskerAPIClient

    func analyze(project: UUID, text: String, cursor: Int) async throws -> TaskQueryReport {
        guard let analysis = try await client.graphQL.fetch(TaskQueryAnalysisQuery(projectId: project, text: text, cursor: cursor)).project?.taskQueryAnalysis else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        return TaskQueryReport(
            valid: analysis.valid,
            hasSort: analysis.hasSort,
            errors: analysis.errors.map { error in
                TaskQueryIssue(
                    code: error.code, message: error.message,
                    params: Dictionary(error.params.map { ($0.name, $0.value) }, uniquingKeysWith: { first, _ in first }),
                    start: error.start, end: error.end)
            },
            hints: analysis.suggestions.map {
                TaskQueryHint(kind: $0.kind, label: $0.label, insert: $0.insert, start: $0.start, end: $0.end, caret: $0.caret, detail: $0.detail)
            })
    }
}

@MainActor
@Observable
final class TaskQueryModel {
    static let analyzeDelay: Duration = .milliseconds(120)
    static let incompleteCodes: Set<String> = [
        "query.expectedCondition", "query.expectedProperty", "query.expectedOperator", "query.expectedValue",
        "query.expectedSort", "query.expectedSortKey", "query.unclosedString", "query.unclosedParen", "query.unclosedBracket"
    ]

    let project: UUID
    private(set) var text: String
    private(set) var cursor: Int
    private(set) var errors: [TaskQueryIssue] = []
    private(set) var hints: [TaskQueryHint] = []
    private(set) var selected: Int?
    private(set) var open = false
    private(set) var applied: String
    private(set) var hasSort = false
    private(set) var revision = 0

    @ObservationIgnored var onApply: ((String, Bool) -> Void)?
    @ObservationIgnored var onTextChange: (() -> Void)?
    @ObservationIgnored private let source: any TaskQuerySource
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var hintsStale = false
    @ObservationIgnored private var pendingAccept = false

    init(project: UUID, source: any TaskQuerySource, text: String = "") {
        self.project = project
        self.source = source
        self.text = text
        cursor = (text as NSString).length
        applied = text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var error: TaskQueryIssue? { visibleErrors.first }

    var visibleErrors: [TaskQueryIssue] {
        guard showsHints, let first = errors.first, first.end >= cursor, Self.incompleteCodes.contains(first.code) else { return errors }
        return []
    }

    var showsHints: Bool { open && !hints.isEmpty }

    func start() {
        guard !text.isEmpty else { return }
        schedule(apply: true, delay: .zero, restoring: true)
    }

    func edited(_ text: String, cursor: Int) {
        guard text != self.text else {
            caretMoved(cursor)
            return
        }
        self.text = text
        self.cursor = cursor
        open = true
        selected = nil
        hintsStale = true
        pendingAccept = false
        onTextChange?()
        schedule(apply: true)
    }

    func caretMoved(_ cursor: Int) {
        guard cursor != self.cursor else { return }
        self.cursor = cursor
        guard open else { return }
        selected = nil
        hintsStale = true
        pendingAccept = false
        schedule(apply: false)
    }

    func recheck() {
        guard !text.isEmpty else { return }
        generation += 1
        let current = generation
        let text = text
        let cursor = cursor
        let source = source
        let project = project
        task?.cancel()
        task = Task { [weak self] in
            do {
                let report = try await source.analyze(project: project, text: text, cursor: cursor)
                self?.rechecked(report, generation: current, text: text)
            } catch {
            }
        }
    }

    private func rechecked(_ report: TaskQueryReport, generation: Int, text: String) {
        guard generation == self.generation else { return }
        errors = report.errors
        guard !hintsStale else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if report.valid {
            hasSort = report.hasSort
            if trimmed == applied { onApply?(applied, report.hasSort) }
        } else if !applied.isEmpty, trimmed == applied {
            apply("", hasSort: false)
        }
    }

    func showHints() {
        open = true
        selected = nil
        schedule(apply: false, delay: .zero)
    }

    func close() {
        pendingAccept = false
        open = false
        selected = nil
    }

    func move(_ delta: Int) {
        guard showsHints else {
            showHints()
            return
        }
        let count = hints.count
        guard let selected else {
            self.selected = delta > 0 ? 0 : count - 1
            return
        }
        self.selected = (selected + delta + count) % count
    }

    func select(_ index: Int?) {
        selected = index
    }

    @discardableResult
    func accept(_ index: Int? = nil) -> Bool {
        if hintsStale, open {
            pendingAccept = true
            return true
        }
        guard showsHints, let index = index ?? selected ?? (hints.isEmpty ? nil : 0), hints.indices.contains(index) else { return false }
        let hint = hints[index]
        let source = text as NSString
        let end = min(hint.end, source.length)
        let start = min(hint.start, end)
        text = source.replacingCharacters(in: NSRange(location: start, length: end - start), with: hint.insert)
        cursor = start + hint.caret
        revision += 1
        selected = nil
        hints = []
        onTextChange?()
        schedule(apply: true, delay: .zero)
        return true
    }

    func submit() {
        if selected != nil, accept() { return }
        close()
        schedule(apply: true, delay: .zero)
    }

    func clear() {
        text = ""
        cursor = 0
        errors = []
        hints = []
        selected = nil
        open = false
        revision += 1
        onTextChange?()
        apply("", hasSort: false)
    }

    private func schedule(apply: Bool, delay: Duration = analyzeDelay, restoring: Bool = false) {
        generation += 1
        let current = generation
        let text = text
        let cursor = cursor
        let source = source
        let project = project
        task?.cancel()
        task = Task { [weak self] in
            if delay > .zero { try? await Task.sleep(for: delay) }
            guard !Task.isCancelled else { return }
            do {
                let report = try await source.analyze(project: project, text: text, cursor: cursor)
                self?.received(report, generation: current, apply: apply, restoring: restoring, text: text)
            } catch {
                self?.failedAnalysis(generation: current)
            }
        }
    }

    private func received(_ report: TaskQueryReport, generation: Int, apply shouldApply: Bool, restoring: Bool, text: String) {
        guard generation == self.generation else { return }
        errors = report.errors
        hints = report.hints
        hintsStale = false
        if let selected, selected >= hints.count { self.selected = nil }
        guard report.valid else {
            if restoring { apply("", hasSort: false) }
            acceptPending()
            return
        }
        hasSort = report.hasSort
        if shouldApply {
            apply(text.trimmingCharacters(in: .whitespacesAndNewlines), hasSort: report.hasSort)
        } else {
            onApply?(applied, hasSort)
        }
        acceptPending()
    }

    private func acceptPending() {
        guard pendingAccept else { return }
        pendingAccept = false
        accept()
    }

    private func failedAnalysis(generation: Int) {
        guard generation == self.generation else { return }
        hints = []
        selected = nil
        hintsStale = false
        pendingAccept = false
    }

    private func apply(_ value: String, hasSort: Bool) {
        self.hasSort = hasSort
        applied = value
        onApply?(value, hasSort)
    }
}
