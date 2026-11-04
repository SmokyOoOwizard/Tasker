import Foundation
import SwiftUI

enum Language: String, CaseIterable, Sendable {
    case ru
    case en

    var locale: Locale {
        Locale(identifier: rawValue)
    }

    init(preferred: [String]) {
        let first = preferred.first?.lowercased() ?? ""
        self = first.hasPrefix("ru") ? .ru : .en
    }
}

enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case system
    case ru
    case en

    static let storageKey = "appLanguage"
    static let systemOverrideKey = "AppleLanguages"

    var id: String { rawValue }

    var labelKey: L10nKey {
        switch self {
        case .system: .settingsLanguageSystem
        case .ru: .settingsLanguageRu
        case .en: .settingsLanguageEn
        }
    }

    init(storedValue: String) {
        self = AppLanguage(rawValue: storedValue) ?? .system
    }

    func resolved(systemPreferred: [String] = AppLanguage.systemPreferredLanguages()) -> Language {
        switch self {
        case .system: Language(preferred: systemPreferred)
        case .ru: .ru
        case .en: .en
        }
    }

    static func systemPreferredLanguages() -> [String] {
        let global = CFPreferencesCopyValue(
            systemOverrideKey as CFString,
            kCFPreferencesAnyApplication,
            kCFPreferencesCurrentUser,
            kCFPreferencesAnyHost
        ) as? [String]
        return global ?? Locale.preferredLanguages
    }

    func syncSystemOverride(defaults: UserDefaults = .standard) {
        switch self {
        case .system: defaults.removeObject(forKey: AppLanguage.systemOverrideKey)
        case .ru: defaults.set([Language.ru.rawValue], forKey: AppLanguage.systemOverrideKey)
        case .en: defaults.set([Language.en.rawValue], forKey: AppLanguage.systemOverrideKey)
        }
    }
}

private final class BundleToken {}

struct Localizer: Sendable {
    let language: Language
    private let bundle: Bundle

    init(language: Language, bundle: Bundle? = nil) {
        self.language = language
        let base = bundle ?? Bundle(for: BundleToken.self)
        if let path = base.path(forResource: language.rawValue, ofType: "lproj"), let localized = Bundle(path: path) {
            self.bundle = localized
        } else {
            self.bundle = base
        }
    }

    init(choice: AppLanguage, systemPreferred: [String] = AppLanguage.systemPreferredLanguages()) {
        self.init(language: choice.resolved(systemPreferred: systemPreferred))
    }

    var locale: Locale { language.locale }

    func string(_ key: L10nKey, _ arguments: any CVarArg...) -> String {
        let format = bundle.localizedString(forKey: key.rawValue, value: key.rawValue, table: nil)
        guard !arguments.isEmpty else { return format }
        return String(format: format, locale: locale, arguments: arguments)
    }

    func taskCount(_ count: Int) -> String {
        string(.countTasks, count)
    }

    func date(_ value: Date) -> String {
        value.formatted(.dateTime.day().month(.abbreviated).year().locale(locale))
    }

    func number(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(0...2)).locale(locale))
    }
}

extension EnvironmentValues {
    @Entry var localizer = Localizer(choice: .system)
}

struct LocalizedRoot: ViewModifier {
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue

    func body(content: Content) -> some View {
        let choice = AppLanguage(storedValue: storedLanguage)
        let localizer = Localizer(choice: choice)
        content
            .environment(\.localizer, localizer)
            .environment(\.locale, localizer.locale)
            .onChange(of: storedLanguage, initial: true) {
                choice.syncSystemOverride()
            }
    }
}

extension View {
    func localized() -> some View {
        modifier(LocalizedRoot())
    }
}
