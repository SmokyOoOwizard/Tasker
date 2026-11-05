import Foundation
import Testing
import TaskerAPI
@testable import Tasker

final class StubSettingsSource: SettingsSource, @unchecked Sendable {
    private let lock = NSLock()
    private var _calls: [String] = []
    var modeValue = ProjectMode.local
    var catalogValue: SettingsCatalog
    var tasksByFilter: [String: SettingsTaskPage] = [:]
    var statsValue: [UUID: SettingsSeriesStat] = [:]
    var membersValue: [SettingsMember] = []
    var lockFailure: TaskerAPIError?
    var deleteFailure: TaskerAPIError?
    var updateFailure: TaskerAPIError?
    private(set) var lastChange: FieldChangeRequest?
    private(set) var lastRemoval: EnumRemovalChoice?

    init(catalog: SettingsCatalog) { catalogValue = catalog }

    var calls: [String] { lock.withLock { _calls } }
    private func record(_ text: String) { lock.withLock { _calls.append(text) } }

    func mode() async -> ProjectMode { modeValue }
    func catalog(project: UUID) async throws -> SettingsCatalog { catalogValue }

    func tasks(project: UUID, filter: [String], cap: Int) async throws -> SettingsTaskPage {
        tasksByFilter[filter.joined(separator: "|")] ?? SettingsTaskPage(total: 0, items: [])
    }

    func seriesStats(project: UUID, series: [UUID]) async throws -> [UUID: SettingsSeriesStat] { statsValue }
    func members(project: UUID) async throws -> [SettingsMember] { membersValue }
    func users() async throws -> [SettingsMember] { membersValue }
    func addMember(project: UUID, user: UUID) async throws { record("addMember") }
    func removeMember(project: UUID, user: UUID) async throws { record("removeMember") }

    func createField(project: UUID, name: String, type: FieldType, multiple: Bool, enumId: UUID?) async throws { record("createField:\(name)") }

    func updateField(project: UUID, field: SettingsFieldItem, name: String, type: FieldType, multiple: Bool, enumId: UUID?, change: FieldChangeRequest?) async throws {
        if let updateFailure { throw updateFailure }
        lock.withLock { lastChange = change }
        record("updateField:\(name):\(type.rawValue)")
    }

    func deleteField(project: UUID, field: SettingsFieldItem) async throws {
        if let deleteFailure { throw deleteFailure }
        record("deleteField")
    }

    func createEnum(project: UUID, name: String, values: [String]) async throws { record("createEnum:\(values.joined(separator: ","))") }

    func updateEnum(project: UUID, item: SettingsEnumItem, name: String, values: [EnumValueDraft], removal: EnumRemovalChoice?) async throws {
        lock.withLock { lastRemoval = removal }
        record("updateEnum:\(values.map(\.name).joined(separator: ","))")
    }

    func deleteEnum(project: UUID, item: SettingsEnumItem) async throws { record("deleteEnum") }
    func createLinkType(project: UUID, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async throws { record("createLinkType") }
    func updateLinkType(project: UUID, item: SettingsLinkTypeItem, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async throws { record("updateLinkType") }

    func deleteLinkType(project: UUID, item: SettingsLinkTypeItem) async throws {
        if let deleteFailure { throw deleteFailure }
        record("deleteLinkType")
    }

    func createSeries(project: UUID, name: String, prefix: String) async throws { record("createSeries:\(prefix)") }
    func updateSeries(project: UUID, item: SettingsSeriesItem, name: String, prefix: String) async throws { record("updateSeries:\(prefix)") }
    func deleteSeries(project: UUID, item: SettingsSeriesItem) async throws { record("deleteSeries") }
    func renameProject(project: SettingsProjectItem, name: String) async throws { record("rename:\(name)") }
    func deleteProject(project: SettingsProjectItem) async throws { record("deleteProject") }

    func lock(project: UUID, target: SettingsLockTarget) async throws {
        if let lockFailure { throw lockFailure }
        record("lock:\(target)")
    }

    func unlock(project: UUID, target: SettingsLockTarget) async throws { record("unlock:\(target)") }
}

@MainActor
struct SettingsTests {
    let project = UUID()
    let fieldId = UUID()
    let enumId = UUID()
    let valueA = UUID()
    let valueB = UUID()
    let seriesId = UUID()
    let ru = Localizer(choice: .ru)

    private func catalog() -> SettingsCatalog {
        var catalog = SettingsCatalog(project: SettingsProjectItem(id: project, name: "Проект", createdAt: Date(timeIntervalSince1970: 0), version: "p1"))
        catalog.fields = [SettingsFieldItem(id: fieldId, name: "Level", type: .enum, multiple: true, enumId: enumId, version: "f1")]
        catalog.enums = [SettingsEnumItem(
            id: enumId, name: "Levels", values: [SettingsEnumValue(id: valueA, name: "low"), SettingsEnumValue(id: valueB, name: "high")], version: "e1")]
        catalog.series = [SettingsSeriesItem(id: seriesId, name: "Основная", prefix: "TSK", version: "s1")]
        catalog.conditions = [SettingsBoardCondition(board: "Main", column: "Hot", fieldId: fieldId, value: valueB.uuidString.lowercased())]
        return catalog
    }

    private func model(_ source: StubSettingsSource) -> SettingsModel {
        let value = SettingsModel(project: project, source: source, heartbeat: .milliseconds(20))
        value.localizer = ru
        return value
    }

    @Test func fieldNameRules() {
        #expect(SettingsRules.validFieldName("Estimate2"))
        #expect(SettingsRules.validFieldName("Оценка"))
        #expect(!SettingsRules.validFieldName("two words"))
        #expect(!SettingsRules.validFieldName("a-b"))
        #expect(!SettingsRules.validFieldName(""))
    }

    @Test func valueListParsing() {
        #expect(SettingsRules.valueNames(from: " one \n\ntwo\r\nthree ") == ["one", "two", "three"])
        #expect(SettingsRules.duplicate(in: ["A", "b", "a"]) == "a")
        #expect(SettingsRules.duplicate(in: ["A", "b"]) == nil)
    }

    @Test func reorderingKeepsOtherValues() {
        #expect(SettingsRules.moved([1, 2, 3, 4], from: 0, to: 2) == [2, 3, 1, 4])
        #expect(SettingsRules.moved([1, 2, 3], from: 2, to: 5) == [1, 2, 3])
        #expect(SettingsRules.moved([1, 2, 3], from: 1, to: 0) == [2, 1, 3])
    }

    @Test func typeChangeNeedsChoiceOnlyWhenValuesChange() {
        let field = SettingsFieldItem(id: fieldId, name: "N", type: .int, multiple: false, enumId: nil, version: "v")
        #expect(SettingsRules.changesValues(old: field, type: .float, multiple: false, enumId: nil))
        #expect(!SettingsRules.changesValues(old: field, type: .int, multiple: true, enumId: nil))
        let multiple = SettingsFieldItem(id: fieldId, name: "N", type: .string, multiple: true, enumId: nil, version: "v")
        #expect(SettingsRules.changesValues(old: multiple, type: .string, multiple: false, enumId: nil))
        #expect(!SettingsRules.changesValues(old: multiple, type: .string, multiple: true, enumId: nil))
    }

    @Test func membersPageOnlyInServerMode() {
        #expect(!SettingsPage.pages(in: .local).contains(.members))
        #expect(SettingsPage.pages(in: .server).contains(.members))
        #expect(SettingsPage.pages(in: .local).contains(.project))
    }

    @Test func lastHumanCannotBeRemoved() {
        let human = SettingsMember(id: UUID(), name: "a", email: nil, isAgent: false, isAdmin: false, ownerId: nil)
        let agent = SettingsMember(id: UUID(), name: "bot", email: nil, isAgent: true, isAdmin: false, ownerId: human.id)
        #expect(!SettingsRules.canRemoveMember(human, among: [human, agent]))
        #expect(SettingsRules.canRemoveMember(agent, among: [human, agent]))
        let second = SettingsMember(id: UUID(), name: "b", email: nil, isAgent: false, isAdmin: false, ownerId: nil)
        #expect(SettingsRules.canRemoveMember(human, among: [human, second]))
    }

    @Test func loadReadsCatalogModeAndSeriesStats() async {
        let source = StubSettingsSource(catalog: catalog())
        source.modeValue = .server
        source.statsValue = [seriesId: SettingsSeriesStat(count: 4, maxNumber: 9)]
        let value = model(source)
        await value.reload()
        #expect(value.phase == .ready)
        #expect(value.mode == .server)
        #expect(value.stats[seriesId]?.next == 10)
        #expect(value.pages.contains(.members))
        #expect(value.prefixes[seriesId] == "TSK")
    }

    @Test func lockHeldAndReleasedAroundEditing() async {
        let source = StubSettingsSource(catalog: catalog())
        let value = model(source)
        #expect(await value.hold(.field(fieldId)) == nil)
        #expect(value.held == .field(fieldId))
        await value.drop()
        #expect(value.held == nil)
        #expect(source.calls == ["lock:field(\(fieldId))", "unlock:field(\(fieldId))"])
    }

    @Test func foreignLockIsReportedAndNotHeld() async {
        let source = StubSettingsSource(catalog: catalog())
        source.lockFailure = .conflict(Conflict(kind: .locked, message: "busy", heldBy: LockHolder(name: "Анна", expiresAt: nil)))
        let value = model(source)
        let message = await value.hold(.project)
        #expect(message?.contains("Анна") == true)
        #expect(value.held == nil)
    }

    @Test func heartbeatRenewsTheLock() async throws {
        let source = StubSettingsSource(catalog: catalog())
        let value = model(source)
        _ = await value.hold(.series(seriesId))
        try await Task.sleep(for: .milliseconds(120))
        await value.drop()
        #expect(source.calls.filter { $0.hasPrefix("lock:") }.count >= 2)
    }

    @Test func releaseUnlocksWhatIsHeld() async {
        let source = StubSettingsSource(catalog: catalog())
        let value = model(source)
        _ = await value.hold(.enumeration(enumId))
        await value.releaseNow()
        #expect(source.calls.last == "unlock:enumeration(\(enumId))")
        #expect(await value.hold(.project) == nil)
        #expect(value.held == nil)
    }

    @Test func fieldTypeChangeSendsTheChoiceAndReportsAffectedTasks() async {
        let source = StubSettingsSource(catalog: catalog())
        let value = model(source)
        var notes: [String] = []
        value.onNotice = { notes.append($0) }
        await value.reload()
        let field = catalog().fields[0]
        var request = FieldChangeRequest()
        request.keepFirst = true
        request.mapping = ["x": "y"]
        let failure = await value.updateField(field, name: "Level", type: .string, multiple: false, enumId: nil, change: request, affected: 3)
        #expect(failure == nil)
        #expect(source.lastChange == request)
        #expect(notes == ["Тип поля изменён, затронуто задач: 3"])
    }

    @Test func staleFieldVersionIsExplained() async {
        let source = StubSettingsSource(catalog: catalog())
        source.updateFailure = .conflict(Conflict(kind: .modified, message: "changed"))
        let value = model(source)
        let failure = await value.updateField(catalog().fields[0], name: "Level", type: .string, multiple: false, enumId: nil, change: nil, affected: 0)
        #expect(failure == ru.string(.settingsModified))
    }

    @Test func enumUsageCountsValuesAndBoardConditions() async {
        let source = StubSettingsSource(catalog: catalog())
        let task = { (values: [UUID]) in
            SettingsTaskBrief(id: UUID(), title: "t", numbers: [], fields: [self.fieldId: values.map { $0.uuidString.lowercased() }])
        }
        source.tasksByFilter["Level:set"] = SettingsTaskPage(total: 3, items: [task([valueA]), task([valueA, valueB]), task([valueB])])
        let value = model(source)
        await value.reload()
        let usage = await value.enumUsage(catalog().enums[0])
        #expect(usage.count(valueA) == 2)
        #expect(usage.count(valueB) == 2)
        #expect(usage.columns[valueB] == 1)
        #expect(usage.complete)
    }

    @Test func incompleteScanIsNotTrusted() async {
        let source = StubSettingsSource(catalog: catalog())
        source.tasksByFilter["Level:set"] = SettingsTaskPage(total: 9000, items: [])
        let value = model(source)
        await value.reload()
        #expect(await value.enumUsage(catalog().enums[0]).complete == false)
    }

    @Test func fieldUsageListsTypesTasksAndBoards() async {
        var data = catalog()
        data.types = [SettingsTypeItem(id: UUID(), name: "Баг", fieldIds: [fieldId])]
        let source = StubSettingsSource(catalog: data)
        source.tasksByFilter["Level:set"] = SettingsTaskPage(
            total: 1, items: [SettingsTaskBrief(id: UUID(), title: "Сломано", numbers: [TaskSeriesRef(seriesId: seriesId, number: 7)], fields: [fieldId: ["x"]])])
        let value = model(source)
        await value.reload()
        let uses = await value.usage(of: data.fields[0])
        #expect(uses.count == 3)
        #expect(uses.contains { $0.kind == .tasks(1) && $0.name == "TSK-7" })
        #expect(uses.contains { $0.kind == .type && $0.name == "Баг" })
        #expect(uses.contains { $0.kind == .board("Main") && $0.name == "Hot" })
    }

    @Test func enumUpdateCarriesRemovalChoice() async {
        let source = StubSettingsSource(catalog: catalog())
        let value = model(source)
        await value.reload()
        let item = catalog().enums[0]
        let drafts = [EnumValueDraft(serverId: valueA, name: "low"), EnumValueDraft(name: "new")]
        #expect(await value.updateEnum(item, name: "Levels", values: drafts, removal: .reassign(valueA)) == nil)
        #expect(source.lastRemoval == .reassign(valueA))
        #expect(source.calls.contains("updateEnum:low,new"))
    }

    @Test func linkTypeInUseIsRecognised() async {
        let source = StubSettingsSource(catalog: catalog())
        source.deleteFailure = .conflict(Conflict(kind: .inUse, message: "Link type 'x' is used by links of 2 task(s)"))
        let value = model(source)
        let item = SettingsLinkTypeItem(id: UUID(), name: "x", outward: "a", inward: "b", allowCycles: false, hierarchical: false, version: "v")
        let result = await value.deleteLinkType(item)
        guard case .failure(let error) = result else {
            Issue.record("deletion should be refused")
            return
        }
        #expect(SettingsModel.isInUse(error)?.contains("2 task") == true)
        #expect(!source.calls.contains("deleteLinkType"))
    }

    @Test func projectDeletionNotifiesAndKeepsNothingLocked() async {
        let source = StubSettingsSource(catalog: catalog())
        let value = model(source)
        await value.reload()
        var deleted = false
        value.onProjectDeleted = { deleted = true }
        #expect(await value.deleteProject() == nil)
        #expect(deleted)
        #expect(source.calls.contains("deleteProject"))
    }

    @Test func lastHumanRemovalIsRefusedWithoutCallingTheServer() async {
        let source = StubSettingsSource(catalog: catalog())
        let human = SettingsMember(id: UUID(), name: "a", email: nil, isAgent: false, isAdmin: false, ownerId: nil)
        source.modeValue = .server
        source.membersValue = [human]
        let value = model(source)
        await value.reload()
        #expect(await value.removeMember(human) == ru.string(.settingsMemberLast))
        #expect(!source.calls.contains("removeMember"))
    }

    @Test func paletteOffersEverySettingsPage() {
        let l = Localizer(choice: .en)
        var context = PaletteContext(
            hasTab: true, hasProject: true, projects: [], currentProject: nil, workspaces: [], theme: .system, isDark: false, language: .en,
            settingsPages: SettingsPage.pages(in: .local))
        let actions = PaletteActions(
            goTo: { _ in }, createTask: {}, createBoard: {}, editBoard: { _ in }, showSeries: { _ in }, openSettings: { _ in }, chooseProject: { _ in },
            openWorkspace: { _ in }, openFolder: {}, setTheme: { _ in }, setLanguage: { _ in })
        let entries = PaletteCommands.build(context, vocabulary: TaskListVocabulary(), actions: actions, l: l)
        let pages = entries.filter { $0.id.hasPrefix("settings:") }
        #expect(pages.count == SettingsPage.pages(in: .local).count)
        #expect(pages.contains { $0.title == "Settings: Fields" })
        context.hasProject = false
        #expect(PaletteCommands.build(context, vocabulary: TaskListVocabulary(), actions: actions, l: l).allSatisfy { !$0.id.hasPrefix("settings:") })
    }
}

@MainActor
struct SettingsStringsTests {
    @Test func everySettingsStringFormatsWithItsArguments() {
        for choice in [AppLanguage.ru, .en] {
            let l = Localizer(choice: choice)
            #expect(!l.string(.settingsFieldChanged, "3").contains("%"))
            #expect(!l.string(.settingsUseTasks, "3").contains("%"))
            #expect(!l.string(.settingsEnumAdded, "3").contains("%"))
            #expect(!l.string(.settingsSeriesMeta, 2, "5").contains("%"))
            #expect(!l.string(.settingsSeriesConflicts, "2").contains("%"))
            #expect(!l.string(.settingsRemovalBody, "«a»", 2, "1").contains("%"))
            #expect(!l.string(.settingsChangeBody, 2, "TSK-1", "int", "string").contains("%"))
            #expect(!l.string(.settingsSeriesDeleteBody, "TSK", 4).contains("%"))
        }
    }
}

struct LinkTypeRefusalTests {
    @Test func countIsReadFromServerMessage() {
        let uses = LinkTypesSettingsPage.linkUsage("Link type 'x' is used by links of 12 task(s) and cannot be deleted")
        #expect(uses.map(\.kind) == [.links(12)])
        #expect(LinkTypesSettingsPage.linkUsage("something else").isEmpty)
    }
}
