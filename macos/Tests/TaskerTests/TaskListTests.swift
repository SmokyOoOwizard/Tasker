import AppKit
import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubTaskListSource: TaskListSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _rows: [TaskRow]
    private var _children: [UUID: [TaskRow]]
    private var _failure: TaskerAPIError?
    private var _requests: [String] = []
    let vocabularyValue: TaskListVocabulary

    init(rows: [TaskRow], children: [UUID: [TaskRow]] = [:], vocabulary: TaskListVocabulary) {
        _rows = rows
        _children = children
        vocabularyValue = vocabulary
    }

    var rows: [TaskRow] {
        get { lock.withLock { _rows } }
        set { lock.withLock { _rows = newValue } }
    }

    var failure: TaskerAPIError? {
        get { lock.withLock { _failure } }
        set { lock.withLock { _failure = newValue } }
    }

    var requests: [String] { lock.withLock { _requests } }

    private func record(_ text: String) { lock.withLock { _requests.append(text) } }

    func vocabulary(project: UUID) async throws -> TaskListVocabulary { vocabularyValue }

    func flat(project: UUID, request: TaskListRequest, offset: Int, limit: Int) async throws -> TaskRowPage {
        let statuses = request.filter.statusIds
        record("flat \(offset) \(TaskSort.string(request.sort) ?? "")" + (statuses.map { " in \($0.count)" } ?? ""))
        let all = rows.filter { statuses?.contains($0.statusId) ?? true }
        let page = Array(all.dropFirst(offset).prefix(limit))
        return TaskRowPage(rows: page, totalCount: all.count, levelCount: all.count)
    }

    func tree(project: UUID, request: TaskListRequest, parent: UUID?, offset: Int, limit: Int) async throws -> TaskRowPage {
        record("tree \(parent.map { String($0.uuidString.prefix(4)) } ?? "root") \(offset)")
        let level = parent.map { id in lock.withLock { _children[id] ?? [] } } ?? rows
        let page = Array(level.dropFirst(offset).prefix(limit))
        return TaskRowPage(rows: page, totalCount: rows.count, levelCount: level.count)
    }

    func count(project: UUID, request: TaskListRequest, status: UUID) async throws -> Int {
        rows.filter { $0.statusId == status }.count
    }

    func setStatus(project: UUID, task: UUID, status: UUID, version: String) async throws {
        record("status \(task.uuidString.prefix(4))")
        if let failure { throw failure }
        rows = rows.map { $0.id == task ? { var row = $0; row.statusId = status; row.version = version + "+"; return row }($0) : $0 }
    }
}

@MainActor
@Suite struct TaskListTests {
    private let ru = Localizer(language: .ru)
    private let en = Localizer(language: .en)
    private let project = UUID()
    private let todo = UUID()
    private let doing = UUID()
    private let done = UUID()
    private let set = UUID()
    private let type = UUID()
    private let series = UUID()
    private let component = UUID()
    private let components = UUID()
    private let core = UUID()
    private let web = UUID()

    private var vocabulary: TaskListVocabulary {
        var value = TaskListVocabulary()
        value.statuses = [
            .init(id: todo, name: "Туду", color: "#2684ff"),
            .init(id: doing, name: "В работе", color: "#f5a623"),
            .init(id: done, name: "Готово", color: "#36b37e")
        ]
        value.statusSets = [set: [todo, doing, done]]
        value.types = [type: .init(name: "Задача", statusSetId: set)]
        value.typeOrder = [type]
        value.prefixes = [series: "TSK"]
        value.fields = [.init(id: component, name: "Компонент", type: .enum, multiple: true, enumId: components)]
        value.enums = [components: [.init(id: core.uuidString, name: "Core"), .init(id: web.uuidString, name: "Web")]]
        return value
    }

    private func row(_ number: Int, status: UUID? = nil, children: Int = 0, repeated: Bool = false, fields: [TaskRowField] = []) -> TaskRow {
        TaskRow(
            id: UUID(uuidString: String(format: "%08d-0000-0000-0000-000000000000", number))!,
            title: "Задача \(number)", typeId: type, statusId: status ?? todo,
            seriesNumbers: [TaskSeriesRef(seriesId: series, number: number)],
            createdAt: Date(timeIntervalSince1970: 1_800_000_000), updatedAt: Date(timeIntervalSince1970: 1_800_000_000 + Double(number)),
            version: "v\(number)", parentIds: [], childCount: children, fields: fields, repeated: repeated, treeChildCount: children)
    }

    private func settings() -> TaskListSettings {
        TaskListSettings(defaults: UserDefaults(suiteName: "TaskListTests-" + UUID().uuidString)!)
    }

    private func until(_ condition: @MainActor () -> Bool) async {
        for _ in 0..<400 {
            if condition() { return }
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    @Test func sortKeysCycleAscendingDescendingAndOff() {
        var keys: [TaskSortKey] = []
        keys = TaskSort.cycled(keys, "status")
        keys = TaskSort.cycled(keys, "updated")
        #expect(TaskSort.string(keys) == "status,updated")
        keys = TaskSort.cycled(keys, "updated")
        #expect(TaskSort.string(keys) == "status,-updated")
        keys = TaskSort.cycled(keys, "status")
        keys = TaskSort.cycled(keys, "status")
        #expect(TaskSort.string(keys) == "-updated")
        #expect(TaskSort.string(TaskSort.toggled(keys, "updated")) == "updated")
        #expect(TaskSort.string(TaskSort.removed(keys, "updated")) == nil)
        #expect(TaskSortField.isSortable(fieldName: "Оценка"))
        #expect(!TaskSortField.isSortable(fieldName: "Срок сдачи"))
        #expect(TaskSortField(key: "title").title(ru) == "Название")
        #expect(TaskSortField(key: "Оценка") == .field("Оценка"))
    }

    @Test func preferencesAreRememberedPerProject() throws {
        let defaults = try #require(UserDefaults(suiteName: "TaskListTests-" + UUID().uuidString))
        let first = TaskListSettings(defaults: defaults)
        first.update(project) {
            $0.mode = .tree
            $0.sort = [TaskSortKey(key: "updated", descending: true)]
            $0.columns.type = true
            $0.setExpanded(todo, true)
        }
        let other = UUID()
        let second = TaskListSettings(defaults: defaults)
        let restored = second.preferences(for: project)
        #expect(restored.mode == .tree)
        #expect(restored.sort.map(\.term) == ["-updated"])
        #expect(restored.columns.type)
        #expect(restored.expanded == [todo])
        #expect(second.preferences(for: other) == TaskListPreferences())

        var capped = TaskListPreferences()
        for _ in 0..<(TaskListPreferences.maxExpanded + 5) { capped.setExpanded(UUID(), true) }
        #expect(capped.expanded.count == TaskListPreferences.maxExpanded)
    }

    @Test func vocabularyDrawsStatusesByTheirPlaceInTheSetAndBuildsReferencesAndChips() {
        let vocabulary = vocabulary
        #expect(vocabulary.glyph(status: todo, type: type) == .named(.dashed))
        #expect(vocabulary.glyph(status: doing, type: type) == .progress(0.5))
        #expect(vocabulary.glyph(status: done, type: type) == .named(.done))
        #expect(vocabulary.statusOptions(forType: type).map(\.name) == ["Туду", "В работе", "Готово"])
        var alphabetical = vocabulary
        alphabetical.statuses.sort { $0.name < $1.name }
        alphabetical.statuses.append(.init(id: UUID(), name: "Вне наборов", color: "#8993a4"))
        #expect(alphabetical.orderedStatuses.map(\.name) == ["Туду", "В работе", "Готово", "Вне наборов"])

        var task = row(5, fields: [TaskRowField(fieldId: component, values: [web.uuidString.lowercased(), core.uuidString])])
        #expect(vocabulary.reference(task) == "TSK-5")
        #expect(vocabulary.chips(task, fieldIds: nil, limit: 3, l: ru).map(\.text) == ["Web", "Core"])
        #expect(vocabulary.chips(task, fieldIds: nil, limit: 1, l: ru).map(\.text) == ["Web"])
        #expect(vocabulary.chips(task, fieldIds: [], limit: 3, l: ru).isEmpty)
        #expect(vocabulary.chips(task, fieldIds: nil, limit: 3, l: ru).first?.colorHex == DesignTag.palette[2])
        task.seriesNumbers = []
        #expect(vocabulary.reference(task) == "#00000005")
    }

    @Test func flatListLoadsPagesOnDemandAndCountsFound() async {
        let source = StubTaskListSource(rows: (1...450).map { row($0) }, vocabulary: vocabulary)
        let model = TaskListModel(project: project, source: source, settings: settings())
        model.update { $0.mode = .flat }
        var changes: [TaskListChange] = []
        model.onChange = { changes.append($0) }
        model.start()
        await until { model.phase == .ready }
        #expect(model.roots.count == TaskListModel.pageSize)
        #expect(model.totalCount == 450)
        #expect(model.hasMore)
        model.loadMore()
        await until { model.roots.count == 400 }
        model.loadMore()
        await until { model.roots.count == 450 }
        #expect(!model.hasMore)
        #expect(source.requests.filter { $0.hasPrefix("flat") } == ["flat 0 ", "flat 200 ", "flat 400 "])
        #expect(changes.contains(.inserted(parent: nil, indexes: IndexSet(200..<400))))
    }

    @Test func everyStatusGroupPagesOnItsOwnSoAMovedTaskShowsUpInItsNewGroup() async {
        let rows = (1...250).map { row($0, status: todo) } + [row(251, status: doing), row(252, status: done)]
        let source = StubTaskListSource(rows: rows, vocabulary: vocabulary)
        let model = TaskListModel(project: project, source: source, settings: settings())
        model.update { $0.sort = [TaskSortKey(key: "updated", descending: true)] }
        model.start()
        await until { model.phase == .ready }
        #expect(model.mode == .groups)
        #expect(model.roots.map(\.groupStatus) == [todo, doing, done])
        #expect(model.roots.map(\.groupCount) == [250, 1, 1])
        #expect(model.roots.map { $0.children?.count } == [200, 1, 1])
        #expect(model.totalCount == 252)
        #expect(source.requests.filter { $0.hasPrefix("flat") }.allSatisfy { $0.hasSuffix("-updated in 1") })

        model.loadMore(group: model.roots[0])
        await until { model.roots[0].children?.count == 250 }
        #expect(model.roots[0].children?.count == 250)

        let moved = model.roots[0].children![0]
        model.changeStatus(moved, to: doing)
        await until { source.rows.contains { $0.id == moved.row?.id && $0.statusId == doing } }
        await model.refresh()
        #expect(model.roots.map(\.groupCount) == [249, 2, 1])
        #expect(model.roots[1].children?.contains { $0.row?.id == moved.row?.id } == true)
        #expect(model.roots[0].children?.contains { $0.row?.id == moved.row?.id } == false)

        model.setExpanded(model.roots[1], false)
        #expect(!model.shouldExpand(model.roots[1]))
        #expect(model.preferences.collapsedGroups == [doing])
        await model.refresh()
        #expect(model.roots[1].children?.isEmpty == true)
        model.setExpanded(model.roots[1], true)
        await until { model.roots[1].children?.count == 2 }
        #expect(model.roots[1].children?.count == 2)
    }

    @Test func treeLoadsChildrenOnExpansionAndRemembersIt() async {
        let epic = row(1, children: 2)
        let other = row(2, children: 1)
        let shared = row(3)
        let source = StubTaskListSource(rows: [epic, other], children: [epic.id: [row(4), shared], other.id: [{ var r = shared; r.repeated = true; return r }()]], vocabulary: vocabulary)
        let store = settings()
        let model = TaskListModel(project: project, source: source, settings: store)
        model.update { $0.mode = .tree }
        model.start()
        await until { model.phase == .ready }
        #expect(model.roots.map { $0.row?.id } == [epic.id, other.id])
        #expect(model.roots.allSatisfy { $0.children == nil && $0.isExpandable })
        model.setExpanded(model.roots[0], true)
        await until { model.roots[0].children != nil }
        #expect(model.roots[0].children?.map { $0.row?.id } == [row(4).id, shared.id])
        #expect(model.roots[0].children?.allSatisfy { $0.depth == 1 } == true)
        #expect(store.preferences(for: project).expanded == [epic.id])

        let again = TaskListModel(project: project, source: source, settings: store)
        again.start()
        await until { again.roots.first?.children != nil }
        #expect(again.roots[0].children?.count == 2)
        #expect(again.roots[1].children == nil)

        again.setExpanded(again.roots[1], true)
        await until { again.roots[1].children != nil }
        #expect(again.roots[1].children?.first?.row?.repeated == true)
        #expect(again.roots[1].children?.first?.key != again.roots[0].children?.last?.key)
    }

    @Test func outlineRemembersExpansionAndShowsLoadedChildren() async throws {
        let epic = row(1, children: 2)
        let source = StubTaskListSource(rows: [epic, row(2)], children: [epic.id: [row(3), row(4)]], vocabulary: vocabulary)
        let store = settings()
        let model = TaskListModel(project: project, source: source, settings: store)
        model.update { $0.mode = .tree }
        model.start()
        await until { model.phase == .ready }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 400), styleMask: [.titled], backing: .buffered, defer: false)
        let coordinator = TaskOutlineCoordinator(model: model)
        window.contentView = TaskOutline.makeScrollView(coordinator: coordinator, localizer: ru)
        let outline = try #require((window.contentView as? NSScrollView)?.documentView as? NSOutlineView)
        #expect(outline.numberOfRows == 2)
        outline.expandItem(model.roots[0])
        await until { outline.numberOfRows == 4 }
        #expect(outline.numberOfRows == 4)
        #expect(outline.isItemExpanded(model.roots[0]))
        #expect(store.preferences(for: project).expanded == [epic.id])
        #expect(outline.level(forRow: 1) == 1)

        model.visibleCount = 2
        await model.refresh()
        #expect(outline.isItemExpanded(model.roots[0]))
        #expect(store.preferences(for: project).expanded == [epic.id])
        outline.collapseItem(model.roots[0])
        #expect(store.preferences(for: project).expanded.isEmpty)
        coordinator.detach()
    }

    @Test func groupHeaderSticksOnlyWhileScrollingThroughItsOpenTasks() async throws {
        let source = StubTaskListSource(rows: (1...60).map { row($0, status: todo) } + (61...70).map { row($0, status: doing) }, vocabulary: vocabulary)
        let model = TaskListModel(project: project, source: source, settings: settings())
        model.start()
        await until { model.phase == .ready }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 400), styleMask: [.titled], backing: .buffered, defer: false)
        let coordinator = TaskOutlineCoordinator(model: model)
        window.contentView = TaskOutline.makeScrollView(coordinator: coordinator, localizer: ru)
        let scroll = try #require(window.contentView as? NSScrollView)
        let outline = try #require(scroll.documentView as? NSOutlineView)
        #expect(!outline.floatsGroupRows)
        #expect(coordinator.stickyGroup == nil)

        func move(to y: CGFloat) {
            scroll.contentView.scroll(to: NSPoint(x: 0, y: y))
            scroll.reflectScrolledClipView(scroll.contentView)
            coordinator.checkEnd()
        }
        move(to: DesignLayout.taskRowHeight * 20)
        #expect(coordinator.stickyGroup === model.roots[0])
        move(to: 0)
        #expect(coordinator.stickyGroup == nil)
        move(to: outline.rect(ofRow: outline.row(forItem: model.roots[1])).minY + 1)
        #expect(coordinator.stickyGroup == nil)
        move(to: outline.rect(ofRow: outline.row(forItem: model.roots[1])).minY + DesignLayout.groupRowHeight + 4)
        #expect(coordinator.stickyGroup === model.roots[1])
        outline.collapseItem(model.roots[1])
        coordinator.checkEnd()
        #expect(coordinator.stickyGroup !== model.roots[1])
        coordinator.detach()
    }

    @Test func liveRefreshUpdatesChangedRowsInPlaceAndReloadsOnlyWhenTheOrderChanges() async {
        let source = StubTaskListSource(rows: [row(1), row(2), row(3)], vocabulary: vocabulary)
        let model = TaskListModel(project: project, source: source, settings: settings())
        model.update { $0.mode = .flat }
        var changes: [TaskListChange] = []
        model.onChange = { changes.append($0) }
        model.start()
        await until { model.phase == .ready }
        let second = model.roots[1]
        changes = []

        var renamed = source.rows
        renamed[1].title = "Переименована"
        renamed[1].version = "v2+"
        source.rows = renamed
        await model.refresh()
        #expect(model.roots[1] === second)
        #expect(second.row?.title == "Переименована")
        #expect(changes == [.rows([second])])

        changes = []
        source.rows = [source.rows[2], source.rows[0], source.rows[1], row(4)]
        await model.refresh()
        #expect(changes == [.reload])
        #expect(model.roots.map { $0.row?.id } == [row(3).id, row(1).id, row(2).id, row(4).id])
        #expect(model.roots[2] === second)
        #expect(model.totalCount == 4)
    }

    @Test func liveRefreshRereadsOnlyThePagesUpToTheVisibleRowsInParallel() async {
        let source = StubTaskListSource(rows: (1...450).map { row($0) }, vocabulary: vocabulary)
        let model = TaskListModel(project: project, source: source, settings: settings())
        model.update { $0.mode = .flat }
        model.start()
        await until { model.phase == .ready }
        model.loadMore()
        await until { model.roots.count == 400 }
        model.loadMore()
        await until { model.roots.count == 450 }

        model.visibleCount = 420
        await model.refresh()
        #expect(model.roots.count == 450)
        #expect(Set(source.requests.suffix(3)) == ["flat 0 ", "flat 200 ", "flat 400 "])

        model.visibleCount = 30
        await model.refresh()
        #expect(model.roots.count == TaskListModel.pageSize)
        #expect(model.hasMore)
        #expect(source.requests.last == "flat 0 ")
        #expect(model.totalCount == 450)
    }

    @Test func statusChangeIsOptimisticAndExplainsRefusals() async {
        let source = StubTaskListSource(rows: [row(1), row(2)], vocabulary: vocabulary)
        let model = TaskListModel(project: project, source: source, settings: settings())
        model.update { $0.mode = .flat }
        model.localizer = ru
        var notices: [String] = []
        model.onFailure = { notices.append($0) }
        model.start()
        await until { model.phase == .ready }

        model.changeStatus(model.roots[0], to: doing)
        #expect(model.roots[0].row?.statusId == doing)
        await until { source.requests.contains { $0.hasPrefix("status") } }
        #expect(source.rows[0].statusId == doing)

        source.failure = TaskerAPIError.locked(by: "Claude")
        model.changeStatus(model.roots[1], to: done)
        #expect(model.roots[1].row?.statusId == done)
        await until { !notices.isEmpty }
        #expect(model.roots[1].row?.statusId == todo)
        #expect(notices == ["Не удалось сменить статус TSK-2: «Item» сейчас редактирует Claude."])
    }

    @Test func statusRefusalsReadLikeSentences() {
        #expect(TaskListModel.statusFailure(TaskerAPIError.modified(), reference: "TSK-7", l: ru)
            == "Не удалось сменить статус TSK-7: задачу только что изменили, список обновлён")
        #expect(TaskListModel.statusFailure(TaskerAPIError.badRequest(Refusal(code: "validation.invalid", message: "Status is not in the set of the type")), reference: "TSK-7", l: en)
            == "Couldn't change the status of TSK-7: Status is not in the set of the type")
        #expect(TaskListModel.statusFailure(TaskerAPIError.notFound(Refusal(message: "gone")), reference: "TSK-7", l: ru) == "Не удалось сменить статус TSK-7: задача удалена")
        #expect(TaskListModel.statusFailure(TaskerAPIError.timedOut, reference: "TSK-7", l: ru) == "Не удалось сменить статус TSK-7: нет связи с демоном")
        #expect(TaskListModel.statusFailure(TaskerAPIError.forbidden(Refusal(code: "validation.invalid", message: "read only")), reference: "TSK-7", l: en) == "You can't change the status of TSK-7: read only")
    }
}
