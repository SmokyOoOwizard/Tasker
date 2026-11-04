import Testing
@testable import Tasker

struct AppInfoTests {
    @Test func versionIsSemantic() {
        let parts = AppInfo.version.split(separator: ".")
        #expect(parts.count >= 3)
        #expect(parts.allSatisfy { Int($0) != nil })
    }
}
