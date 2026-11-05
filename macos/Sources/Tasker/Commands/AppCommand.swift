import SwiftUI

struct CommandShortcut: Equatable, Sendable {
    let key: KeyEquivalent
    let symbol: String
    let modifiers: EventModifiers

    init(_ character: Character, _ modifiers: EventModifiers = .command) {
        key = KeyEquivalent(character)
        symbol = String(character).uppercased()
        self.modifiers = modifiers
    }

    init(key: KeyEquivalent, symbol: String, _ modifiers: EventModifiers) {
        self.key = key
        self.symbol = symbol
        self.modifiers = modifiers
    }

    var tokens: [String] {
        var result: [String] = []
        if modifiers.contains(.control) { result.append("⌃") }
        if modifiers.contains(.option) { result.append("⌥") }
        if modifiers.contains(.shift) { result.append("⇧") }
        if modifiers.contains(.command) { result.append("⌘") }
        result.append(symbol)
        return result
    }

    var display: String { tokens.joined() }
}

struct CommandState: Equatable, Sendable {
    var hasWindow = false
    var hasTab = false
    var hasProject = false
    var hasTask = false
    var hasLink = false
    var hasBoard = false
    var tabCount = 0
    var otherWindows = 0
    var listMode: TaskListMode?

    static let none = CommandState()
}

enum AppCommand: Hashable, Sendable {
    case newTask
    case newBoard
    case editBoard
    case newTab
    case newWindow
    case openFolder
    case closeTab
    case closeWindow
    case palette
    case toggleSidebar
    case go(AppSection)
    case listMode(TaskListMode)
    case theme(AppTheme)
    case taskStatus
    case taskLink
    case taskParent
    case taskCopyKey
    case copyLink
    case nextTab
    case previousTab
    case moveTabToNewWindow
    case mergeWindows
    case shortcuts
    case help
    case installCommand
    case removeIntegration

    static let sectionCommands: [AppCommand] = AppSection.allCases.map(AppCommand.go)
    static let listModes: [AppCommand] = TaskListMode.allCases.map(AppCommand.listMode)
    static let themes: [AppCommand] = AppTheme.allCases.map(AppCommand.theme)

    static let shortcutBearing: [AppCommand] = [
        .newTask, .newBoard, .editBoard, .newTab, .newWindow, .openFolder, .closeTab, .closeWindow, .palette, .toggleSidebar,
        .taskStatus, .taskLink, .taskParent, .taskCopyKey, .copyLink, .nextTab, .previousTab, .shortcuts, .help
    ] + sectionCommands + listModes

    var id: String {
        switch self {
        case .newTask: "newTask"
        case .newBoard: "newBoard"
        case .editBoard: "editBoard"
        case .newTab: "newTab"
        case .newWindow: "newWindow"
        case .openFolder: "openFolder"
        case .closeTab: "closeTab"
        case .closeWindow: "closeWindow"
        case .palette: "palette"
        case .toggleSidebar: "toggleSidebar"
        case .go(let section): "go." + section.rawValue
        case .listMode(let mode): "listMode." + mode.rawValue
        case .theme(let theme): "theme." + theme.rawValue
        case .taskStatus: "taskStatus"
        case .taskLink: "taskLink"
        case .taskParent: "taskParent"
        case .taskCopyKey: "taskCopyKey"
        case .copyLink: "copyLink"
        case .nextTab: "nextTab"
        case .previousTab: "previousTab"
        case .moveTabToNewWindow: "moveTabToNewWindow"
        case .mergeWindows: "mergeWindows"
        case .shortcuts: "shortcuts"
        case .help: "help"
        case .installCommand: "installCommand"
        case .removeIntegration: "removeIntegration"
        }
    }

    var titleKey: L10nKey {
        switch self {
        case .newTask: .taskCreateAction
        case .newBoard: .paletteNewBoard
        case .editBoard: .boardTitleEdit
        case .newTab: .tabNew
        case .newWindow: .windowNew
        case .openFolder: .workspaceOpen
        case .closeTab: .workspaceCloseTab
        case .closeWindow: .windowClose
        case .palette: .paletteTitle
        case .toggleSidebar: .sidebarHide
        case .go(let section): section.titleKey
        case .listMode(let mode): mode.titleKey
        case .theme(let theme): theme.titleKey
        case .taskStatus: .taskChangeStatus
        case .taskLink: .taskLink
        case .taskParent: .taskSetParent
        case .taskCopyKey: .taskCopyKey
        case .copyLink: .deeplinkCopy
        case .nextTab: .tabNext
        case .previousTab: .tabPrevious
        case .moveTabToNewWindow: .tabMoveToNewWindow
        case .mergeWindows: .windowMergeAll
        case .shortcuts: .menuShortcuts
        case .help: .menuHelp
        case .installCommand: .installMenuCommand
        case .removeIntegration: .installMenuRemove
        }
    }

    var shortcut: CommandShortcut? {
        switch self {
        case .newTask: CommandShortcut("n")
        case .newBoard: CommandShortcut("n", [.command, .option])
        case .editBoard: CommandShortcut("e", [.command, .option])
        case .newTab: CommandShortcut("t")
        case .newWindow: CommandShortcut("n", [.command, .shift])
        case .openFolder: CommandShortcut("o")
        case .closeTab: CommandShortcut("w")
        case .closeWindow: CommandShortcut("w", [.command, .shift])
        case .palette: CommandShortcut("k")
        case .toggleSidebar: CommandShortcut("0")
        case .go(let section): CommandShortcut(section.shortcutKey)
        case .listMode(let mode):
            switch mode {
            case .groups: CommandShortcut("1", [.command, .option])
            case .flat: CommandShortcut("2", [.command, .option])
            case .tree: CommandShortcut("3", [.command, .option])
            }
        case .taskStatus: CommandShortcut("s", [.command, .option])
        case .taskLink: CommandShortcut("l")
        case .taskParent: CommandShortcut("p", [.command, .option])
        case .taskCopyKey: CommandShortcut("c", [.command, .option])
        case .copyLink: CommandShortcut("c", [.command, .shift])
        case .nextTab: CommandShortcut(key: .tab, symbol: "⇥", .control)
        case .previousTab: CommandShortcut(key: .tab, symbol: "⇥", [.control, .shift])
        case .shortcuts: CommandShortcut("/", [.command, .option])
        case .help: CommandShortcut("?", .command)
        case .theme, .moveTabToNewWindow, .mergeWindows, .installCommand, .removeIntegration: nil
        }
    }

    func isEnabled(_ state: CommandState) -> Bool {
        switch self {
        case .newTask, .newBoard: state.hasProject
        case .editBoard: state.hasBoard
        case .newTab, .moveTabToNewWindow: state.hasTab
        case .palette, .toggleSidebar: state.hasWindow
        case .go: state.hasTab
        case .listMode: state.hasProject
        case .taskStatus, .taskLink, .taskParent, .taskCopyKey: state.hasTask
        case .copyLink: state.hasLink
        case .nextTab, .previousTab: state.tabCount > 1
        case .mergeWindows: state.otherWindows > 0
        case .newWindow, .openFolder, .closeTab, .closeWindow, .theme, .shortcuts, .help, .installCommand, .removeIntegration: true
        }
    }
}

extension AppTheme {
    var titleKey: L10nKey {
        switch self {
        case .system: .themeSystem
        case .light: .themeLight
        case .dark: .themeDark
        }
    }
}
