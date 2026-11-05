import AppKit
import Foundation
import Observation
import TaskerAPI

@MainActor
final class TaskDrafts {
    private var values: [String: String] = [:]

    var isEmpty: Bool { values.isEmpty }

    func text(task: UUID, slot: String) -> String? {
        values[key(task, slot)]
    }

    func set(_ text: String?, task: UUID, slot: String) {
        values[key(task, slot)] = text
    }

    private func key(_ task: UUID, _ slot: String) -> String {
        task.uuidString + "/" + slot
    }
}

@MainActor
struct TaskDraftSlot {
    let drafts: TaskDrafts
    let task: UUID
    let slot: String

    var text: String? { drafts.text(task: task, slot: slot) }

    var list: [String]? {
        text.flatMap { try? JSONDecoder().decode([String].self, from: Data($0.utf8)) }
    }

    func set(_ text: String?) {
        drafts.set(text, task: task, slot: slot)
    }

    func set(list: [String]?) {
        set(list.flatMap { try? JSONEncoder().encode($0) }.map { String(decoding: $0, as: UTF8.self) })
    }
}

enum TaskDraftCaret {
    @MainActor
    static func moveToEnd() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            (NSApp.keyWindow?.firstResponder as? NSTextView)?.moveToEndOfDocument(nil)
        }
    }
}

enum TaskDetailPhase: Equatable {
    case loading
    case ready
    case failed(String)
    case missing
}

struct TaskEditConflict: Equatable {
    var edit: TaskEdit
    var message: String
}

@MainActor
@Observable
final class TaskDetailModel {
    let project: UUID
    let taskId: UUID

    private(set) var phase: TaskDetailPhase = .loading
    private(set) var detail: TaskDetailData?
    private(set) var vocabulary: TaskListVocabulary = .empty
    private(set) var busy = false
    private(set) var revision = 0
    var conflict: TaskEditConflict?

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored var onDeleted: (() -> Void)?
    @ObservationIgnored var onChanged: (() -> Void)?
    @ObservationIgnored var drafts: TaskDrafts?

    @ObservationIgnored let source: any TaskDetailSource
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var reloadAgain = false

    init(project: UUID, task: UUID, source: any TaskDetailSource) {
        self.project = project
        taskId = task
        self.source = source
    }

    func draftSlot(_ slot: String) -> TaskDraftSlot? {
        drafts.map { TaskDraftSlot(drafts: $0, task: taskId, slot: slot) }
    }

    var reference: String {
        guard let detail else { return "#" + taskId.uuidString.lowercased().prefix(8) }
        return vocabulary.reference(detail.seriesNumbers, id: detail.id)
    }

    var readyReference: String? {
        detail == nil ? nil : reference
    }

    var statusOptions: [TaskListVocabulary.Status] {
        guard let detail else { return [] }
        return vocabulary.statusOptions(forType: detail.typeId)
    }

    var typeFields: [TaskFieldValue] { detail?.fields.filter { $0.source == .type } ?? [] }
    var extraFields: [TaskFieldValue] { detail?.fields.filter { $0.source != .type } ?? [] }

    var parents: [TaskLinkEntry] {
        (detail?.links ?? []).filter { vocabulary.linkType($0.typeId)?.hierarchical == true && $0.direction == .inward }
    }

    var children: [TaskLinkEntry] {
        (detail?.links ?? []).filter { vocabulary.linkType($0.typeId)?.hierarchical == true && $0.direction == .outward }
    }

    var childProgress: (done: Int, total: Int) {
        let kids = children
        return (kids.filter { vocabulary.isDone(status: $0.task.statusId) }.count, kids.count)
    }

    struct LinkGroup: Identifiable {
        var name: String
        var entries: [TaskLinkEntry]
        var id: String { name }
    }

    var linkGroups: [LinkGroup] {
        var order: [String] = []
        var groups: [String: [TaskLinkEntry]] = [:]
        for link in detail?.links ?? [] where vocabulary.linkType(link.typeId)?.hierarchical != true {
            if groups[link.name] == nil { order.append(link.name) }
            groups[link.name, default: []].append(link)
        }
        return order.map { LinkGroup(name: $0, entries: groups[$0] ?? []) }
    }

    var catalogFields: [TaskListVocabulary.Field] {
        vocabulary.catalogFields(excluding: Set((detail?.fields ?? []).map(\.fieldId)))
    }

    var freeSeries: [UUID] {
        vocabulary.seriesAvailable(excluding: detail?.seriesNumbers ?? [])
    }

    func load() {
        loadTask?.cancel()
        phase = .loading
        loadTask = Task { [weak self] in
            guard let self else { return }
            await fetch(initial: true)
        }
    }

    func refresh() {
        if busy { reloadAgain = true; return }
        loadTask?.cancel()
        loadTask = Task { [weak self] in
            guard let self else { return }
            await fetch(initial: false)
        }
    }

    func stop() {
        loadTask?.cancel()
    }

    private func fetch(initial: Bool) async {
        do {
            async let vocabulary = source.vocabulary(project: project)
            async let loaded = source.load(project: project, task: taskId)
            let (vocab, value) = try await (vocabulary, loaded)
            if Task.isCancelled { return }
            self.vocabulary = vocab
            if let value {
                detail = value
                phase = .ready
                revision += 1
            } else {
                detail = nil
                phase = .missing
            }
        } catch is CancellationError {
        } catch let error as TaskerAPIError {
            if case .notFound = error {
                detail = nil
                phase = .missing
            } else if initial || detail == nil {
                phase = .failed(localizer.message(for: error))
            }
        } catch {
            if initial || detail == nil { phase = .failed(localizer.message(for: error)) }
        }
    }

    func perform(_ edit: TaskEdit) {
        Task { await run(edit) }
    }

    func run(_ edit: TaskEdit) async {
        guard let detail, !busy else { return }
        busy = true
        defer {
            busy = false
            if reloadAgain {
                reloadAgain = false
                refresh()
            }
        }
        do {
            try await apply(edit, to: detail)
            if edit == .delete {
                onDeleted?()
                return
            }
            await fetch(initial: false)
            onChanged?()
        } catch let error as TaskerAPIError {
            await fail(error, edit: edit)
        } catch is CancellationError {
        } catch {
            onNotice?(localizer.string(.taskDetailFailed, localizer.message(for: error)))
        }
    }

    private func fail(_ error: TaskerAPIError, edit: TaskEdit) async {
        switch error {
        case .conflict(let conflict):
            switch conflict.kind {
            case .modified:
                await fetch(initial: false)
                self.conflict = TaskEditConflict(edit: edit, message: localizer.refusal(conflict.refusal))
            case .locked:
                onNotice?(localizer.refusal(conflict.refusal))
            default:
                onNotice?(localizer.string(.taskDetailFailed, localizer.refusal(conflict.refusal)))
            }
        case .notFound:
            phase = .missing
            detail = nil
            onNotice?(localizer.string(.taskDetailDeleted, reference))
        case .forbidden(let refusal):
            onNotice?(localizer.string(.taskDetailForbidden, localizer.refusal(refusal)))
        case .timedOut, .network:
            onNotice?(localizer.string(.taskDetailOffline))
        default:
            onNotice?(localizer.string(.taskDetailFailed, localizer.message(for: error)))
        }
        if case .conflict = error {} else { await fetch(initial: false) }
    }

    func resolveConflict(keepMine: Bool) {
        guard let pending = conflict else { return }
        conflict = nil
        if keepMine { perform(pending.edit) }
    }

    private func apply(_ edit: TaskEdit, to detail: TaskDetailData) async throws {
        let version = detail.version
        switch edit {
        case .title(let text):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(title: text, version: version))
        case .description(let text):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(description: text, version: version))
        case .status(let status):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(statusId: status, version: version))
        case .type(let type, let status):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(typeId: type, statusId: status, version: version))
        case .setField(let id, let values):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(setValues: [id: values], setOrder: [id], version: version))
        case .addFields(let ids):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(addFields: ids, version: version))
        case .addOwnField(let field):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(newOwnFields: [field], version: version))
        case .removeField(let id):
            try await source.update(project: project, task: taskId, request: TaskUpdateRequest(removeFields: [id], version: version))
        case .addSeries(let series):
            try await source.addToSeries(project: project, task: taskId, series: series, version: version)
        case .removeSeries(let series):
            try await source.removeFromSeries(project: project, task: taskId, series: series, version: version)
        case .renumber(let series, let number):
            try await source.renumber(project: project, task: taskId, series: series, to: number, version: version)
        case .addLink(let type, let target, let outward):
            if outward {
                try await source.addLink(project: project, task: taskId, type: type, target: target, version: version)
            } else {
                try await source.addLink(project: project, task: target, type: type, target: taskId, version: nil)
            }
        case .removeLink(let type, let target, let outward):
            if outward {
                try await source.removeLink(project: project, task: taskId, type: type, target: target, version: version)
            } else {
                try await source.removeLink(project: project, task: target, type: type, target: taskId, version: nil)
            }
        case .delete:
            try await source.delete(project: project, task: taskId, version: version)
        }
    }
}
