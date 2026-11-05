import Foundation
import Observation
import TaskerAPI

enum SettingsPhase: Equatable {
    case loading
    case ready
    case failed(String)
}

struct EnumUsage: Equatable, Sendable {
    var perValue: [UUID: Int] = [:]
    var complete = true
    var columns: [UUID: Int] = [:]

    func count(_ id: UUID) -> Int { perValue[id] ?? 0 }
}

@MainActor
@Observable
final class SettingsModel {
    static let heartbeat: Duration = .seconds(40)
    static let usageCap = 5_000

    let project: UUID

    private(set) var phase: SettingsPhase = .loading
    private(set) var catalog: SettingsCatalog?
    private(set) var mode: ProjectMode = .local
    private(set) var stats: [UUID: SettingsSeriesStat] = [:]
    private(set) var members: [SettingsMember] = []
    private(set) var held: SettingsLockTarget?
    private(set) var busy = false

    @ObservationIgnored var localizer = Localizer(choice: .system)
    @ObservationIgnored var onNotice: ((String) -> Void)?
    @ObservationIgnored var onProjectDeleted: (() -> Void)?
    @ObservationIgnored private let source: any SettingsSource
    @ObservationIgnored private let heartbeat: Duration
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var beatTask: Task<Void, Never>?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var released = false

    init(project: UUID, source: any SettingsSource, heartbeat: Duration = SettingsModel.heartbeat) {
        self.project = project
        self.source = source
        self.heartbeat = heartbeat
    }

    var pages: [SettingsPage] { SettingsPage.pages(in: mode) }

    var prefixes: [UUID: String] {
        Dictionary((catalog?.series ?? []).map { ($0.id, $0.prefix) }, uniquingKeysWith: { first, _ in first })
    }

    var isEditing: Bool { held != nil }

    func load() {
        generation += 1
        let current = generation
        loadTask?.cancel()
        loadTask = Task { [weak self] in await self?.fetch(current) }
    }

    func reload() async {
        generation += 1
        await fetch(generation)
    }

    func stop() {
        generation += 1
        loadTask?.cancel()
        loadTask = nil
    }

    private func fetch(_ current: Int) async {
        do {
            async let loadedMode = source.mode()
            async let loaded = source.catalog(project: project)
            let (newMode, newCatalog) = try await (loadedMode, loaded)
            let ids = newCatalog.series.map(\.id)
            let newStats = (try? await source.seriesStats(project: project, series: ids)) ?? [:]
            let newMembers = newMode == .server ? ((try? await source.members(project: project)) ?? []) : []
            guard current == generation else { return }
            mode = newMode
            catalog = newCatalog
            stats = newStats
            members = newMembers
            phase = .ready
        } catch is CancellationError {
        } catch {
            guard current == generation else { return }
            if catalog == nil { phase = .failed(Self.reason(error)) }
        }
    }

    func message(_ error: any Error) -> String {
        Self.failure(error, l: localizer)
    }

    static func failure(_ error: any Error, l: Localizer) -> String {
        guard let error = error as? TaskerAPIError else { return l.string(.settingsFailed, error.localizedDescription) }
        if case .conflict(let conflict) = error {
            switch conflict.kind {
            case .modified: return l.string(.settingsModified)
            case .locked: return l.string(.settingsLockedBy, conflict.heldBy?.name ?? conflict.message)
            case .inUse: return l.string(.settingsInUse, conflict.message)
            default: break
            }
        }
        return l.string(.settingsFailed, error.message)
    }

    static func isInUse(_ error: any Error) -> String? {
        if case TaskerAPIError.conflict(let conflict) = error, conflict.kind == .inUse { return conflict.message }
        return nil
    }

    func hold(_ target: SettingsLockTarget) async -> String? {
        guard !released else { return nil }
        if held != nil { await drop() }
        do {
            try await source.lock(project: project, target: target)
        } catch {
            if case TaskerAPIError.conflict(let conflict) = error, conflict.kind == .locked {
                return localizer.string(.settingsLockedBy, conflict.heldBy?.name ?? conflict.message)
            }
            return nil
        }
        if released {
            try? await source.unlock(project: project, target: target)
            return nil
        }
        held = target
        startBeat(target)
        return nil
    }

    private func startBeat(_ target: SettingsLockTarget) {
        beatTask?.cancel()
        let interval = heartbeat
        beatTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: interval)
                if Task.isCancelled { return }
                guard let self, self.held == target else { return }
                try? await self.source.lock(project: self.project, target: target)
            }
        }
    }

    func drop() async {
        beatTask?.cancel()
        beatTask = nil
        guard let target = held else { return }
        held = nil
        try? await source.unlock(project: project, target: target)
    }

    func dropSoon() {
        Task { await drop() }
    }

    func release() {
        guard !released else { return }
        released = true
        stop()
        dropSoon()
    }

    func releaseNow() async {
        released = true
        stop()
        await drop()
    }

    private func run(_ notice: L10nKey?, _ work: () async throws -> Void) async -> String? {
        guard !busy else { return nil }
        busy = true
        defer { busy = false }
        do {
            try await work()
        } catch {
            return message(error)
        }
        if let notice { onNotice?(localizer.string(notice)) }
        await reload()
        return nil
    }

    private func attempt(_ work: () async throws -> Void) async -> Result<Void, any Error> {
        guard !busy else { return .failure(CancellationError()) }
        busy = true
        defer { busy = false }
        do {
            try await work()
            return .success(())
        } catch {
            return .failure(error)
        }
    }

    func affectedTasks(of field: SettingsFieldItem) async -> SettingsTaskPage? {
        try? await source.tasks(project: project, filter: ["\(field.name):set"], cap: Self.usageCap)
    }

    func usage(of field: SettingsFieldItem) async -> [SettingsUsage] {
        guard let catalog else { return [] }
        var uses: [SettingsUsage] = []
        for type in catalog.types where type.fieldIds.contains(field.id) {
            uses.append(SettingsUsage(kind: .type, name: type.name))
        }
        if let page = await affectedTasks(of: field), page.total > 0 {
            let prefixes = prefixes
            let shown = page.items.prefix(6).map { SettingsRules.reference($0, prefixes: prefixes) }
            uses.append(SettingsUsage(kind: .tasks(page.total), name: shown.joined(separator: ", ") + (page.total > 6 ? " …" : "")))
        }
        for condition in catalog.conditions where condition.fieldId == field.id {
            uses.append(SettingsUsage(kind: .board(condition.board), name: condition.column))
        }
        return uses
    }

    func enumUsage(_ item: SettingsEnumItem) async -> EnumUsage {
        var usage = EnumUsage()
        guard let catalog else { return usage }
        for field in catalog.fields(of: item.id) {
            guard let page = try? await source.tasks(project: project, filter: ["\(field.name):set"], cap: Self.usageCap) else {
                usage.complete = false
                continue
            }
            if !page.isComplete { usage.complete = false }
            for task in page.items {
                for raw in task.values(of: field.id) {
                    guard let id = UUID(uuidString: raw) else { continue }
                    usage.perValue[id, default: 0] += 1
                }
            }
        }
        let fieldIds = Set(catalog.fields(of: item.id).map(\.id))
        let valueIds = Dictionary(item.values.map { ($0.id.uuidString.lowercased(), $0.id) }, uniquingKeysWith: { first, _ in first })
        for condition in catalog.conditions where fieldIds.contains(condition.fieldId) {
            if let value = condition.value, let id = valueIds[value.lowercased()] { usage.columns[id, default: 0] += 1 }
        }
        return usage
    }

    func tasksWithValues(of field: SettingsFieldItem) async -> [SettingsTaskBrief] {
        await affectedTasks(of: field)?.items ?? []
    }

    func createField(name: String, type: FieldType, multiple: Bool, enumId: UUID?) async -> String? {
        await run(.settingsFieldSaved) { try await source.createField(project: project, name: name, type: type, multiple: multiple, enumId: enumId) }
    }

    func updateField(
        _ field: SettingsFieldItem, name: String, type: FieldType, multiple: Bool, enumId: UUID?, change: FieldChangeRequest?, affected: Int
    ) async -> String? {
        guard !busy else { return nil }
        busy = true
        defer { busy = false }
        do {
            try await source.updateField(project: project, field: field, name: name, type: type, multiple: multiple, enumId: enumId, change: change)
        } catch {
            return message(error)
        }
        onNotice?(affected > 0 ? localizer.string(.settingsFieldChanged, String(affected)) : localizer.string(.settingsFieldSaved))
        await reload()
        return nil
    }

    func deleteField(_ field: SettingsFieldItem) async -> Result<Void, any Error> {
        let result = await attempt { try await source.deleteField(project: project, field: field) }
        if case .success = result {
            onNotice?(localizer.string(.settingsFieldDeleted))
            await reload()
        }
        return result
    }

    func createEnum(name: String, values: [String]) async -> String? {
        await run(.settingsEnumSaved) { try await source.createEnum(project: project, name: name, values: values) }
    }

    func updateEnum(_ item: SettingsEnumItem, name: String, values: [EnumValueDraft], removal: EnumRemovalChoice?) async -> String? {
        await run(.settingsEnumSaved) { try await source.updateEnum(project: project, item: item, name: name, values: values, removal: removal) }
    }

    func deleteEnum(_ item: SettingsEnumItem) async -> Result<Void, any Error> {
        let result = await attempt { try await source.deleteEnum(project: project, item: item) }
        if case .success = result {
            onNotice?(localizer.string(.settingsEnumDeleted))
            await reload()
        }
        return result
    }

    func createLinkType(name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async -> String? {
        await run(.settingsLinkTypeSaved) {
            try await source.createLinkType(project: project, name: name, outward: outward, inward: inward, allowCycles: allowCycles, hierarchical: hierarchical)
        }
    }

    func updateLinkType(_ item: SettingsLinkTypeItem, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async -> String? {
        await run(.settingsLinkTypeSaved) {
            try await source.updateLinkType(project: project, item: item, name: name, outward: outward, inward: inward, allowCycles: allowCycles, hierarchical: hierarchical)
        }
    }

    func deleteLinkType(_ item: SettingsLinkTypeItem) async -> Result<Void, any Error> {
        let result = await attempt { try await source.deleteLinkType(project: project, item: item) }
        if case .success = result {
            onNotice?(localizer.string(.settingsLinkTypeDeleted))
            await reload()
        }
        return result
    }

    func statusUsage(of item: SettingsStatusItem) async -> [SettingsUsage] {
        guard let catalog else { return [] }
        var uses: [SettingsUsage] = []
        if let page = try? await source.tasks(project: project, filter: [], statusIds: [item.id], typeIds: nil, cap: Self.usageCap), page.total > 0 {
            let prefixes = prefixes
            let shown = page.items.prefix(6).map { SettingsRules.reference($0, prefixes: prefixes) }
            uses.append(SettingsUsage(kind: .tasks(page.total), name: shown.joined(separator: ", ") + (page.total > 6 ? " …" : "")))
        }
        for set in catalog.statusSets where set.statusIds.contains(item.id) {
            uses.append(SettingsUsage(kind: .statusSet, name: set.name))
        }
        for board in catalog.boards {
            for column in board.columns where column.statusIds.contains(item.id) || column.dropStatusIds.contains(item.id) {
                uses.append(SettingsUsage(kind: .board(board.name), name: column.name))
            }
        }
        return uses
    }

    func statusRemovalUsage(from set: SettingsStatusSetItem, lost: [UUID]) async -> [SettingsUsage] {
        guard let catalog, !lost.isEmpty else { return [] }
        let typeIds = catalog.types.filter { $0.statusSetId == set.id }.map(\.id)
        guard !typeIds.isEmpty,
              let page = try? await source.tasks(project: project, filter: [], statusIds: lost, typeIds: typeIds, cap: Self.usageCap), page.total > 0
        else { return [] }
        let prefixes = prefixes
        let shown = page.items.prefix(6).map { SettingsRules.reference($0, prefixes: prefixes) }
        return [SettingsUsage(kind: .tasks(page.total), name: shown.joined(separator: ", ") + (page.total > 6 ? " …" : ""))]
    }

    func statusSetUsage(of item: SettingsStatusSetItem) -> [SettingsUsage] {
        guard let catalog else { return [] }
        var uses: [SettingsUsage] = []
        for type in catalog.types where type.statusSetId == item.id {
            uses.append(SettingsUsage(kind: .type, name: type.name))
        }
        for board in catalog.boards where board.statusSetIds.contains(item.id) {
            uses.append(SettingsUsage(kind: .boardName, name: board.name))
        }
        return uses
    }

    func taskTypeUsage(of item: SettingsTypeItem) async -> [SettingsUsage] {
        guard let page = try? await source.tasks(project: project, filter: [], statusIds: nil, typeIds: [item.id], cap: Self.usageCap), page.total > 0 else { return [] }
        let prefixes = prefixes
        let shown = page.items.prefix(6).map { SettingsRules.reference($0, prefixes: prefixes) }
        return [SettingsUsage(kind: .tasks(page.total), name: shown.joined(separator: ", ") + (page.total > 6 ? " …" : ""))]
    }

    func holdersOfRemovedFields(in item: SettingsTypeItem, fields removed: [UUID]) async -> (names: [String], tasks: Int)? {
        guard let catalog else { return nil }
        var names: [String] = []
        var holders = Set<UUID>()
        for id in removed {
            guard let field = catalog.field(id) else { continue }
            guard let page = try? await source.tasks(project: project, filter: ["\(field.name):set"], statusIds: nil, typeIds: [item.id], cap: Self.usageCap) else {
                return nil
            }
            if page.total > 0 {
                names.append(field.name)
                holders.formUnion(page.items.map(\.id))
                if !page.isComplete { holders.insert(UUID()) }
            }
        }
        return (names, holders.count)
    }

    func createStatus(name: String, color: String, description: String, icon: String) async -> String? {
        await run(.settingsStatusSaved) { try await source.createStatus(project: project, name: name, color: color, description: description, icon: icon) }
    }

    func updateStatus(_ item: SettingsStatusItem, name: String, color: String, description: String, icon: String) async -> String? {
        await run(.settingsStatusSaved) { try await source.updateStatus(project: project, item: item, name: name, color: color, description: description, icon: icon) }
    }

    func deleteStatus(_ item: SettingsStatusItem) async -> Result<Void, any Error> {
        let result = await attempt { try await source.deleteStatus(project: project, item: item) }
        if case .success = result {
            onNotice?(localizer.string(.settingsStatusDeleted))
            await reload()
        }
        return result
    }

    func createStatusSet(name: String, statusIds: [UUID]) async -> String? {
        await run(.settingsStatusSetSaved) { try await source.createStatusSet(project: project, name: name, statusIds: statusIds) }
    }

    func updateStatusSet(_ item: SettingsStatusSetItem, name: String, statusIds: [UUID]) async -> String? {
        await run(.settingsStatusSetSaved) { try await source.updateStatusSet(project: project, item: item, name: name, statusIds: statusIds) }
    }

    func deleteStatusSet(_ item: SettingsStatusSetItem) async -> Result<Void, any Error> {
        let result = await attempt { try await source.deleteStatusSet(project: project, item: item) }
        if case .success = result {
            onNotice?(localizer.string(.settingsStatusSetDeleted))
            await reload()
        }
        return result
    }

    func createTaskType(name: String, description: String, statusSetId: UUID, fields: [SettingsTypeField]) async -> String? {
        await run(.settingsTypeSaved) { try await source.createTaskType(project: project, name: name, description: description, statusSetId: statusSetId, fields: fields) }
    }

    func updateTaskType(
        _ item: SettingsTypeItem, name: String, description: String, statusSetId: UUID, fields: [SettingsTypeField], removal: RemovedFieldChoice?
    ) async -> String? {
        guard !busy else { return nil }
        busy = true
        defer { busy = false }
        let affected: Int
        do {
            affected = try await source.updateTaskType(
                project: project, item: item, name: name, description: description, statusSetId: statusSetId, fields: fields, removal: removal)
        } catch {
            return message(error)
        }
        onNotice?(affected > 0 ? localizer.string(.settingsTypeChanged, String(affected)) : localizer.string(.settingsTypeSaved))
        await reload()
        return nil
    }

    func deleteTaskType(_ item: SettingsTypeItem) async -> Result<Void, any Error> {
        let result = await attempt { try await source.deleteTaskType(project: project, item: item) }
        if case .success = result {
            onNotice?(localizer.string(.settingsTypeDeleted))
            await reload()
        }
        return result
    }

    func createSeries(name: String, prefix: String) async -> String? {
        await run(.seriesSaved) { try await source.createSeries(project: project, name: name, prefix: prefix) }
    }

    func updateSeries(_ item: SettingsSeriesItem, name: String, prefix: String) async -> String? {
        await run(.seriesSaved) { try await source.updateSeries(project: project, item: item, name: name, prefix: prefix) }
    }

    func deleteSeries(_ item: SettingsSeriesItem) async -> String? {
        await run(.settingsSeriesDeleted) { try await source.deleteSeries(project: project, item: item) }
    }

    func renameProject(_ name: String) async -> String? {
        guard let item = catalog?.project else { return nil }
        return await run(.settingsProjectRenamed) { try await source.renameProject(project: item, name: name) }
    }

    func deleteProject() async -> String? {
        guard let item = catalog?.project else { return nil }
        guard !busy else { return nil }
        busy = true
        defer { busy = false }
        do {
            try await source.deleteProject(project: item)
        } catch {
            return message(error)
        }
        onNotice?(localizer.string(.settingsProjectDeleted))
        onProjectDeleted?()
        return nil
    }

    func candidates() async -> [SettingsMember] {
        let all = (try? await source.users()) ?? []
        let present = Set(members.map(\.id))
        return all.filter { !present.contains($0.id) }
    }

    func addMember(_ user: SettingsMember) async -> String? {
        await run(.settingsMemberAdded) { try await source.addMember(project: project, user: user.id) }
    }

    func removeMember(_ user: SettingsMember) async -> String? {
        guard SettingsRules.canRemoveMember(user, among: members) else { return localizer.string(.settingsMemberLast) }
        return await run(.settingsMemberRemoved) { try await source.removeMember(project: project, user: user.id) }
    }

    private static func reason(_ error: any Error) -> String {
        (error as? TaskerAPIError)?.message ?? error.localizedDescription
    }
}

struct SettingsUsage: Hashable, Sendable, Identifiable {
    enum Kind: Hashable, Sendable {
        case type
        case tasks(Int)
        case links(Int)
        case board(String)
        case field
        case statusSet
        case boardName
    }

    var kind: Kind
    var name: String

    var id: String { "\(kind)|\(name)" }
}

enum SettingsPage: String, CaseIterable, Identifiable, Sendable {
    case statuses
    case statusSets
    case types
    case series
    case linkTypes
    case fields
    case enums
    case members
    case project

    var id: String { rawValue }

    var titleKey: L10nKey {
        switch self {
        case .statuses: .settingsPageStatuses
        case .statusSets: .settingsPageStatusSets
        case .types: .settingsPageTypes
        case .series: .settingsPageSeries
        case .linkTypes: .settingsPageLinkTypes
        case .fields: .settingsPageFields
        case .enums: .settingsPageEnums
        case .members: .settingsPageMembers
        case .project: .settingsPageProject
        }
    }

    var keywords: String {
        switch self {
        case .statuses: "statuses status color icon"
        case .statusSets: "status sets workflow"
        case .types: "task types fields"
        case .series: "series prefix"
        case .linkTypes: "link types relations"
        case .fields: "fields custom"
        case .enums: "enums lists values"
        case .members: "members users agents"
        case .project: "project rename delete"
        }
    }

    func isAvailable(in mode: ProjectMode) -> Bool {
        self != .members || mode == .server
    }

    static func pages(in mode: ProjectMode) -> [SettingsPage] {
        allCases.filter { $0.isAvailable(in: mode) }
    }

    static let storageKey = "settingsPage"
}
