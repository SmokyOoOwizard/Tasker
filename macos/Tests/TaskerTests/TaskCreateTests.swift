import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubCreateSource: TaskCreateSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _requests: [TaskCreateRequest] = []
    private var _failures: [TaskerAPIError] = []
    let vocab: TaskListVocabulary
    let created: TaskCreated

    init(vocabulary: TaskListVocabulary, created: TaskCreated) {
        vocab = vocabulary
        self.created = created
    }

    var requests: [TaskCreateRequest] { lock.withLock { _requests } }
    func failNext(_ error: TaskerAPIError) { lock.withLock { _failures.append(error) } }

    func vocabulary(project: UUID) async throws -> TaskListVocabulary { vocab }
    func search(project: UUID) async throws -> [TaskLinkedBrief] { [] }
    func create(project: UUID, request: TaskCreateRequest) async throws -> TaskCreated {
        try lock.withLock {
            if !_failures.isEmpty { throw _failures.removeFirst() }
            _requests.append(request)
        }
        return created
    }
}

@MainActor
struct TaskCreateTests {
    let project = UUID()
    let todo = UUID()
    let doing = UUID()
    let done = UUID()
    let cancelled = UUID()
    let flow = UUID()
    let simple = UUID()
    let feature = UUID()
    let chore = UUID()
    let series = UUID()
    let blocks = UUID()
    let parent = UUID()
    let priority = UUID()
    let component = UUID()
    let estimate = UUID()
    let priorities = UUID()
    let components = UUID()
    let high = UUID()
    let low = UUID()
    let core = UUID()
    let web = UUID()

    var vocabulary: TaskListVocabulary {
        var value = TaskListVocabulary()
        value.statuses = [
            .init(id: todo, name: "Todo", color: "#8993a4"), .init(id: doing, name: "Doing", color: "#2684ff"),
            .init(id: done, name: "Done", color: "#36b37e"), .init(id: cancelled, name: "Cancelled", color: "#de350b")
        ]
        value.statusSets = [flow: [todo, doing, done], simple: [todo, cancelled]]
        value.types = [
            feature: .init(name: "Feature", statusSetId: flow, fields: [
                .init(fieldId: priority, required: true), .init(fieldId: component, required: false), .init(fieldId: estimate, required: false)
            ]),
            chore: .init(name: "Chore", statusSetId: simple)
        ]
        value.typeOrder = [feature, chore]
        value.prefixes = [series: "DMO"]
        value.seriesNames = [series: "Demo"]
        value.seriesOrder = [series]
        value.linkTypes = [
            .init(id: blocks, name: "Blocks", outwardName: "blocks", inwardName: "is blocked by", hierarchical: false, isSymmetric: false),
            .init(id: parent, name: "Parent", outwardName: "includes", inwardName: "part of", hierarchical: true, isSymmetric: false)
        ]
        value.fields = [
            .init(id: priority, name: "Priority", type: .enum, multiple: false, enumId: priorities),
            .init(id: component, name: "Component", type: .enum, multiple: true, enumId: components),
            .init(id: estimate, name: "Estimate", type: .int, multiple: false, enumId: nil)
        ]
        value.enums = [
            priorities: [.init(id: high.uuidString, name: "High"), .init(id: low.uuidString, name: "Low")],
            components: [.init(id: core.uuidString, name: "Core"), .init(id: web.uuidString, name: "Web")]
        ]
        return value
    }

    func brief(_ title: String = "Epic") -> TaskLinkedBrief {
        TaskLinkedBrief(id: UUID(), title: title, statusId: todo, typeId: feature, seriesNumbers: [TaskSeriesRef(seriesId: series, number: 7)])
    }

    func ready(parent: TaskLinkedBrief? = nil, source: StubCreateSource? = nil) async -> TaskCreateModel {
        let source = source ?? StubCreateSource(vocabulary: vocabulary, created: TaskCreated(id: UUID(), seriesNumbers: [TaskSeriesRef(seriesId: series, number: 1)]))
        let model = TaskCreateModel(project: project, request: TaskCreationRequest(parent: parent), source: source)
        model.load()
        for _ in 0..<100 where model.phase == .loading { try? await Task.sleep(for: .milliseconds(10)) }
        return model
    }

    @Test func startsWithFirstTypeItsFirstStatusAndFirstSeries() async {
        let model = await ready()
        #expect(model.phase == .ready)
        #expect(model.typeId == feature)
        #expect(model.statusId == todo)
        #expect(model.seriesIds == [series])
        #expect(model.typeFields.map(\.name) == ["Priority", "Component", "Estimate"])
        #expect(model.typeFields.map(\.required) == [true, false, false])
    }

    @Test func parentBecomesAnInwardHierarchicalLink() async {
        let epic = brief()
        let model = await ready(parent: epic)
        #expect(model.links == [DraftLink(typeId: parent, outward: false, task: epic)])
        #expect(model.linkGroups.map(\.name) == ["part of"])
        #expect(!model.isDirty)
    }

    @Test func changingTypeResetsFieldsAndKeepsOrFixesStatus() async {
        let model = await ready()
        model.setStatus(doing)
        model.setValues(priority, [high.uuidString.lowercased()])
        model.setType(chore)
        #expect(model.typeId == chore)
        #expect(model.statusId == todo)
        #expect(model.values.isEmpty)
        #expect(model.typeFields.isEmpty)
        #expect(model.statusOptions.map(\.id) == [todo, cancelled])
    }

    @Test func emptyTitleAndMissingRequiredFieldsBlockSubmitAndAreExplained() async {
        let source = StubCreateSource(vocabulary: vocabulary, created: TaskCreated(id: UUID(), seriesNumbers: []))
        let model = await ready(source: source)
        #expect(model.problem == nil)

        await model.submit()
        #expect(model.attempted)
        #expect(model.problem == model.localizer.string(.taskCreateTitleRequired))
        #expect(source.requests.isEmpty)

        model.title = "  Real  "
        #expect(model.problem == model.localizer.string(.taskCreateMissingFields, "Priority"))
        await model.submit()
        #expect(source.requests.isEmpty)

        model.setValues(priority, [high.uuidString.lowercased()])
        #expect(model.problem == nil)
        await model.submit()
        #expect(source.requests.count == 1)
    }

    @Test func invalidNumberIsReportedBeforeSending() async {
        let source = StubCreateSource(vocabulary: vocabulary, created: TaskCreated(id: UUID(), seriesNumbers: []))
        let model = await ready(source: source)
        model.title = "T"
        model.setValues(priority, ["x"])
        model.setValues(estimate, ["abc"])
        await model.submit()
        #expect(source.requests.isEmpty)
        #expect(model.problem?.contains("Estimate") == true)
    }

    @Test func payloadCarriesTypeStatusSeriesFieldsAndLinks() async {
        let source = StubCreateSource(vocabulary: vocabulary, created: TaskCreated(id: UUID(), seriesNumbers: [TaskSeriesRef(seriesId: series, number: 12)]))
        let epic = brief()
        let model = await ready(parent: epic, source: source)
        var created: (TaskCreated, String)?
        model.onCreated = { created = ($0, $1) }
        model.title = "New one"
        model.description = "Body"
        model.setStatus(doing)
        model.setValues(priority, [high.uuidString.lowercased()])
        model.setValues(component, [core.uuidString.lowercased(), web.uuidString.lowercased()])
        model.setValues(estimate, [" 5 "])
        let other = brief("Other")
        model.addLink(type: blocks, outward: true, task: other)
        model.addLink(type: blocks, outward: true, task: other)

        await model.submit()

        let request = source.requests.first
        #expect(request?.title == "New one")
        #expect(request?.description == "Body")
        #expect(request?.typeId == feature)
        #expect(request?.statusId == doing)
        #expect(request?.seriesIds == [series])
        #expect(request?.fieldOrder == [priority, component, estimate])
        #expect(request?.fields[estimate] == ["5"])
        #expect(request?.fields[component]?.count == 2)
        #expect(request?.links.count == 2)
        #expect(request?.links.first == DraftLink(typeId: parent, outward: false, task: epic))
        #expect(created?.1 == "DMO-12")
        #expect(!model.submitting)
    }

    @Test func serverFailureKeepsTheDraftAndShowsTheMessage() async {
        let source = StubCreateSource(vocabulary: vocabulary, created: TaskCreated(id: UUID(), seriesNumbers: []))
        let model = await ready(source: source)
        var closed = false
        model.onCreated = { _, _ in closed = true }
        model.title = "T"
        model.setValues(priority, [high.uuidString.lowercased()])
        source.failNext(.badRequest("Links: cycle"))

        await model.submit()

        #expect(!closed)
        #expect(model.serverError?.contains("Links: cycle") == true)
        #expect(model.title == "T")
        #expect(!model.submitting)

        await model.submit()
        #expect(closed)
        #expect(model.serverError == nil)
    }

    @Test func linksCanBeAddedRemovedAndGroupedBySide() async {
        let model = await ready()
        let a = brief("A")
        let b = brief("B")
        model.addLink(type: blocks, outward: true, task: a)
        model.addLink(type: blocks, outward: false, task: b)
        #expect(model.linkGroups.map(\.name) == ["blocks", "is blocked by"])
        #expect(model.linkedIds(type: blocks, outward: true) == [a.id])
        #expect(model.isDirty)
        model.removeLink(DraftLink(typeId: blocks, outward: true, task: a).id)
        #expect(model.linkGroups.map(\.name) == ["is blocked by"])
    }

    @Test func resizableHeightsStayWithinTheBounds() {
        #expect(TaskCreateModel.minLinksHeight < TaskCreateModel.defaultLinksHeight)
        #expect(TaskCreateModel.minDescriptionHeight > 0)
    }
}
