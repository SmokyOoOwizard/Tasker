import AppKit
import SwiftUI

struct WorkspaceWindowKey: EnvironmentKey {
    static let defaultValue = UUID()
}

extension EnvironmentValues {
    var workspaceWindow: UUID {
        get { self[WorkspaceWindowKey.self] }
        set { self[WorkspaceWindowKey.self] = newValue }
    }
}

struct WorkspaceWindowBinder: NSViewRepresentable {
    let id: UUID
    let layout: WorkspaceLayout
    let close: Bool

    func makeCoordinator() -> Coordinator {
        Coordinator(id: id, layout: layout)
    }

    func makeNSView(context: Context) -> BinderView {
        let view = BinderView()
        view.coordinator = context.coordinator
        return view
    }

    func updateNSView(_ nsView: BinderView, context: Context) {
        if close { context.coordinator.closeWindow() }
    }

    final class BinderView: NSView {
        var coordinator: Coordinator?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window, let coordinator else { return }
            DispatchQueue.main.async { coordinator.bind(window) }
        }
    }

    @MainActor
    final class Coordinator {
        private let id: UUID
        private let layout: WorkspaceLayout
        private weak var window: NSWindow?
        private var observers: [NSObjectProtocol] = []
        private var closing = false

        init(id: UUID, layout: WorkspaceLayout) {
            self.id = id
            self.layout = layout
        }

        func bind(_ window: NSWindow) {
            guard self.window !== window else { return }
            self.window = window
            window.isRestorable = false
            window.tabbingMode = .disallowed
            if let frame = layout.takePendingFrame(id) ?? layout.window(id)?.frame {
                window.setFrame(frame, display: true)
            } else if let name = layout.window(id)?.frameName {
                window.setFrameUsingName(name)
            }
            observe(window, NSWindow.willCloseNotification) { $0.windowWillClose() }
            observe(window, NSWindow.didBecomeKeyNotification) { $0.windowBecameKey() }
            observe(window, NSWindow.didMoveNotification) { $0.noteFrame() }
            observe(window, NSWindow.didResizeNotification) { $0.noteFrame() }
            noteFrame()
            if closing { window.close() }
            if window.isKeyWindow { windowBecameKey() }
        }

        func closeWindow() {
            closing = true
            DispatchQueue.main.async { [weak self] in self?.window?.close() }
        }

        private func observe(_ window: NSWindow, _ name: Notification.Name, _ action: @escaping @MainActor (Coordinator) -> Void) {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    if let self { action(self) }
                }
            })
        }

        private func noteFrame() {
            if let window { layout.noteFrame(window.frame, of: id) }
        }

        private func windowBecameKey() {
            layout.setActive(id)
        }

        private func windowWillClose() {
            layout.windowClosed(id)
        }
    }
}
