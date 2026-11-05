import Foundation
import Testing
import TaskerAPI
@testable import Tasker

@MainActor
struct RefusalTests {
    private let ru = Localizer(language: .ru)
    private let en = Localizer(language: .en)

    private func sample(_ code: String, _ entry: ErrorCatalogEntry) -> [String: JSONValue] {
        var params: [String: JSONValue] = [:]
        for (index, name) in entry.params.enumerated() {
            if index == entry.plural {
                params[name] = .int(3)
            } else if name == "field" {
                params[name] = .string("projectName")
            } else if ["values", "fields", "tasks", "conditions", "allowed"].contains(name) {
                params[name] = .array([.string("alpha"), .string("beta")])
            } else if name == "columnIndex" || name == "count" || name == "max" || name == "min" {
                params[name] = .int(2)
            } else {
                params[name] = .string("Sample")
            }
        }
        return params
    }

    @Test func everyCatalogEntryRendersInBothLanguages() {
        #expect(!ErrorCatalog.entries.isEmpty)
        for (code, entry) in ErrorCatalog.entries {
            let refusal = Refusal(code: code, message: "fallback", params: sample(code, entry))
            for l in [ru, en] {
                let text = l.refusal(refusal)
                #expect(!text.isEmpty, "\(code) \(l.language.rawValue)")
                #expect(!text.contains("%"), "\(code) \(l.language.rawValue): \(text)")
                #expect(!text.contains("{"), "\(code) \(l.language.rawValue): \(text)")
                if code != "validation.invalid" { #expect(text != "fallback", "\(code)") }
            }
        }
    }

    @Test func everyCatalogEntryRendersWithoutParams() {
        for code in ErrorCatalog.entries.keys {
            for l in [ru, en] {
                let text = l.refusal(Refusal(code: code, message: "fallback"))
                #expect(!text.isEmpty, "\(code)")
                #expect(!text.contains("%"), "\(code): \(text)")
            }
        }
    }

    @Test func unknownCodeAndValidationInvalidFallBackToEnglishMessage() {
        #expect(ru.refusal(Refusal(code: "nope.nothing", message: "Something broke")) == "Something broke")
        #expect(en.refusal(Refusal(code: "", message: "Plain text")) == "Plain text")
        #expect(ru.refusal(Refusal(code: "validation.invalid", message: "Name: must be short")) == "Name: must be short")
    }

    @Test func fieldParamIsLocalizedAndArraysAreJoined() {
        let required = Refusal(code: "validation.required", message: "x", params: ["field": .string("title")])
        #expect(en.refusal(required).contains(en.string(.errorLabelTitle)))
        #expect(ru.refusal(required).contains(ru.string(.errorLabelTitle)))
        let enumValues = Refusal(code: "enum.valuesInUse", message: "x", params: ["name": .string("Levels"), "values": .array([.string("low"), .string("high")])])
        #expect(en.refusal(enumValues).contains("low, high"))
    }

    @Test func pluralParamIsFormattedAsNumber() {
        let refusal = Refusal(
            code: "taskType.fieldsHaveValues", message: "x", params: ["name": .string("Bug"), "fields": .array([.string("Level")]), "count": .int(1)])
        #expect(en.refusal(refusal).contains("1"))
        #expect(!ru.refusal(refusal).contains("%"))
    }

    @Test func messageForNonApiErrorUsesDescription() {
        struct Plain: LocalizedError { var errorDescription: String? { "plain" } }
        #expect(en.message(for: Plain()) == "plain")
        #expect(en.message(for: TaskerAPIError.timedOut) == TaskerAPIError.timedOut.message)
        #expect(en.message(for: TaskerAPIError.locked(by: "Anna")).contains("Anna"))
    }

    @Test func refusalRowsFollowUsages() {
        let refusal = SettingsRefusal(refusal: Refusal(code: "status.inUse", message: "x", params: ["name": .string("Open")], usages: [
            RefusalUsage(kind: "tasks", count: 8, tasks: ["TSK-1", "TSK-2"]),
            RefusalUsage(kind: "links", count: 4),
            RefusalUsage(kind: "ownFields", count: 2),
            RefusalUsage(kind: "board", name: "Main"),
            RefusalUsage(kind: "boardColumn", board: "Main", column: "Todo"),
            RefusalUsage(kind: "taskType", name: "Bug"),
            RefusalUsage(kind: "statusSet", name: "Flow"),
            RefusalUsage(kind: "field", name: "Level"),
            RefusalUsage(kind: "unknown", name: "ignored"),
        ]))
        let rows = refusal.rows(en)
        #expect(rows.count == 8)
        #expect(rows[0] == SettingsRefusalRow(label: en.string(.errorUsageTasks, "8"), value: "TSK-1, TSK-2 …"))
        #expect(rows[1] == SettingsRefusalRow(label: en.string(.errorUsageLinks, "4"), value: nil))
        #expect(rows[2] == SettingsRefusalRow(label: en.string(.errorUsageOwnFields, "2"), value: nil))
        #expect(rows[3] == SettingsRefusalRow(label: en.string(.errorUsageBoard), value: "Main"))
        #expect(rows[4] == SettingsRefusalRow(label: en.string(.errorUsageBoardColumn, "Main"), value: "Todo"))
        #expect(rows[5] == SettingsRefusalRow(label: en.string(.errorUsageTaskType), value: "Bug"))
        #expect(rows[6] == SettingsRefusalRow(label: en.string(.errorUsageStatusSet), value: "Flow"))
        #expect(rows[7] == SettingsRefusalRow(label: en.string(.errorUsageField), value: "Level"))
        #expect(refusal.body(en) == en.refusal(refusal.refusal))
    }

    @Test func fullyListedTasksHaveNoEllipsis() {
        let refusal = SettingsRefusal(refusal: Refusal(
            code: "taskType.inUse", message: "x", usages: [RefusalUsage(kind: "tasks", count: 2, tasks: ["TSK-1", "TSK-2"])]))
        #expect(refusal.rows(ru).first?.value == "TSK-1, TSK-2")
    }

    @Test func inUseDeleteIsAnsweredByServerWithoutPreflight() async {
        let source = StubSettingsSource(catalog: SettingsCatalog(project: SettingsProjectItem(id: UUID(), name: "P", createdAt: Date(timeIntervalSince1970: 0), version: "p")))
        source.deleteFailure = .inUse(code: "taskType.inUse", name: "Bug", usages: [RefusalUsage(kind: "tasks", count: 5, tasks: ["TSK-1"])])
        let model = SettingsModel(project: UUID(), source: source)
        model.localizer = en
        let item = SettingsTypeItem(id: UUID(), name: "Bug", statusSetId: UUID(), version: "v")
        guard case .failure(let error) = await model.deleteTaskType(item) else {
            Issue.record("expected refusal")
            return
        }
        #expect(source.calls.filter { $0 == "tasks" }.isEmpty)
        let sheet = SettingsModel.inUseRefusal(error).map { SettingsRefusal(refusal: $0) }
        #expect(sheet?.rows(en).first?.value == "TSK-1 …")
        #expect(sheet?.body(en) == en.refusal(sheet!.refusal))
    }
}
