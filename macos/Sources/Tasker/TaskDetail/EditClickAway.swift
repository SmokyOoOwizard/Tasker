import AppKit
import SwiftUI

private struct ClickAwayMonitor: NSViewRepresentable {
    let active: Bool
    let perform: () -> Void

    func makeNSView(context: Context) -> ClickAwayView {
        ClickAwayView()
    }

    func updateNSView(_ view: ClickAwayView, context: Context) {
        view.perform = perform
        view.setActive(active)
    }

    static func dismantleNSView(_ view: ClickAwayView, coordinator: ()) {
        view.setActive(false)
    }
}

final class ClickAwayView: NSView {
    var perform: () -> Void = {}
    private var monitor: Any?

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func setActive(_ active: Bool) {
        if active, monitor == nil {
            monitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] event in
                self?.handle(event)
                return event
            }
        } else if !active, let monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
    }

    private func handle(_ event: NSEvent) {
        guard let window, event.window === window else { return }
        let point = convert(event.locationInWindow, from: nil)
        guard !bounds.contains(point) else { return }
        let action = perform
        DispatchQueue.main.async { action() }
    }
}

extension View {
    func endsOnClickAway(active: Bool, perform: @escaping () -> Void) -> some View {
        background(ClickAwayMonitor(active: active, perform: perform))
    }
}
