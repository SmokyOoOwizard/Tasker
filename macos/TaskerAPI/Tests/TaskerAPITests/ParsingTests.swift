import Foundation
import Testing
@testable import TaskerAPI

@Suite struct DateParsingTests {
    @Test(arguments: [
        ("2026-11-03T21:32:14Z", 1_793_741_534.0),
        ("2026-11-03T21:32:14.5Z", 1_793_741_534.5),
        ("2026-11-03T21:32:14.5081040+00:00", 1_793_741_534.508104),
        ("2026-11-04T00:32:14+03:00", 1_793_741_534.0),
        ("2026-11-03T16:32:14.250-05:00", 1_793_741_534.25)
    ])
    func parsesServerDates(text: String, seconds: Double) throws {
        let date = try #require(TaskerDate.parse(text))
        #expect(abs(date.timeIntervalSince1970 - seconds) < 0.000_01)
    }

    @Test(arguments: ["", "2026-11-03", "2026-11-03T21:32:14", "2026-11-03T21:32:14.Z", "2026-13-03T21:32:14Zjunk"])
    func rejectsMalformedDates(text: String) {
        #expect(TaskerDate.parse(text) == nil)
    }

    @Test func formatsRoundTrip() throws {
        let date = Date(timeIntervalSince1970: 1_793_741_534.125)
        let parsed = try #require(TaskerDate.parse(TaskerDate.format(date)))
        #expect(abs(parsed.timeIntervalSince(date)) < 0.001)
    }
}

@Suite struct ServerSentEventTests {
    @Test func parsesFixtureStream() {
        var parser = ServerSentEventParser()
        let events = parser.consume(Fixture.data("workspace-changes.sse"))
        #expect(events.map(\.event) == ["next", "next", "complete"])
        #expect(events[0].data.contains("\"entity\":\"task\""))
        #expect(events[1].data.contains("\"entity\":\"unknown\""))
        #expect(events[2].data.isEmpty)
    }

    @Test func joinsMultilineDataAndIgnoresComments() {
        var parser = ServerSentEventParser()
        let events = parser.consume(Data(": ping\r\n\r\ndata: first\ndata:second\nid: 7\nretry: 10\n\n".utf8))
        #expect(events == [ServerSentEvent(event: "message", data: "first\nsecond")])
    }

    @Test func handlesEventsSplitAcrossChunks() {
        var parser = ServerSentEventParser()
        let text = Array("event: next\r\ndata: {\"a\":1}\r\n\r\n".utf8)
        var events: [ServerSentEvent] = []
        for byte in text { if let event = parser.consume(byte) { events.append(event) } }
        #expect(events == [ServerSentEvent(event: "next", data: "{\"a\":1}")])
    }
}

@Suite struct ErrorMappingTests {
    @Test func mapsNotFound() throws {
        let error = #expect(throws: TaskerAPIError.self) {
            try GraphQLClient.parse(status: 200, body: Fixture.data("project-not-found.json"))
        }
        #expect(error == .notFound(Refusal(code: "entity.notFound", message: "Project 6cb8f29b-d6d5-4987-a61d-745df39b1ded not found")))
    }

    @Test func mapsLockedConflictWithHolder() throws {
        let error = #expect(throws: TaskerAPIError.self) {
            try GraphQLClient.parse(status: 200, body: Fixture.data("graphql-locked.json"))
        }
        guard case .conflict(let conflict) = error else {
            Issue.record("expected conflict, got \(String(describing: error))")
            return
        }
        #expect(conflict.kind == .locked)
        #expect(conflict.message == "Task is being edited by alice")
        #expect(conflict.refusal.code == "entity.locked")
        #expect(conflict.heldBy?.name == "alice")
        #expect(conflict.heldBy?.expiresAt == TaskerDate.parse("2026-11-04T10:00:00Z"))
    }

    @Test func mapsUnauthenticated() {
        let error = #expect(throws: TaskerAPIError.self) {
            try GraphQLClient.parse(status: 401, body: Fixture.data("graphql-unauthenticated.json"))
        }
        #expect(error == .unauthenticated(Refusal(code: "auth.required", message: "Sign in to read data")))
    }

    @Test func keepsErrorsWithoutCode() {
        let error = #expect(throws: TaskerAPIError.self) {
            try GraphQLClient.parse(status: 400, body: Fixture.data("graphql-validation.json"))
        }
        guard case .graphQL(let errors) = error else {
            Issue.record("expected graphQL errors, got \(String(describing: error))")
            return
        }
        #expect(errors.count == 1)
        #expect(errors[0].message.contains("nope"))
        #expect(errors[0].code == nil)
    }

    @Test func mapsRestBodies() {
        #expect(TaskerAPIError.from(status: 400, body: Fixture.data("rest-bad-request.json")) == .badRequest(Refusal(code: "validation.required", message: "Name is required", params: ["field": .string("projectName")])))
        #expect(TaskerAPIError.from(status: 409, body: Fixture.data("rest-conflict-modified.json"))
            == .conflict(Conflict(kind: .modified, refusal: Refusal(code: "entity.modified", message: "Project was changed by someone else", params: ["name": .string("Site"), "entity": .string("project")]))))
        #expect(TaskerAPIError.from(status: 503, body: Data()) == .server(status: 503, refusal: Refusal(message: HTTPURLResponse.localizedString(forStatusCode: 503))))
        #expect(TaskerAPIError.from(status: 502, body: Data("Bad gateway".utf8)) == .server(status: 502, refusal: Refusal(message: "Bad gateway")))
    }

    @Test func mapsInternalErrorToServer() {
        let body = Data(#"{"error":"Unexpected","code":"internal.unexpected","params":{}}"#.utf8)
        #expect(TaskerAPIError.from(status: 500, body: body) == .server(status: 500, refusal: Refusal(code: "internal.unexpected", message: "Unexpected")))
    }

    @Test func nonJSONBodyKeepsText() {
        let error = TaskerAPIError.from(status: 400, body: Data("plain text".utf8))
        #expect(error == .badRequest(Refusal(message: "plain text")))
        #expect(error.refusal?.code == "")
    }

    @Test func classifiesConflictKinds() {
        #expect(Conflict.Kind(refusal: Refusal(code: "series.planChanged", message: "")) == .modified)
        #expect(Conflict.Kind(refusal: Refusal(code: "format.unsupported", message: "")) == .unsupportedFormat)
        #expect(Conflict.Kind(refusal: Refusal(code: "statusSet.statusInUse", message: "")) == .inUse)
        #expect(Conflict.Kind(refusal: Refusal(code: "enum.valuesInUse", message: "")) == .inUse)
        #expect(Conflict.Kind(refusal: Refusal(code: "x.y", message: "", usages: [RefusalUsage(kind: "links")])) == .inUse)
        #expect(Conflict.Kind(refusal: Refusal(code: "x.y", message: "")) == .other("x.y"))
    }

    @Test func parsesInUseUsagesThroughRest() {
        let error = TaskerAPIError.from(status: 409, body: Fixture.data("rest-in-use.json"))
        assertInUse(error)
    }

    @Test func parsesInUseUsagesThroughGraphQL() {
        let error = #expect(throws: TaskerAPIError.self) {
            try GraphQLClient.parse(status: 200, body: Fixture.data("graphql-in-use.json"))
        }
        assertInUse(error)
    }

    private func assertInUse(_ error: TaskerAPIError?) {
        guard case .conflict(let conflict) = error else {
            Issue.record("expected conflict, got \(String(describing: error))")
            return
        }
        #expect(conflict.kind == .inUse)
        #expect(conflict.refusal.code == "status.inUse")
        #expect(conflict.refusal.params["name"]?.stringValue == "Open")
        #expect(conflict.usages == [
            RefusalUsage(kind: "tasks", count: 3, tasks: ["TSK-1", "TSK-2"]),
            RefusalUsage(kind: "boardColumn", board: "Main", column: "Todo"),
            RefusalUsage(kind: "statusSet", name: "Default"),
        ])
    }

    @Test func readsColumnIndex() {
        let body = Data(#"{"error":"x","code":"board.noDropRule","params":{"column":"A","taskType":"B","columnIndex":2}}"#.utf8)
        #expect(TaskerAPIError.from(status: 400, body: body).refusal?.columnIndex == 2)
    }

    @Test func wrapsTransportErrors() {
        #expect(TaskerAPIError.wrap(URLError(.timedOut)) as? TaskerAPIError == .timedOut)
        #expect(TaskerAPIError.wrap(URLError(.cancelled)) is CancellationError)
        guard case .network(let code, _) = TaskerAPIError.wrap(URLError(.cannotConnectToHost)) as? TaskerAPIError else {
            Issue.record("expected network error")
            return
        }
        #expect(code == .cannotConnectToHost)
    }
}

@Suite struct GeneratedOperationTests {
    @Test func decodesProjectsFixture() throws {
        let json = try GraphQLClient.parse(status: 200, body: Fixture.data("projects.json"))
        let data = try TaskerJSON.decode(ProjectsQuery.Data.self, from: json)
        #expect(data.projects.totalCount == 2)
        #expect(data.projects.data.map(\.name) == ["Tasker", "Сайт"])
        #expect(data.projects.data[0].id == UUID(uuidString: "6cb8f29b-d6d5-4987-a61d-745df39b1ded"))
    }

    @Test func omitsUnsetVariables() {
        #expect(ProjectsQuery().variables.isEmpty)
        #expect(ProjectsQuery(limit: 10).variables == ["limit": .int(10)])
        let id = UUID()
        let statusId = UUID()
        #expect(ProjectTasksQuery(projectId: id, statusIds: [statusId]).variables == [
            "projectId": .string(id.uuidString.lowercased()),
            "statusIds": .array([.string(statusId.uuidString.lowercased())])
        ])
    }

    @Test func documentsAskForCacheKeys() {
        #expect(ProjectsQuery.document.contains("fragment ProjectFields on Project"))
        #expect(ProjectsQuery.document.contains("__typename"))
        let data = ProjectsQuery.selections[0].selections.first { $0.name == "data" }
        #expect(data?.selections.map(\.name) == ["__typename", "id", "name", "createdAt", "version"])
    }

    @Test func storageKeysAreCanonical() {
        let field = GraphQLField("tasks", arguments: ["limit": .variable("limit"), "offset": .variable("offset"), "descriptionLength": .value(.int(0))])
        #expect(field.storageKey(["offset": .int(0), "limit": .int(50)]) == #"tasks({"descriptionLength":0,"limit":50,"offset":0})"#)
        #expect(field.storageKey([:]) == #"tasks({"descriptionLength":0})"#)
        #expect(GraphQLField("me").storageKey([:]) == "me")
    }
}
