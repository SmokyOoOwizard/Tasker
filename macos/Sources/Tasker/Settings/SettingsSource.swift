import Foundation
import TaskerAPI

protocol SettingsSource: Sendable {
    func mode() async -> ProjectMode
    func catalog(project: UUID) async throws -> SettingsCatalog
    func tasks(project: UUID, filter: [String], statusIds: [UUID]?, typeIds: [UUID]?, cap: Int) async throws -> SettingsTaskPage
    func seriesStats(project: UUID, series: [UUID]) async throws -> [UUID: SettingsSeriesStat]
    func members(project: UUID) async throws -> [SettingsMember]
    func users() async throws -> [SettingsMember]
    func addMember(project: UUID, user: UUID) async throws
    func removeMember(project: UUID, user: UUID) async throws

    func createField(project: UUID, name: String, type: FieldType, multiple: Bool, enumId: UUID?) async throws
    func updateField(project: UUID, field: SettingsFieldItem, name: String, type: FieldType, multiple: Bool, enumId: UUID?, change: FieldChangeRequest?) async throws
    func deleteField(project: UUID, field: SettingsFieldItem) async throws

    func createEnum(project: UUID, name: String, values: [String]) async throws
    func updateEnum(project: UUID, item: SettingsEnumItem, name: String, values: [EnumValueDraft], removal: EnumRemovalChoice?) async throws
    func deleteEnum(project: UUID, item: SettingsEnumItem) async throws

    func createLinkType(project: UUID, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async throws
    func updateLinkType(project: UUID, item: SettingsLinkTypeItem, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async throws
    func deleteLinkType(project: UUID, item: SettingsLinkTypeItem) async throws

    func createStatus(project: UUID, name: String, color: String, description: String, icon: String) async throws
    func updateStatus(project: UUID, item: SettingsStatusItem, name: String, color: String, description: String, icon: String) async throws
    func deleteStatus(project: UUID, item: SettingsStatusItem) async throws

    func createStatusSet(project: UUID, name: String, statusIds: [UUID]) async throws
    func updateStatusSet(project: UUID, item: SettingsStatusSetItem, name: String, statusIds: [UUID]) async throws
    func deleteStatusSet(project: UUID, item: SettingsStatusSetItem) async throws

    func createTaskType(project: UUID, name: String, description: String, statusSetId: UUID, fields: [SettingsTypeField]) async throws
    func updateTaskType(
        project: UUID, item: SettingsTypeItem, name: String, description: String, statusSetId: UUID, fields: [SettingsTypeField], removal: RemovedFieldChoice?
    ) async throws -> Int
    func deleteTaskType(project: UUID, item: SettingsTypeItem) async throws

    func createSeries(project: UUID, name: String, prefix: String) async throws
    func updateSeries(project: UUID, item: SettingsSeriesItem, name: String, prefix: String) async throws
    func deleteSeries(project: UUID, item: SettingsSeriesItem) async throws

    func renameProject(project: SettingsProjectItem, name: String) async throws
    func deleteProject(project: SettingsProjectItem) async throws

    func lock(project: UUID, target: SettingsLockTarget) async throws
    func unlock(project: UUID, target: SettingsLockTarget) async throws
}

extension SettingsSource {
    func tasks(project: UUID, filter: [String], cap: Int) async throws -> SettingsTaskPage {
        try await tasks(project: project, filter: filter, statusIds: nil, typeIds: nil, cap: cap)
    }
}

private extension UUID {
    var wire: String { uuidString.lowercased() }
}

struct GraphQLSettingsSource: SettingsSource {
    static let page = 200

    let client: TaskerAPIClient

    func mode() async -> ProjectMode {
        guard let health = try? await client.rest.perform({ try await $0.getHealth().ok.body.json }) else { return .local }
        return ProjectMode(wire: health.mode)
    }

    func catalog(project: UUID) async throws -> SettingsCatalog {
        guard let data = try await client.graphQL.fetch(SettingsCatalogQuery(projectId: project)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        var catalog = SettingsCatalog(project: SettingsProjectItem(id: data.id, name: data.name, createdAt: data.createdAt, version: data.version))
        catalog.fields = data.fields.data.map {
            SettingsFieldItem(id: $0.id, name: $0.name, type: $0.type, multiple: $0.multiple, enumId: $0.enumId, version: $0.version)
        }
        catalog.enums = data.enums.data.map { item in
            SettingsEnumItem(id: item.id, name: item.name, values: item.values.map { SettingsEnumValue(id: $0.id, name: $0.name) }, version: item.version)
        }
        catalog.linkTypes = data.linkTypes.data.map {
            SettingsLinkTypeItem(
                id: $0.id, name: $0.name, outward: $0.outwardName, inward: $0.inwardName, allowCycles: $0.allowCycles,
                hierarchical: $0.hierarchical, version: $0.version)
        }
        catalog.series = data.seriesList.data.map { SettingsSeriesItem(id: $0.id, name: $0.name, prefix: $0.prefix, version: $0.version) }
        catalog.types = data.taskTypes.data.map {
            SettingsTypeItem(
                id: $0.id, name: $0.name, description: $0.description, statusSetId: $0.statusSetId,
                fields: $0.fields.map { SettingsTypeField(fieldId: $0.fieldId, required: $0.required) }, version: $0.version)
        }
        catalog.statuses = data.statuses.data.map {
            SettingsStatusItem(id: $0.id, name: $0.name, color: $0.color, description: $0.description, icon: $0.icon, version: $0.version)
        }
        catalog.statusSets = data.statusSets.data.map { SettingsStatusSetItem(id: $0.id, name: $0.name, statusIds: $0.statusIds, version: $0.version) }
        catalog.boards = data.boards.data.map { board in
            SettingsBoardItem(
                name: board.name, statusSetIds: board.statusSetIds,
                columns: board.columns.map { SettingsBoardColumnItem(name: $0.name, statusIds: $0.statusIds, dropStatusIds: $0.dropStatuses.map(\.statusId)) })
        }
        catalog.health = SettingsHealth(
            numberConflicts: data.seriesHealth.numberConflicts.map { SeriesNumberConflict(seriesId: $0.seriesId, number: $0.number, taskIds: $0.taskIds) },
            prefixConflicts: data.seriesHealth.prefixConflicts.map(\.prefix),
            invalidTasks: data.seriesHealth.tasksWithInvalidSeries,
            unreadableFiles: data.seriesHealth.unreadableSeriesFiles)
        return catalog
    }

    func tasks(project: UUID, filter: [String], statusIds: [UUID]?, typeIds: [UUID]?, cap: Int) async throws -> SettingsTaskPage {
        var items: [SettingsTaskBrief] = []
        var total = 0
        var offset = 0
        while offset < cap {
            guard let page = try await client.graphQL.fetch(
                SettingsTasksQuery(projectId: project, field: filter.isEmpty ? nil : filter, statusIds: statusIds, typeIds: typeIds, offset: offset, limit: Self.page)).project?.tasks
            else { throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString)) }
            total = page.totalCount
            for task in page.data {
                items.append(SettingsTaskBrief(
                    id: task.id, title: task.title, numbers: task.seriesNumbers.map { TaskSeriesRef(seriesId: $0.seriesId, number: $0.number) },
                    fields: Dictionary(task.fields.map { ($0.fieldId, $0.values) }, uniquingKeysWith: { first, _ in first })))
            }
            offset += page.data.count
            if page.data.isEmpty || offset >= total { break }
        }
        return SettingsTaskPage(total: total, items: items)
    }

    func seriesStats(project: UUID, series: [UUID]) async throws -> [UUID: SettingsSeriesStat] {
        var stats = Dictionary(uniqueKeysWithValues: series.map { ($0, SettingsSeriesStat(count: 0, maxNumber: 0)) })
        guard !series.isEmpty else { return stats }
        var offset = 0
        while offset < GraphQLSeriesSource.cap {
            guard let page = try await client.graphQL.fetch(
                SeriesNumbersPageQuery(projectId: project, seriesIds: series, offset: offset, limit: Self.page)).project?.tasks
            else { throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString)) }
            for task in page.data {
                for number in task.seriesNumbers {
                    guard var stat = stats[number.seriesId] else { continue }
                    stat.count += 1
                    stat.maxNumber = max(stat.maxNumber, number.number)
                    stats[number.seriesId] = stat
                }
            }
            offset += page.data.count
            if page.data.isEmpty || offset >= page.totalCount { break }
        }
        return stats
    }

    func members(project: UUID) async throws -> [SettingsMember] {
        guard let data = try await client.graphQL.fetch(SettingsMembersQuery(projectId: project)).project else {
            throw TaskerAPIError.notFound(Refusal(code: "entity.notFound", message: project.uuidString))
        }
        return data.members.data.map {
            SettingsMember(id: $0.id, name: $0.username, email: $0.email, isAgent: $0.kind == .agent, isAdmin: $0.isAdmin, ownerId: $0.ownerId)
        }
    }

    func users() async throws -> [SettingsMember] {
        try await client.graphQL.fetch(SettingsUsersQuery()).users.data.map {
            SettingsMember(id: $0.id, name: $0.username, email: $0.email, isAgent: $0.kind == .agent, isAdmin: $0.isAdmin, ownerId: $0.ownerId)
        }
    }

    func addMember(project: UUID, user: UUID) async throws {
        try await client.rest.perform {
            try await $0.addProjectMember(path: .init(projectId: project.wire), body: .json(.init(userId: user.wire))).created
        }
    }

    func removeMember(project: UUID, user: UUID) async throws {
        try await client.rest.perform {
            try await $0.removeProjectMember(path: .init(projectId: project.wire, userId: user.wire)).noContent
        }
    }

    func createField(project: UUID, name: String, type: FieldType, multiple: Bool, enumId: UUID?) async throws {
        _ = try await client.rest.perform {
            try await $0.createField(
                path: .init(projectId: project.wire),
                body: .json(.init(name: name, _type: Self.wire(type), multiple: multiple, enumId: type == .enum ? enumId?.wire : nil))
            ).created.body.json
        }
    }

    func updateField(
        project: UUID, field: SettingsFieldItem, name: String, type: FieldType, multiple: Bool, enumId: UUID?, change: FieldChangeRequest?
    ) async throws {
        var body = Components.Schemas.UpdateField(name: name, version: field.version)
        if type != field.type { body._type = .init(value1: Self.wire(type)) }
        if multiple != field.multiple { body.multiple = multiple }
        if type == .enum, enumId != field.enumId { body.enumId = enumId?.wire }
        if let change {
            let several: Components.Schemas.FieldChangeChoice.SeveralPayload? = change.keepFirst.map { .init(value1: $0 ? .keepFirst : .clear) }
            let mapping = change.mapping.sorted { $0.key < $1.key }.map { Components.Schemas.FieldValueMapping(from: $0.key, to: $0.value) }
            body.choice = .init(value1: .init(clearUnconvertible: change.clearUnconvertible, several: several, mapping: mapping.isEmpty ? nil : mapping))
        }
        _ = try await client.rest.perform {
            try await $0.updateField(path: .init(projectId: project.wire, id: field.id.wire), body: .json(body)).ok.body.json
        }
    }

    func deleteField(project: UUID, field: SettingsFieldItem) async throws {
        try await client.rest.perform {
            try await $0.deleteField(path: .init(projectId: project.wire, id: field.id.wire), query: .init(version: field.version)).noContent
        }
    }

    func createEnum(project: UUID, name: String, values: [String]) async throws {
        _ = try await client.rest.perform {
            try await $0.createFieldEnum(path: .init(projectId: project.wire), body: .json(.init(name: name, values: values))).created.body.json
        }
    }

    func updateEnum(project: UUID, item: SettingsEnumItem, name: String, values: [EnumValueDraft], removal: EnumRemovalChoice?) async throws {
        var body = Components.Schemas.UpdateFieldEnum(
            name: name, values: values.map { .init(id: $0.serverId?.wire, name: $0.name) }, version: item.version)
        switch removal {
        case .clear: body.removed = .init(value1: .init(clear: true))
        case .reassign(let target): body.removed = .init(value1: .init(reassignTo: target.wire))
        case nil: break
        }
        _ = try await client.rest.perform {
            try await $0.updateFieldEnum(path: .init(projectId: project.wire, id: item.id.wire), body: .json(body)).ok.body.json
        }
    }

    func deleteEnum(project: UUID, item: SettingsEnumItem) async throws {
        try await client.rest.perform {
            try await $0.deleteFieldEnum(path: .init(projectId: project.wire, id: item.id.wire), query: .init(version: item.version)).noContent
        }
    }

    func createLinkType(project: UUID, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool) async throws {
        _ = try await client.rest.perform {
            try await $0.createLinkType(
                path: .init(projectId: project.wire),
                body: .json(.init(name: name, outwardName: outward, inwardName: inward.isEmpty ? nil : inward, allowCycles: allowCycles, hierarchical: hierarchical))
            ).created.body.json
        }
    }

    func updateLinkType(
        project: UUID, item: SettingsLinkTypeItem, name: String, outward: String, inward: String, allowCycles: Bool, hierarchical: Bool
    ) async throws {
        _ = try await client.rest.perform {
            try await $0.updateLinkType(
                path: .init(projectId: project.wire, id: item.id.wire),
                body: .json(.init(name: name, outwardName: outward, inwardName: inward, version: item.version, allowCycles: allowCycles, hierarchical: hierarchical))
            ).ok.body.json
        }
    }

    func deleteLinkType(project: UUID, item: SettingsLinkTypeItem) async throws {
        try await client.rest.perform {
            try await $0.deleteLinkType(path: .init(projectId: project.wire, id: item.id.wire), query: .init(version: item.version)).noContent
        }
    }

    func createStatus(project: UUID, name: String, color: String, description: String, icon: String) async throws {
        _ = try await client.rest.perform {
            try await $0.createStatus(
                path: .init(projectId: project.wire), body: .json(.init(name: name, color: color, description: description, icon: icon))
            ).created.body.json
        }
    }

    func updateStatus(project: UUID, item: SettingsStatusItem, name: String, color: String, description: String, icon: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateStatus(
                path: .init(projectId: project.wire, id: item.id.wire),
                body: .json(.init(name: name, color: color, version: item.version, description: description, icon: icon))
            ).ok.body.json
        }
    }

    func deleteStatus(project: UUID, item: SettingsStatusItem) async throws {
        try await client.rest.perform {
            try await $0.deleteStatus(path: .init(projectId: project.wire, id: item.id.wire), query: .init(version: item.version)).noContent
        }
    }

    func createStatusSet(project: UUID, name: String, statusIds: [UUID]) async throws {
        _ = try await client.rest.perform {
            try await $0.createStatusSet(path: .init(projectId: project.wire), body: .json(.init(name: name, statusIds: statusIds.map(\.wire)))).created.body.json
        }
    }

    func updateStatusSet(project: UUID, item: SettingsStatusSetItem, name: String, statusIds: [UUID]) async throws {
        _ = try await client.rest.perform {
            try await $0.updateStatusSet(
                path: .init(projectId: project.wire, id: item.id.wire), body: .json(.init(name: name, statusIds: statusIds.map(\.wire), version: item.version))
            ).ok.body.json
        }
    }

    func deleteStatusSet(project: UUID, item: SettingsStatusSetItem) async throws {
        try await client.rest.perform {
            try await $0.deleteStatusSet(path: .init(projectId: project.wire, id: item.id.wire), query: .init(version: item.version)).noContent
        }
    }

    func createTaskType(project: UUID, name: String, description: String, statusSetId: UUID, fields: [SettingsTypeField]) async throws {
        _ = try await client.rest.perform {
            try await $0.createTaskType(
                path: .init(projectId: project.wire),
                body: .json(.init(name: name, statusSetId: statusSetId.wire, fields: fields.map { .init(fieldId: $0.fieldId.wire, required: $0.required) }, description: description))
            ).created.body.json
        }
    }

    func updateTaskType(
        project: UUID, item: SettingsTypeItem, name: String, description: String, statusSetId: UUID, fields: [SettingsTypeField], removal: RemovedFieldChoice?
    ) async throws -> Int {
        var body = Components.Schemas.UpdateTaskType(
            name: name, statusSetId: statusSetId.wire, version: item.version, fields: fields.map { .init(fieldId: $0.fieldId.wire, required: $0.required) },
            description: description)
        switch removal {
        case .clear: body.removedFields = .init(value1: .clear)
        case .keep: body.removedFields = .init(value1: .keep)
        case nil: break
        }
        let result = try await client.rest.perform {
            try await $0.updateTaskType(path: .init(projectId: project.wire, id: item.id.wire), body: .json(body)).ok.body.json
        }
        return Int(result.value2.affectedTasks)
    }

    func deleteTaskType(project: UUID, item: SettingsTypeItem) async throws {
        try await client.rest.perform {
            try await $0.deleteTaskType(path: .init(projectId: project.wire, id: item.id.wire), query: .init(version: item.version)).noContent
        }
    }

    func createSeries(project: UUID, name: String, prefix: String) async throws {
        _ = try await client.rest.perform {
            try await $0.createSeries(path: .init(projectId: project.wire), body: .json(.init(name: name, prefix: prefix))).created.body.json
        }
    }

    func updateSeries(project: UUID, item: SettingsSeriesItem, name: String, prefix: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateSeries(
                path: .init(projectId: project.wire, id: item.id.wire), body: .json(.init(name: name, prefix: prefix, version: item.version))
            ).ok.body.json
        }
    }

    func deleteSeries(project: UUID, item: SettingsSeriesItem) async throws {
        try await client.rest.perform {
            try await $0.deleteSeries(path: .init(projectId: project.wire, id: item.id.wire), query: .init(version: item.version)).noContent
        }
    }

    func renameProject(project: SettingsProjectItem, name: String) async throws {
        _ = try await client.rest.perform {
            try await $0.updateProject(path: .init(projectId: project.id.wire), body: .json(.init(name: name, version: project.version))).ok.body.json
        }
    }

    func deleteProject(project: SettingsProjectItem) async throws {
        try await client.rest.perform {
            try await $0.deleteProject(path: .init(projectId: project.id.wire), query: .init(version: project.version)).noContent
        }
    }

    func lock(project: UUID, target: SettingsLockTarget) async throws {
        let projectId = project.wire
        try await client.rest.perform { client in
            switch target {
            case .project: _ = try await client.acquireProjectLock(path: .init(projectId: projectId)).ok.body.json
            case .field(let id): _ = try await client.acquireFieldLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            case .enumeration(let id): _ = try await client.acquireEnumLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            case .linkType(let id): _ = try await client.acquireLinkTypeLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            case .series(let id): _ = try await client.acquireSeriesLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            case .status(let id): _ = try await client.acquireStatusLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            case .statusSet(let id): _ = try await client.acquireStatusSetLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            case .taskType(let id): _ = try await client.acquireTaskTypeLock(path: .init(projectId: projectId, id: id.wire)).ok.body.json
            }
        }
    }

    func unlock(project: UUID, target: SettingsLockTarget) async throws {
        let projectId = project.wire
        try await client.rest.perform { client in
            switch target {
            case .project: _ = try await client.releaseProjectLock(path: .init(projectId: projectId)).noContent
            case .field(let id): _ = try await client.releaseFieldLock(path: .init(projectId: projectId, id: id.wire)).noContent
            case .enumeration(let id): _ = try await client.releaseEnumLock(path: .init(projectId: projectId, id: id.wire)).noContent
            case .linkType(let id): _ = try await client.releaseLinkTypeLock(path: .init(projectId: projectId, id: id.wire)).noContent
            case .series(let id): _ = try await client.releaseSeriesLock(path: .init(projectId: projectId, id: id.wire)).noContent
            case .status(let id): _ = try await client.releaseStatusLock(path: .init(projectId: projectId, id: id.wire)).noContent
            case .statusSet(let id): _ = try await client.releaseStatusSetLock(path: .init(projectId: projectId, id: id.wire)).noContent
            case .taskType(let id): _ = try await client.releaseTaskTypeLock(path: .init(projectId: projectId, id: id.wire)).noContent
            }
        }
    }

    private static func wire(_ type: FieldType) -> Components.Schemas.FieldType {
        switch type {
        case .string: .string
        case .int: .int
        case .float: .float
        case .bool: .bool
        case .date: .date
        case .enum: ._enum
        }
    }
}
