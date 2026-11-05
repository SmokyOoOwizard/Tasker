import Foundation
import TaskerAPI

struct PaletteWorkspace: Equatable {
    var path: String
    var current: Bool
}

struct PaletteActions {
    var goTo: @MainActor (AppSection) -> Void
    var createTask: @MainActor () -> Void
    var createBoard: @MainActor () -> Void
    var editBoard: @MainActor (UUID) -> Void
    var showSeries: @MainActor (UUID) -> Void
    var openSettings: @MainActor (SettingsPage) -> Void
    var chooseProject: @MainActor (UUID) -> Void
    var openWorkspace: @MainActor (String) -> Void
    var openFolder: @MainActor () -> Void
    var setTheme: @MainActor (AppTheme) -> Void
    var setLanguage: @MainActor (AppLanguage) -> Void
    var perform: @MainActor (AppCommand) -> Void = { _ in }
}

struct PaletteContext {
    var hasTab: Bool
    var hasProject: Bool
    var projects: [ProjectFields]
    var currentProject: UUID?
    var workspaces: [PaletteWorkspace]
    var theme: AppTheme
    var isDark: Bool
    var language: AppLanguage
    var settingsPages: [SettingsPage] = []
    var commands = CommandState.none
}

@MainActor
enum PaletteCommands {
    static func build(_ context: PaletteContext, vocabulary: TaskListVocabulary, actions: PaletteActions, l: Localizer) -> [PaletteEntry] {
        var result: [PaletteEntry] = []

        if context.hasTab {
            result.append(PaletteEntry(
                id: "create-task", title: l.string(.taskCreateAction), detail: l.string(.paletteKindAction), icon: .mockup(.plus),
                keywords: "create new task", shortcut: AppCommand.newTask.shortcut?.display, isEnabled: context.hasProject, run: actions.createTask))
            result.append(PaletteEntry(
                id: "create-board", title: l.string(.paletteNewBoard), detail: l.string(.paletteKindAction), icon: .mockup(.plus),
                keywords: "create new board", shortcut: AppCommand.newBoard.shortcut?.display, isEnabled: context.hasProject, run: actions.createBoard))
            for section in AppSection.allCases {
                result.append(PaletteEntry(
                    id: "go:" + section.rawValue, title: l.string(.paletteGoTo, l.string(section.titleKey)),
                    detail: l.string(section == .settings ? .paletteKindSettings : .paletteKindGo), icon: .mockup(section.icon),
                    keywords: section.rawValue, shortcut: AppCommand.go(section).shortcut?.display, run: { actions.goTo(section) }))
            }
            if context.hasProject {
                for mode in TaskListMode.allCases {
                    let command = AppCommand.listMode(mode)
                    result.append(PaletteEntry(
                        id: "listmode:" + mode.rawValue, title: l.string(.paletteListMode, l.string(mode.titleKey)), detail: l.string(.paletteKindView),
                        icon: .mockup(.list), keywords: "list view layout tree flat groups", shortcut: command.shortcut?.display,
                        isEnabled: context.commands.listMode != mode, run: { actions.perform(command) }))
                }
            }
            if context.commands.hasLink {
                let command = AppCommand.copyLink
                result.append(PaletteEntry(
                    id: "task:" + command.id, title: l.string(command.titleKey), detail: l.string(.paletteKindAction),
                    icon: .system("link"), keywords: "copy link url", shortcut: command.shortcut?.display,
                    run: { actions.perform(command) }))
            }
            if context.commands.hasTask {
                for command in [AppCommand.taskStatus, .taskLink, .taskParent, .taskCopyKey] {
                    result.append(PaletteEntry(
                        id: "task:" + command.id, title: l.string(command.titleKey), detail: l.string(.paletteKindAction),
                        icon: .system("checkmark.circle"), keywords: "task status link parent copy key", shortcut: command.shortcut?.display,
                        run: { actions.perform(command) }))
                }
            }
            if context.hasProject {
                for id in vocabulary.boardOrder {
                    guard let name = vocabulary.boardNames[id] else { continue }
                    result.append(PaletteEntry(
                        id: "board:" + id.uuidString, title: l.string(.paletteBoardEdit, name), detail: l.string(.paletteKindBoard),
                        icon: .mockup(.board), keywords: "board edit settings configure", run: { actions.editBoard(id) }))
                }
            }
            if context.hasProject {
                for page in context.settingsPages {
                    result.append(PaletteEntry(
                        id: "settings:" + page.rawValue, title: l.string(.paletteSettingsPage, l.string(page.titleKey)),
                        detail: l.string(.paletteKindSettings), icon: .mockup(.gear), keywords: "settings " + page.keywords,
                        run: { actions.openSettings(page) }))
                }
            }
            for id in vocabulary.seriesOrder {
                guard let prefix = vocabulary.prefixes[id] else { continue }
                let name = vocabulary.seriesNames[id] ?? prefix
                result.append(PaletteEntry(
                    id: "series:" + id.uuidString, title: l.string(.paletteSeries, prefix, name), detail: l.string(.paletteKindSeries),
                    icon: .system("tag"), keywords: "series " + prefix, run: { actions.showSeries(id) }))
            }
            for project in context.projects where project.id != context.currentProject {
                result.append(PaletteEntry(
                    id: "project:" + project.id.uuidString, title: l.string(.paletteProject, project.name), detail: l.string(.paletteKindProject),
                    icon: .system("folder"), keywords: "project switch", run: { actions.chooseProject(project.id) }))
            }
        }

        for workspace in context.workspaces where !workspace.current {
            result.append(PaletteEntry(
                id: "workspace:" + workspace.path, title: l.string(.paletteWorkspace, WorkspacePath.name(workspace.path)),
                detail: l.string(.paletteKindWorkspace), icon: .system("folder.badge.gearshape"), keywords: "workspace " + workspace.path,
                run: { actions.openWorkspace(workspace.path) }))
        }
        result.append(PaletteEntry(
            id: "open-folder", title: l.string(.workspaceOpen), detail: l.string(.paletteKindWorkspace), icon: .system("folder.badge.plus"),
            keywords: "open folder workspace", shortcut: AppCommand.openFolder.shortcut?.display, run: actions.openFolder))
        result.append(PaletteEntry(
            id: "shortcuts", title: l.string(.paletteShortcuts), detail: l.string(.paletteKindView), icon: .system("keyboard"),
            keywords: "keyboard shortcuts hotkeys help", shortcut: AppCommand.shortcuts.shortcut?.display, run: { actions.perform(.shortcuts) }))

        let target: AppTheme = context.isDark ? .light : .dark
        result.append(PaletteEntry(
            id: "theme-switch", title: l.string(.paletteThemeSwitch, l.string(target == .dark ? .paletteThemeDark : .paletteThemeLight)),
            detail: l.string(.paletteKindView), icon: .system(target == .dark ? "moon" : "sun.max"), keywords: "theme dark light",
            run: { actions.setTheme(target) }))
        if context.theme != .system {
            result.append(PaletteEntry(
                id: "theme-system", title: l.string(.paletteThemeSystem), detail: l.string(.paletteKindView), icon: .system("circle.lefthalf.filled"),
                keywords: "theme system", run: { actions.setTheme(.system) }))
        }
        for option in AppLanguage.allCases where option != context.language {
            result.append(PaletteEntry(
                id: "language:" + option.rawValue, title: l.string(.paletteLanguage, l.string(option.labelKey)),
                detail: l.string(.paletteKindView), icon: .system("globe"), keywords: "language",
                run: { actions.setLanguage(option) }))
        }
        return result
    }
}
