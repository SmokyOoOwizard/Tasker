import Foundation
import Testing
@testable import Tasker

struct LocalizationTests {
    private let ru = Localizer(language: .ru)
    private let en = Localizer(language: .en)

    @Test func everyKeyHasTranslationInBothLanguages() {
        for localizer in [ru, en] {
            for key in L10nKey.allCases {
                let value = localizer.string(key)
                #expect(!value.isEmpty, "\(key.rawValue) \(localizer.language.rawValue)")
                #expect(value != key.rawValue, "\(key.rawValue) \(localizer.language.rawValue)")
            }
        }
    }

    @Test func translationsKeepTheSamePlaceholders() {
        let pattern = /%(\d+\$)?(@|lld)/
        for key in L10nKey.allCases where key != .countTasks {
            let left = ru.string(key).matches(of: pattern).map { String($0.output.0) }.sorted()
            let right = en.string(key).matches(of: pattern).map { String($0.output.0) }.sorted()
            #expect(left == right, "\(key.rawValue)")
        }
    }

    @Test func russianPluralForms() {
        let expected: [Int: String] = [
            0: "0 задач", 1: "1 задача", 2: "2 задачи", 4: "4 задачи", 5: "5 задач",
            11: "11 задач", 12: "12 задач", 21: "21 задача", 22: "22 задачи", 25: "25 задач", 101: "101 задача"
        ]
        for (count, text) in expected {
            #expect(ru.taskCount(count) == text)
        }
    }

    @Test func englishPluralForms() {
        #expect(en.taskCount(1) == "1 task")
        #expect(en.taskCount(0) == "0 tasks")
        #expect(en.taskCount(2) == "2 tasks")
        #expect(en.taskCount(21) == "21 tasks")
    }

    @Test func argumentsAreSubstituted() {
        #expect(ru.string(.menuAbout, "Tasker") == "О программе Tasker")
        #expect(en.string(.placeholderVersion, "1.2.3") == "Version 1.2.3")
    }

    @Test func sectionTitlesDifferByLanguage() {
        #expect(ru.string(AppSection.tasks.titleKey) == "Задачи")
        #expect(en.string(AppSection.tasks.titleKey) == "Tasks")
        #expect(ru.string(AppSection.admin.titleKey) == "Админ-панель")
        #expect(en.string(AppSection.admin.titleKey) == "Admin panel")
    }

    @Test func numbersFollowLanguage() {
        let ruNumber = ru.number(1234.5)
        let enNumber = en.number(1234.5)
        #expect(ruNumber.hasSuffix(",5"))
        #expect(enNumber == "1,234.5")
    }

    @Test func datesFollowLanguage() {
        let date = Date(timeIntervalSince1970: 1_793_750_400)
        #expect(ru.date(date) != en.date(date))
        #expect(en.date(date).contains("Nov"))
        #expect(ru.date(date).contains("нояб"))
    }

    @Test func systemLanguageIsRussianOnlyForRussian() {
        #expect(Language(preferred: ["ru-RU", "en-US"]) == .ru)
        #expect(Language(preferred: ["ru"]) == .ru)
        #expect(Language(preferred: ["en-US", "ru-RU"]) == .en)
        #expect(Language(preferred: ["de-DE"]) == .en)
        #expect(Language(preferred: []) == .en)
    }

    @Test func explicitChoiceOverridesSystem() {
        #expect(AppLanguage.system.resolved(systemPreferred: ["ru-RU"]) == .ru)
        #expect(AppLanguage.system.resolved(systemPreferred: ["fr-FR"]) == .en)
        #expect(AppLanguage.ru.resolved(systemPreferred: ["en-US"]) == .ru)
        #expect(AppLanguage.en.resolved(systemPreferred: ["ru-RU"]) == .en)
    }

    @Test func storedChoiceFallsBackToSystem() {
        for option in AppLanguage.allCases {
            #expect(AppLanguage(storedValue: option.rawValue) == option)
        }
        #expect(AppLanguage(storedValue: "") == .system)
        #expect(AppLanguage(storedValue: "de") == .system)
    }

    @Test func systemOverrideIsWrittenAndCleared() {
        let suite = "tasker.tests.language"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        AppLanguage.en.syncSystemOverride(defaults: defaults)
        #expect(defaults.persistentDomain(forName: suite)?[AppLanguage.systemOverrideKey] as? [String] == ["en"])
        AppLanguage.system.syncSystemOverride(defaults: defaults)
        #expect(defaults.persistentDomain(forName: suite)?[AppLanguage.systemOverrideKey] == nil)
        defaults.removePersistentDomain(forName: suite)
    }
}
