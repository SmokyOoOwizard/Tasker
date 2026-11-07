import Foundation
import Observation
import SwiftUI
import TaskerAPI

struct BoardChoice: Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
}

struct BoardColumnState: Identifiable, Equatable {
    var info: BoardColumnInfo
    var rows: [TaskRow] = []
    var total = 0
    var expanded = false

    var id: UUID { info.id }

    var hidden: Int { max(0, total - rows.count) }
}

struct BoardRefusal: Equatable {
    var column: UUID
    var tick: Int
}

@MainActor
@Observable
final class BoardViewModel {
    static let pageSize = 50
    static let fetchSize = 200
    static let refreshDelay: Duration = .milliseconds(120)

    enum Phase: Equatable {
        case loading
        case ready
        case empty
        case missing
        case failed(String)
    }

    let project: UUID
    let requested: UUID?

    private(set) var phase = Phase.loading
    private(set) var board: BoardInfo?
    private(set) var choices: [BoardChoice] = []
    private(set) var columns: [BoardColumnState] = []
    private(set) var outside = 0
    private(set) var vocabulary = TaskListVocabulary.empty
    private(set) var parentRefs: [UUID: BoardParentRef] = [:]
    private(set) var moving: [UUID: UUID] = [:]
    private(set) var refusal: BoardRefusal?
    var dragging: UUID?

    var fieldFilter: [String] = [] {
        didSet {
            if started, fieldFilter != oldValue { reload() }
        }
    }

    @ObservationIgnored var localizer = Localizer(language: .ru)
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored var onFailure: ((String) -> Void)?
    @ObservationIgnored var onPeek: ((UUID) -> Void)?
    @ObservationIgnored var onOpen: ((UUID) -> Void)?
    @ObservationIgnored private let source: any BoardViewSource
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var started = false
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var refreshTask: Task<Void, Never>?
    @ObservationIgnored private var refreshing = false
    @ObservationIgnored private var refreshAgain = false

    init(project: UUID, board: UUID?, source: any BoardViewSource) {
        self.project = project
        requested = board
        self.source = source
    }

    var shown: Int { columns.reduce(0) { $0 + $1.rows.count } }

    func start() {
        started = true
        reload()
    }

    func resume() {
        started = true
        if phase == .ready { scheduleRefresh() } else { reload() }
    }

    func stop() {
        started = false
        loadTask?.cancel()
        refreshTask?.cancel()
        generation += 1
        collapseColumns()
    }

    private func collapseColumns() {
        for index in columns.indices where columns[index].expanded {
            columns[index].expanded = false
            columns[index].rows = Array(columns[index].rows.prefix(Self.pageSize))
        }
    }

    func reload() {
        loadTask?.cancel()
        generation += 1
        let current = generation
        phase = .loading
        loadTask = Task { [weak self] in
            await self?.fetch(generation: current, initial: true)
        }
    }

    func scheduleRefresh() {
        guard started else { return }
        guard phase == .ready || phase == .empty else {
            if case .failed = phase { reload() }
            return
        }
        if refreshing {
            refreshAgain = true
            return
        }
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in
            try? await Task.sleep(for: Self.refreshDelay)
            guard !Task.isCancelled, let self else { return }
            await self.refreshNow()
        }
    }

    private func refreshNow() async {
        refreshing = true
        await fetch(generation: generation, initial: false)
        refreshing = false
        if refreshAgain {
            refreshAgain = false
            scheduleRefresh()
        }
    }

    private func fetch(generation current: Int, initial: Bool) async {
        do {
            let vocabulary = try await source.vocabulary(project: project)
            guard current == generation else { return }
            let list = vocabulary.boardOrder.compactMap { id in vocabulary.boardNames[id].map { BoardChoice(id: id, name: $0) } }
            guard let target = list.first(where: { $0.id == requested })?.id ?? list.first?.id else {
                apply(vocabulary: vocabulary, choices: list, board: nil, columns: [], outside: 0, phase: .empty, animated: !initial)
                return
            }
            guard let info = try await source.board(project: project, id: target) else {
                apply(vocabulary: vocabulary, choices: list, board: nil, columns: [], outside: 0, phase: .missing, animated: !initial)
                return
            }
            let previous = Dictionary(columns.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
            let states = try await loadColumns(info, previous: board?.id == info.id ? previous : [:])
            let typeIds = vocabulary.typeOrder.filter { id in vocabulary.types[id].map { info.setIds.contains($0.statusSetId) } ?? false }
            var outside = 0
            if !typeIds.isEmpty, fieldFilter.isEmpty {
                let all = try await source.taskPage(project: project, typeIds: typeIds, offset: 0, limit: 1).total
                outside = max(0, all - states.reduce(0) { $0 + $1.total })
            }
            let parents = await loadParents(for: states.flatMap(\.rows)) ?? parentRefs
            guard current == generation else { return }
            parentRefs = parents
            apply(vocabulary: vocabulary, choices: list, board: info, columns: states, outside: outside, phase: .ready, animated: !initial)
        } catch is CancellationError {
            return
        } catch {
            guard current == generation else { return }
            if initial || phase == .loading {
                phase = .failed(localizer.message(for: error))
            }
        }
    }

    private func loadParents(for rows: [TaskRow]) async -> [UUID: BoardParentRef]? {
        let ids = Array(Set(rows.filter { !$0.parentIds.isEmpty }.map(\.id)))
        guard !ids.isEmpty else { return [:] }
        guard let found = try? await source.ancestors(project: project, ids: ids) else { return nil }
        return Dictionary(found.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
    }

    func parentChains(of row: TaskRow) -> BoardParentChains {
        row.parentIds.isEmpty ? .none : BoardParentChains.build(parentIds: row.parentIds, refs: parentRefs)
    }

    func parentReference(_ parent: BoardParentRef) -> String? {
        vocabulary.prefixedReference(id: parent.id, seriesNumbers: parent.seriesNumbers)
    }

    private func apply(vocabulary: TaskListVocabulary, choices: [BoardChoice], board: BoardInfo?, columns: [BoardColumnState], outside: Int, phase: Phase, animated: Bool) {
        let change = { [self] in
            self.vocabulary = vocabulary
            self.choices = choices
            self.board = board
            self.columns = columns
            self.outside = outside
            self.phase = phase
        }
        if animated {
            withAnimation(.easeInOut(duration: 0.24)) { change() }
        } else {
            change()
        }
    }

    private func loadColumns(_ info: BoardInfo, previous: [UUID: BoardColumnState]) async throws -> [BoardColumnState] {
        let source = source
        let project = project
        let board = info.id
        let field = fieldFilter
        return try await withThrowingTaskGroup(of: (Int, BoardColumnState).self) { group in
            for (index, column) in info.columns.enumerated() {
                let old = previous[column.id]
                group.addTask {
                    var state = BoardColumnState(info: column, expanded: old?.expanded ?? false)
                    let first = try await source.columnPage(project: project, board: board, column: column.id, field: field, offset: 0, limit: Self.pageSize)
                    state.rows = first.rows
                    state.total = first.total
                    if state.expanded {
                        while state.rows.count < state.total {
                            let page = try await source.columnPage(project: project, board: board, column: column.id, field: field, offset: state.rows.count, limit: Self.fetchSize)
                            if page.rows.isEmpty { break }
                            state.rows += page.rows
                            state.total = page.total
                        }
                    }
                    return (index, state)
                }
            }
            var result: [(Int, BoardColumnState)] = []
            for try await item in group { result.append(item) }
            return result.sorted { $0.0 < $1.0 }.map(\.1)
        }
    }

    func showAll(_ column: UUID) {
        guard let index = columns.firstIndex(where: { $0.id == column }), let board else { return }
        columns[index].expanded = true
        let current = generation
        Task { [weak self] in
            guard let self else { return }
            do {
                var state = self.columns.first { $0.id == column } ?? self.columns[index]
                while state.rows.count < state.total {
                    let page = try await self.source.columnPage(project: self.project, board: board.id, column: column, field: self.fieldFilter, offset: state.rows.count, limit: Self.fetchSize)
                    if page.rows.isEmpty { break }
                    state.rows += page.rows
                    state.total = page.total
                    guard current == self.generation else { return }
                    let parents = await self.loadParents(for: page.rows)
                    guard current == self.generation, let at = self.columns.firstIndex(where: { $0.id == column }) else { return }
                    withAnimation(.easeInOut(duration: 0.2)) {
                        if let parents { self.parentRefs.merge(parents) { _, new in new } }
                        self.columns[at].rows = state.rows
                        self.columns[at].total = state.total
                    }
                }
            } catch {
                self.onFailure?(self.localizer.message(for: error))
            }
        }
    }

    func reference(_ row: TaskRow) -> String {
        vocabulary.reference(row)
    }

    func location(of task: UUID) -> (column: BoardColumnState, row: TaskRow)? {
        for column in columns {
            if let row = column.rows.first(where: { $0.id == task }) { return (column, row) }
        }
        return nil
    }

    func canDrop(task: UUID, on column: UUID) -> Bool {
        guard moving[task] == nil, let place = location(of: task) else { return false }
        return place.column.id != column
    }

    func move(task: UUID, to column: UUID) {
        guard canDrop(task: task, on: column), let board, let place = location(of: task) else { return }
        let reference = reference(place.row)
        moving[task] = column
        let current = generation
        Task { [weak self] in
            guard let self else { return }
            do {
                let moved = try await self.source.move(project: self.project, board: board.id, column: column, task: task, version: place.row.version)
                withAnimation(.easeInOut(duration: 0.24)) {
                    self.moving[task] = nil
                    self.relocate(task: task, to: column, moved: moved)
                }
                let name = self.vocabulary.status(moved.statusId)?.name ?? ""
                self.onNotice?(self.localizer.string(.boardMoved, reference, name))
                guard current == self.generation else { return }
                self.generation += 1
                await self.fetch(generation: self.generation, initial: false)
            } catch {
                withAnimation(.easeInOut(duration: 0.2)) { self.moving[task] = nil }
                self.refusal = BoardRefusal(column: column, tick: (self.refusal?.tick ?? 0) + 1)
                self.onFailure?(Self.failureText(error, reference: reference, l: self.localizer))
                if case TaskerAPIError.conflict = error { self.scheduleRefresh() }
            }
        }
    }

    private func relocate(task: UUID, to column: UUID, moved: BoardMoved) {
        guard let target = columns.firstIndex(where: { $0.id == column }), var row = location(of: task)?.row else { return }
        for index in columns.indices where columns[index].rows.contains(where: { $0.id == task }) {
            columns[index].rows.removeAll { $0.id == task }
            columns[index].total = max(0, columns[index].total - 1)
        }
        row.statusId = moved.statusId
        row.version = moved.version
        let position = columns[target].rows.firstIndex { $0.createdAt > row.createdAt } ?? columns[target].rows.endIndex
        columns[target].rows.insert(row, at: position)
        columns[target].total += 1
    }

    static func failureText(_ error: any Error, reference: String, l: Localizer) -> String {
        if let error = error as? TaskerAPIError, case .badRequest(let refusal) = error {
            return l.refusal(refusal)
        }
        return TaskListModel.statusFailure(error, reference: reference, l: l)
    }

    func outsideTasks() async -> [TaskRow] {
        guard let board else { return [] }
        let vocabulary = vocabulary
        let typeIds = vocabulary.typeOrder.filter { id in vocabulary.types[id].map { board.setIds.contains($0.statusSetId) } ?? false }
        guard !typeIds.isEmpty else { return [] }
        do {
            let inColumns = try await withThrowingTaskGroup(of: Set<UUID>.self) { group in
                for column in board.columns {
                    group.addTask { [source, project] in
                        var ids: Set<UUID> = []
                        var offset = 0
                        while true {
                            let page = try await source.columnPage(project: project, board: board.id, column: column.id, field: [], offset: offset, limit: Self.fetchSize)
                            ids.formUnion(page.rows.map(\.id))
                            offset += page.rows.count
                            if page.rows.isEmpty || offset >= page.total { return ids }
                        }
                    }
                }
                var all: Set<UUID> = []
                for try await ids in group { all.formUnion(ids) }
                return all
            }
            var result: [TaskRow] = []
            var offset = 0
            while true {
                let page = try await source.taskPage(project: project, typeIds: typeIds, offset: offset, limit: Self.fetchSize)
                result += page.rows.filter { !inColumns.contains($0.id) }
                offset += page.rows.count
                if page.rows.isEmpty || offset >= page.total { return result }
            }
        } catch {
            onFailure?(localizer.message(for: error))
            return []
        }
    }
}

extension BoardViewModel {
    func conditionText(_ condition: BoardCondition) -> String {
        guard let field = vocabulary.fields.first(where: { $0.id == condition.fieldId }) else { return "" }
        let symbol: String
        switch condition.op {
        case .equal: symbol = "="
        case .notEqual: symbol = "≠"
        case .greater: symbol = ">"
        case .greaterOrEqual: symbol = "≥"
        case .less: symbol = "<"
        case .lessOrEqual: symbol = "≤"
        case .set: return field.name + " " + localizer.string(.boardOpSet)
        case .unset: return field.name + " " + localizer.string(.boardOpUnset)
        case .attached: return field.name + " " + localizer.string(.boardOpAttached)
        case .detached: return field.name + " " + localizer.string(.boardOpDetached)
        }
        return field.name + " " + symbol + " " + valueText(condition.value, field: field)
    }

    private func valueText(_ value: String, field: TaskListVocabulary.Field) -> String {
        switch field.type {
        case .enum:
            let options = field.enumId.flatMap { vocabulary.enums[$0] } ?? []
            return options.first { $0.id.caseInsensitiveCompare(value) == .orderedSame }?.name ?? value
        case .bool:
            return value.lowercased() == "true" ? localizer.string(.taskDetailYes) : localizer.string(.taskDetailNo)
        default:
            return value
        }
    }
}
