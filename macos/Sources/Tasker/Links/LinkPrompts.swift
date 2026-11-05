import AppKit

@MainActor
enum LinkPrompts {
    static func invalid(_ l: Localizer) {
        let alert = NSAlert()
        alert.messageText = l.string(.linkInvalidTitle)
        alert.informativeText = l.string(.linkInvalidBody)
        alert.addButton(withTitle: l.string(.commonOk))
        alert.runModal()
    }

    static func unknownWorkspace(_ l: Localizer) -> Bool {
        let alert = NSAlert()
        alert.messageText = l.string(.linkWorkspaceUnknownTitle)
        alert.informativeText = l.string(.linkWorkspaceUnknownBody)
        alert.addButton(withTitle: l.string(.workspaceOpen))
        alert.addButton(withTitle: l.string(.commonCancel))
        return alert.runModal() == .alertFirstButtonReturn
    }

    static func message(_ title: String, _ l: Localizer) {
        let alert = NSAlert()
        alert.messageText = title
        alert.addButton(withTitle: l.string(.commonOk))
        alert.runModal()
    }

    static func choose(_ candidates: [LinkCandidate], _ l: Localizer) -> LinkCandidate? {
        let alert = NSAlert()
        alert.messageText = l.string(.linkChooseTitle)
        alert.informativeText = l.string(.linkChooseBody)
        let popup = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 320, height: 26), pullsDown: false)
        for candidate in candidates {
            popup.addItem(withTitle: l.string(.linkCandidate, candidate.title, candidate.project.name))
        }
        alert.accessoryView = popup
        alert.addButton(withTitle: l.string(.linkChooseOpen))
        alert.addButton(withTitle: l.string(.commonCancel))
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        let index = popup.indexOfSelectedItem
        return candidates.indices.contains(index) ? candidates[index] : nil
    }
}
