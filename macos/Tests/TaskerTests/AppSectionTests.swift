import Testing
@testable import Tasker

struct AppSectionTests {
    @Test func storedValueRestoresSection() {
        for section in AppSection.allCases {
            #expect(AppSection(storedValue: section.rawValue) == section)
        }
    }

    @Test func unknownStoredValueFallsBackToDefault() {
        #expect(AppSection(storedValue: "") == .default)
        #expect(AppSection(storedValue: "nope") == .default)
    }

    @Test func shortcutsAreDistinctDigitsFromOne() {
        let keys = AppSection.allCases.map { String($0.shortcutKey) }
        #expect(keys == ["1", "2", "3", "4"])
    }
}
