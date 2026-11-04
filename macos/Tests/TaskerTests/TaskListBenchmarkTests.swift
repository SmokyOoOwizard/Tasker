import AppKit
import Foundation
import Testing
import TaskerAPI
@testable import Tasker

@MainActor
@Suite(.enabled(if: ProcessInfo.processInfo.environment["TASKER_BENCH"] != nil), .serialized)
struct TaskListBenchmarkTests {
    private let project = UUID()
    private let type = UUID()
    private let set = UUID()
    private let series = UUID()
    private let component = UUID()
    private let components = UUID()
    private let statuses = (0..<5).map { _ in UUID() }
    private let values = (0..<6).map { _ in UUID() }

    private var vocabulary: TaskListVocabulary {
        var value = TaskListVocabulary()
        value.statuses = statuses.enumerated().map { .init(id: $1, name: "Статус \($0)", color: DesignTag.palette[$0 + 1]) }
        value.statusSets = [set: statuses]
        value.types = [type: .init(name: "Задача", statusSetId: set)]
        value.prefixes = [series: "BEN"]
        value.fields = [.init(id: component, name: "Компонент", type: .enum, multiple: true, enumId: components)]
        value.enums = [components: values.enumerated().map { .init(id: $1.uuidString, name: "Компонент \($0)") }]
        return value
    }

    private func rows(_ count: Int) -> [TaskRow] {
        (1...count).map { number in
            TaskRow(
                id: UUID(), title: "Задача номер \(number) с достаточно длинным заголовком, чтобы его пришлось обрезать",
                typeId: type, statusId: statuses[number % statuses.count],
                seriesNumbers: [TaskSeriesRef(seriesId: series, number: number)],
                createdAt: .now, updatedAt: .now, version: "1", parentIds: [], childCount: 0,
                fields: [TaskRowField(fieldId: component, values: [values[number % 6].uuidString, values[(number + 2) % 6].uuidString])])
        }
    }

    private static func milliseconds(_ duration: Duration) -> Double {
        Double(duration.components.seconds) * 1000 + Double(duration.components.attoseconds) / 1e15
    }

    private static func report(_ name: String, _ samples: [Double]) -> (median: Double, p95: Double, max: Double) {
        let sorted = samples.sorted()
        let result = (sorted[sorted.count / 2], sorted[Int(Double(sorted.count - 1) * 0.95)], sorted.last ?? 0)
        print(String(format: "BENCH %@: n=%d median=%.2f ms p95=%.2f ms max=%.2f ms", name, samples.count, result.0, result.1, result.2))
        return result
    }

    private func loaded(_ mode: TaskListMode, count: Int) async -> TaskListModel {
        let source = StubTaskListSource(rows: rows(count), vocabulary: vocabulary)
        let settings = TaskListSettings(defaults: UserDefaults(suiteName: "TaskListBench-" + UUID().uuidString)!)
        let model = TaskListModel(project: project, source: source, settings: settings)
        model.update { $0.mode = mode }
        model.start()
        while model.phase != .ready { try? await Task.sleep(for: .milliseconds(1)) }
        while model.hasMore {
            let before = model.roots.count + model.allNodes().count
            model.loadMore()
            while model.loadingMore || model.roots.count + model.allNodes().count == before { try? await Task.sleep(for: .milliseconds(1)) }
        }
        return model
    }

    private func scrollThrough(_ mode: TaskListMode) async {
        let model = await loaded(mode, count: 10_000)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 960, height: 640), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: .darkAqua)
        let coordinator = TaskOutlineCoordinator(model: model)
        let clock = ContinuousClock()
        let opened = clock.measure {
            window.contentView = TaskOutline.makeScrollView(coordinator: coordinator, localizer: Localizer(language: .ru))
            window.layoutIfNeeded()
            window.displayIfNeeded()
        }
        print(String(format: "BENCH %@ open with 10000 rows loaded: %.2f ms", mode.rawValue, Self.milliseconds(opened)))
        guard let scroll = window.contentView as? NSScrollView, let outline = scroll.documentView as? NSOutlineView else { return }
        #expect(outline.numberOfRows >= 10_000)
        let clip = scroll.contentView
        let end = outline.bounds.height - clip.bounds.height
        var frames: [Double] = []
        var y: CGFloat = 0
        while y < end {
            y = min(end, y + DesignLayout.taskRowHeight * 3)
            let frame = clock.measure {
                clip.scroll(to: NSPoint(x: 0, y: y))
                scroll.reflectScrolledClipView(clip)
                window.displayIfNeeded()
            }
            frames.append(Self.milliseconds(frame))
        }
        var jumps: [Double] = []
        for step in 0..<40 {
            let target = end * CGFloat((step * 37) % 40) / 40
            jumps.append(Self.milliseconds(clock.measure {
                clip.scroll(to: NSPoint(x: 0, y: target))
                scroll.reflectScrolledClipView(clip)
                window.displayIfNeeded()
            }))
        }
        let smooth = Self.report("\(mode.rawValue) scroll by 3 rows", frames)
        _ = Self.report("\(mode.rawValue) jump to a random place", jumps)
        #expect(smooth.p95 < 16.7)
        coordinator.detach()
    }

    @Test func flatListScrollsTenThousandRows() async {
        await scrollThrough(.flat)
    }

    @Test func groupedListScrollsTenThousandRows() async {
        await scrollThrough(.groups)
    }

    @Test func liveDaemonAnswersFastEnough() async throws {
        guard let port = ProcessInfo.processInfo.environment["TASKER_BENCH_PORT"].flatMap(Int.init) else { return }
        let workspace = ProcessInfo.processInfo.environment["TASKER_BENCH_WORKSPACE"] ?? "ws"
        let client = TaskerAPIClient(endpoint: .local(port: port, workspace: workspace))
        let projectId = try #require(try await client.graphQL.fetch(ProjectsQuery(limit: 1)).projects.data.first?.id)
        let source = GraphQLTaskListSource(client: client)
        let clock = ContinuousClock()
        func time(_ name: String, runs: Int = 5, _ body: () async throws -> Void) async throws {
            var samples: [Double] = []
            for _ in 0..<runs { samples.append(Self.milliseconds(try await clock.measure { try await body() })) }
            _ = Self.report(name, samples)
        }
        let request = TaskListRequest()
        let grouped = TaskListRequest(sort: TaskSort.grouped([]))
        let vocabulary = try await source.vocabulary(project: projectId)
        try await time("daemon vocabulary") { _ = try await source.vocabulary(project: projectId) }
        try await time("daemon flat first page (200)") { _ = try await source.flat(project: projectId, request: request, offset: 0, limit: 200) }
        try await time("daemon flat page at offset 9800") { _ = try await source.flat(project: projectId, request: request, offset: 9800, limit: 200) }
        try await time("daemon grouped first page (200)") { _ = try await source.flat(project: projectId, request: grouped, offset: 0, limit: 200) }
        try await time("daemon group counts (all statuses)") {
            for status in vocabulary.statuses { _ = try await source.count(project: projectId, request: grouped, status: status.id) }
        }
        try await time("daemon sorted page -updated,title") {
            _ = try await source.flat(project: projectId, request: TaskListRequest(sort: [TaskSortKey(key: "updated", descending: true), TaskSortKey(key: "title", descending: false)]), offset: 0, limit: 200)
        }
        let roots = try await source.tree(project: projectId, request: request, parent: nil, offset: 0, limit: 200)
        try await time("daemon tree roots (200)") { _ = try await source.tree(project: projectId, request: request, parent: nil, offset: 0, limit: 200) }
        let epic = try #require(roots.rows.first { $0.treeChildCount > 0 })
        try await time("daemon tree children of an epic (\(epic.treeChildCount))") { _ = try await source.tree(project: projectId, request: request, parent: epic.id, offset: 0, limit: 200) }
        let settings = TaskListSettings(defaults: UserDefaults(suiteName: "TaskListBench-" + UUID().uuidString)!)
        let model = TaskListModel(project: projectId, source: source, settings: settings)
        model.update { $0.mode = .flat }
        let opened = try await clock.measure {
            model.start()
            while model.phase != .ready { try await Task.sleep(for: .milliseconds(1)) }
        }
        print(String(format: "BENCH daemon list opened (vocabulary + first page): %.2f ms", Self.milliseconds(opened)))
        let all = try await clock.measure {
            while model.hasMore {
                let before = model.roots.count
                model.loadMore()
                while model.loadingMore || model.roots.count == before { try await Task.sleep(for: .milliseconds(1)) }
            }
        }
        print(String(format: "BENCH daemon scrolled to the end, 10000 rows in 50 pages: %.2f ms", Self.milliseconds(all)))
        model.visibleCount = 10_000
        try await time("daemon live refresh, scrolled to the end (50 pages, 6 at a time)", runs: 3) { await model.refresh() }
        model.visibleCount = 30
        try await time("daemon live refresh, top of the list (1 page)") { await model.refresh() }
    }
}
