import Foundation
import TaskerAPI

extension TaskerAPIError {
    static func modified(_ name: String = "Item") -> TaskerAPIError {
        .conflict(Conflict(refusal: Refusal(code: "entity.modified", message: "changed", params: ["name": .string(name), "entity": .string("project")])))
    }

    static func locked(by holder: String, name: String = "Item") -> TaskerAPIError {
        .conflict(Conflict(refusal: Refusal(
            code: "entity.locked", message: "locked",
            params: ["name": .string(name), "holder": .string(holder), "entity": .string("project"), "expiresAt": .string("2026-11-04T10:00:00Z")])))
    }

    static func inUse(code: String, name: String, usages: [RefusalUsage]) -> TaskerAPIError {
        .conflict(Conflict(refusal: Refusal(code: code, message: "\(name) is in use", params: ["name": .string(name)], usages: usages)))
    }
}
