import AppKit
import Foundation
import Observation

@MainActor
@Observable
final class WorkspaceController {
    let layout: WorkspaceLayout
    let dropZones = WorkspaceTabDropZones()
    private(set) var failure: String?

    @ObservationIgnored private let settings: WorkspaceSettingsFile
    @ObservationIgnored private let fileManager: FileManager

    init(layout: WorkspaceLayout, settings: WorkspaceSettingsFile, fileManager: FileManager = .default) {
        self.layout = layout
        self.settings = settings
        self.fileManager = fileManager
    }

    static func live() -> WorkspaceController {
        WorkspaceController(layout: WorkspaceLayout(), settings: WorkspaceSettingsFile(directory: DaemonPaths().dataDirectory))
    }

    func sync(with state: DaemonConnectionState) {
        guard let status = state.status else { return }
        layout.seed(from: status.workspaces.map(\.path))
    }

    func reopenable(in status: DaemonStatus?) -> [String] {
        let open = Set(layout.openPaths)
        var result = layout.recents
        for path in (status?.workspaces ?? []).map({ WorkspacePath.canonical($0.path) }) where !open.contains(path) && !result.contains(path) {
            result.append(path)
        }
        return result
    }

    func needsData(_ path: String) -> Bool {
        !WorkspacePath.hasData(WorkspacePath.canonical(path), fileManager: fileManager)
    }

    func add(_ path: String, createData: Bool, window: UUID) async {
        let canonical = WorkspacePath.canonical(path)
        failure = nil
        do {
            if createData {
                try fileManager.createDirectory(at: URL(fileURLWithPath: canonical).appending(path: WorkspacePath.dataFolder), withIntermediateDirectories: true)
            }
            let settings = settings
            _ = try await Task.detached { try settings.add(canonical) }.value
            layout.open(canonical, in: window)
        } catch {
            failure = error.localizedDescription
        }
    }

    func openFolder(_ l: Localizer, window: UUID) async {
        guard let chosen = FolderPicker.choose(prompt: l.string(.workspacePanelPrompt), message: l.string(.workspacePanelMessage)) else { return }
        let path = WorkspacePath.canonical(chosen)
        let create = needsData(path)
        if create, !FolderPicker.confirmCreate(l, folder: path) { return }
        await add(path, createData: create, window: window)
    }

    func reopen(_ path: String, window: UUID) async {
        guard fileManager.fileExists(atPath: path) else {
            layout.forget(recent: path)
            failure = path
            return
        }
        await add(path, createData: false, window: window)
    }

    func reconnect(_ path: String) async {
        failure = nil
        do {
            let settings = settings
            _ = try await Task.detached { try settings.add(path) }.value
        } catch {
            failure = error.localizedDescription
        }
    }

    func dismissFailure() {
        failure = nil
    }
}

@MainActor
enum FolderPicker {
    static func choose(prompt: String, message: String) -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.showsHiddenFiles = false
        panel.prompt = prompt
        panel.message = message
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        return url.path
    }

    static func confirmCreate(_ l: Localizer, folder: String) -> Bool {
        let alert = NSAlert()
        alert.messageText = l.string(.workspaceNoTaskerTitle)
        alert.informativeText = folder + "\n\n" + l.string(.workspaceNoTaskerBody)
        alert.addButton(withTitle: l.string(.workspaceNoTaskerCreate))
        alert.addButton(withTitle: l.string(.commonCancel))
        return alert.runModal() == .alertFirstButtonReturn
    }
}
