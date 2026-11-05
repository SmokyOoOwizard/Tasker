enum ShortcutTable {
    struct Row: Sendable {
        let title: L10nKey
        let keys: [String]
        let command: String?
    }

    struct Group: Sendable {
        let title: L10nKey
        let rows: [Row]
    }

    static let groups: [Group] = [
        Group(title: .shortcutsGroupGo, rows: [
            Row(title: .paletteTitle, keys: ["⌘", "K"], command: "palette"),
            Row(title: .sectionTasks, keys: ["⌘", "1"], command: "go.tasks"),
            Row(title: .sectionBoards, keys: ["⌘", "2"], command: "go.boards"),
            Row(title: .sectionSettings, keys: ["⌘", "3"], command: "go.settings"),
            Row(title: .sectionAdmin, keys: ["⌘", "4"], command: "go.admin"),
            Row(title: .sidebarHide, keys: ["⌘", "0"], command: "toggleSidebar"),
        ]),
        Group(title: .shortcutsGroupTask, rows: [
            Row(title: .taskChangeStatus, keys: ["⌥", "⌘", "S"], command: "taskStatus"),
            Row(title: .taskLink, keys: ["⌘", "L"], command: "taskLink"),
            Row(title: .taskSetParent, keys: ["⌥", "⌘", "P"], command: "taskParent"),
            Row(title: .taskCopyKey, keys: ["⌥", "⌘", "C"], command: "taskCopyKey"),
            Row(title: .deeplinkCopy, keys: ["⇧", "⌘", "C"], command: "copyLink"),
        ]),
        Group(title: .shortcutsGroupView, rows: [
            Row(title: .taskListViewGroups, keys: ["⌥", "⌘", "1"], command: "listMode.groups"),
            Row(title: .taskListViewFlat, keys: ["⌥", "⌘", "2"], command: "listMode.flat"),
            Row(title: .taskListViewTree, keys: ["⌥", "⌘", "3"], command: "listMode.tree"),
            Row(title: .boardTitleEdit, keys: ["⌥", "⌘", "E"], command: "editBoard"),
        ]),
        Group(title: .shortcutsGroupList, rows: [
            Row(title: .shortcutsListMove, keys: ["J", "K"], command: nil),
            Row(title: .shortcutsListPeek, keys: ["Space"], command: nil),
            Row(title: .shortcutsListOpen, keys: ["↵"], command: nil),
            Row(title: .shortcutsListStatus, keys: ["S"], command: nil),
            Row(title: .shortcutsEscape, keys: ["Esc"], command: nil),
        ]),
        Group(title: .shortcutsGroupFile, rows: [
            Row(title: .taskCreateAction, keys: ["⌘", "N"], command: "newTask"),
            Row(title: .paletteNewBoard, keys: ["⌥", "⌘", "N"], command: "newBoard"),
            Row(title: .tabNew, keys: ["⌘", "T"], command: "newTab"),
            Row(title: .windowNew, keys: ["⇧", "⌘", "N"], command: "newWindow"),
            Row(title: .workspaceOpen, keys: ["⌘", "O"], command: "openFolder"),
            Row(title: .workspaceCloseTab, keys: ["⌘", "W"], command: "closeTab"),
            Row(title: .windowClose, keys: ["⇧", "⌘", "W"], command: "closeWindow"),
            Row(title: .tabNext, keys: ["⌃", "⇥"], command: "nextTab"),
            Row(title: .tabPrevious, keys: ["⌃", "⇧", "⇥"], command: "previousTab"),
            Row(title: .shortcutsSettings, keys: ["⌘", ","], command: nil),
        ]),
        Group(title: .shortcutsGroupHelp, rows: [
            Row(title: .shortcutsOpenHelp, keys: ["⌘", "?"], command: "help"),
            Row(title: .paletteShortcuts, keys: ["⌥", "⌘", "/"], command: "shortcuts"),
        ]),
    ]
}
