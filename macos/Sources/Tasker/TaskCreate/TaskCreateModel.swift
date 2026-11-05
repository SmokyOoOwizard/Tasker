import Foundation
import Observation
import TaskerAPI

enum TaskCreatePhase: Equatable {
    case loading
    case ready
    case failed(String)
}

struct TaskCreationRequest: Identifiable, Equatable {
    let id = UUID()
    var parent: TaskLinkedBrief?
    var series: UUID?

    static func == (lhs: TaskCreationRequest, rhs: TaskCreationRequest) -> Bool { lhs.id == rhs.id }
}

@MainActor
@Observable
final class TaskCreateModel {
    let project: UUID
    let request: TaskCreationRequest

    private(set) var phase: TaskCreatePhase = .loading
    private(set) var vocabulary: TaskListVocabulary = .empty
    private(set) var submitting = false
    private(set) var attempted = false
    private(set) var serverError: String?

    var title = ""
    var description = ""
    private(set) var typeId: UUID?
    private(set) var statusId: UUID?
    private(set) var seriesIds: [UUID] = []
    private(set) var values: [UUID: [String]] = [:]
    private(set) var links: [DraftLink] = []
    var linksHeight: CGFloat = TaskCreateModel.defaultLinksHeight

    static let defaultLinksHeight: CGFloat = 150
    static let minLinksHeight: CGFloat = 70
    static let minDescriptionHeight: CGFloat = 90

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onCreated: ((TaskCreated, String) -> Void)?
    @ObservationIgnored let source: any TaskCreateSource
    @ObservationIgnored private var loadTask: Task<Void, Never>?

    init(project: UUID, request: TaskCreationRequest, source: any TaskCreateSource) {
        self.project = project
        self.request = request
        self.source = source
    }

    func load() {
        loadTask?.cancel()
        phase = .loading
        loadTask = Task { [weak self] in
            guard let self else { return }
            do {
                let value = try await source.vocabulary(project: project)
                if Task.isCancelled { return }
                prepare(value)
            } catch is CancellationError {
            } catch let error as TaskerAPIError {
                phase = .failed(localizer.message(for: error))
            } catch {
                phase = .failed(localizer.message(for: error))
            }
        }
    }

    func stop() {
        loadTask?.cancel()
    }

    func prepare(_ value: TaskListVocabulary) {
        vocabulary = value
        if typeId == nil {
            typeId = value.typeOrder.first
            statusId = typeId.flatMap { value.statusOptions(forType: $0).first?.id }
            if let series = request.series, value.seriesOrder.contains(series) {
                seriesIds = [series]
            } else if let first = value.seriesOrder.first {
                seriesIds = [first]
            }
            if let parent = request.parent, let hierarchical = value.linkTypes.first(where: { $0.hierarchical && !$0.isSymmetric }) {
                links = [DraftLink(typeId: hierarchical.id, outward: false, task: parent)]
            }
        }
        phase = .ready
    }

    var statusOptions: [TaskListVocabulary.Status] {
        typeId.map { vocabulary.statusOptions(forType: $0) } ?? []
    }

    var typeFields: [TaskFieldValue] {
        guard let typeId, let type = vocabulary.types[typeId] else { return [] }
        let catalog = Dictionary(vocabulary.fields.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return type.fields.compactMap { entry in
            guard let field = catalog[entry.fieldId] else { return nil }
            return TaskFieldValue(
                fieldId: field.id, name: field.name, type: field.type, multiple: field.multiple, enumId: field.enumId,
                required: entry.required, source: .type, values: values[field.id] ?? [], texts: [])
        }
    }

    var freeSeries: [UUID] {
        vocabulary.seriesOrder.filter { !seriesIds.contains($0) }
    }

    func setType(_ id: UUID) {
        guard id != typeId else { return }
        typeId = id
        values = [:]
        if !vocabulary.statusOptions(forType: id).contains(where: { $0.id == statusId }) {
            statusId = vocabulary.statusOptions(forType: id).first?.id
        }
        serverError = nil
    }

    func setStatus(_ id: UUID) {
        statusId = id
    }

    func addSeries(_ id: UUID) {
        if !seriesIds.contains(id) { seriesIds.append(id) }
    }

    func removeSeries(_ id: UUID) {
        seriesIds.removeAll { $0 == id }
    }

    func setValues(_ id: UUID, _ newValues: [String]) {
        if newValues.isEmpty { values[id] = nil } else { values[id] = newValues }
    }

    func addLink(type: UUID, outward: Bool, task: TaskLinkedBrief) {
        let link = DraftLink(typeId: type, outward: outward, task: task)
        if !links.contains(where: { $0.id == link.id }) { links.append(link) }
    }

    func removeLink(_ id: String) {
        links.removeAll { $0.id == id }
    }

    func linkedIds(type: UUID, outward: Bool) -> Set<UUID> {
        Set(links.filter { $0.typeId == type && $0.outward == outward }.map(\.task.id))
    }

    func linkName(_ link: DraftLink) -> String {
        guard let type = vocabulary.linkType(link.typeId) else { return "" }
        if link.outward || type.inwardName.isEmpty { return type.outwardName }
        return type.inwardName
    }

    var linkGroups: [TaskDetailModel.LinkGroup] {
        var order: [String] = []
        var groups: [String: [TaskLinkEntry]] = [:]
        for link in links {
            let name = linkName(link)
            if groups[name] == nil { order.append(name) }
            groups[name, default: []].append(
                TaskLinkEntry(typeId: link.typeId, typeName: "", direction: link.outward ? .outward : .inward, name: name, task: link.task))
        }
        return order.map { TaskDetailModel.LinkGroup(name: $0, entries: groups[$0] ?? []) }
    }

    var titleMissing: Bool {
        title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var missingRequired: [TaskFieldValue] {
        typeFields.filter { $0.required && $0.isEmpty }
    }

    var invalidFields: [TaskFieldValue] {
        typeFields.filter { !$0.isEmpty && FieldValueCodec.normalizeAll($0.values, type: $0.type) == nil }
    }

    var problem: String? {
        guard attempted else { return nil }
        if titleMissing { return localizer.string(.taskCreateTitleRequired) }
        let missing = missingRequired
        if !missing.isEmpty { return localizer.string(.taskCreateMissingFields, missing.map(\.name).joined(separator: ", ")) }
        if let invalid = invalidFields.first { return "\(invalid.name): \(localizer.string(.taskDetailInvalid))" }
        return nil
    }

    var isDirty: Bool {
        !titleMissing
            || !description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            || !values.isEmpty
            || links.count > (request.parent == nil ? 0 : 1)
    }

    func validate() -> Bool {
        attempted = true
        return !titleMissing && missingRequired.isEmpty && invalidFields.isEmpty && typeId != nil
    }

    func payload() -> TaskCreateRequest? {
        guard let typeId else { return nil }
        var fields: [UUID: [String]] = [:]
        var order: [UUID] = []
        for field in typeFields where !field.isEmpty {
            guard let normalized = FieldValueCodec.normalizeAll(field.values, type: field.type), !normalized.isEmpty else { continue }
            fields[field.fieldId] = normalized
            order.append(field.fieldId)
        }
        return TaskCreateRequest(
            title: title.trimmingCharacters(in: .whitespacesAndNewlines), description: description, typeId: typeId, statusId: statusId,
            seriesIds: seriesIds, fields: fields, fieldOrder: order, links: links)
    }

    func submit() async {
        guard !submitting, phase == .ready else { return }
        guard validate(), let body = payload() else { return }
        submitting = true
        serverError = nil
        defer { submitting = false }
        do {
            let created = try await source.create(project: project, request: body)
            onCreated?(created, vocabulary.reference(created.seriesNumbers, id: created.id))
        } catch is CancellationError {
        } catch let error as TaskerAPIError {
            serverError = failure(error)
        } catch {
            serverError = localizer.string(.taskCreateFailed, localizer.message(for: error))
        }
    }

    private func failure(_ error: TaskerAPIError) -> String {
        switch error {
        case .timedOut, .network:
            return localizer.string(.taskDetailOffline)
        case .forbidden(let refusal):
            return localizer.string(.taskDetailForbidden, localizer.refusal(refusal))
        default:
            return localizer.string(.taskCreateFailed, localizer.message(for: error))
        }
    }
}
