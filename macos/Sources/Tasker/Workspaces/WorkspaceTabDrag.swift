import AppKit
import SwiftUI

enum WorkspaceTabDrag {
    static let pasteboardType = NSPasteboard.PasteboardType("com.technotronika.tasker.workspace-tab")
    static let threshold: CGFloat = 4
}

struct WorkspaceTabDragSurface: NSViewRepresentable {
    let reservedTrailing: CGFloat
    let image: @MainActor () -> NSImage?
    let click: () -> Void
    let begin: () -> Void
    let moved: (NSPoint) -> Bool
    let end: (NSWindow?, NSPoint, Bool) -> Void

    func makeNSView(context: Context) -> WorkspaceTabDragView {
        let view = WorkspaceTabDragView()
        update(view)
        return view
    }

    func updateNSView(_ view: WorkspaceTabDragView, context: Context) {
        update(view)
    }

    private func update(_ view: WorkspaceTabDragView) {
        view.reservedTrailing = reservedTrailing
        view.image = image
        view.click = click
        view.begin = begin
        view.moved = moved
        view.end = end
    }
}

final class WorkspaceTabDragView: NSView, NSDraggingSource {
    var reservedTrailing: CGFloat = 0
    var image: (@MainActor () -> NSImage?)?
    var click: (() -> Void)?
    var begin: (() -> Void)?
    var moved: ((NSPoint) -> Bool)?
    var end: ((NSWindow?, NSPoint, Bool) -> Void)?

    private var downPoint: NSPoint?
    private var dragging = false
    private weak var sourceWindow: NSWindow?

    private var hovering = false

    override var mouseDownCanMoveWindow: Bool { false }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) {
        hovering = true
        syncMovable()
    }

    override func mouseExited(with event: NSEvent) {
        hovering = false
        syncMovable()
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        window?.isMovable = true
        super.viewWillMove(toWindow: newWindow)
    }

    private func syncMovable() {
        window?.isMovable = !hovering && !dragging && downPoint == nil
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        if NSApp.currentEvent?.type == .rightMouseDown { return nil }
        let local = convert(point, from: superview)
        guard bounds.contains(local), local.x < bounds.width - reservedTrailing else { return nil }
        return self
    }

    override func mouseDown(with event: NSEvent) {
        downPoint = event.locationInWindow
        window?.isMovable = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard !dragging, let downPoint else { return }
        let distance = hypot(event.locationInWindow.x - downPoint.x, event.locationInWindow.y - downPoint.y)
        guard distance >= WorkspaceTabDrag.threshold else { return }
        startDrag(event)
    }

    override func mouseUp(with event: NSEvent) {
        if downPoint != nil, !dragging { click?() }
        downPoint = nil
        syncMovable()
    }

    private func startDrag(_ event: NSEvent) {
        dragging = true
        sourceWindow = window
        let item = NSPasteboardItem()
        item.setString("", forType: WorkspaceTabDrag.pasteboardType)
        let dragging = NSDraggingItem(pasteboardWriter: item)
        let picture = image?() ?? NSImage(size: bounds.size)
        dragging.setDraggingFrame(NSRect(origin: .zero, size: picture.size), contents: picture)
        let session = beginDraggingSession(with: [dragging], event: event, source: self)
        session.animatesToStartingPositionsOnCancelOrFail = true
        begin?()
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .withinApplication ? .move : []
    }

    func draggingSession(_ session: NSDraggingSession, movedTo screenPoint: NSPoint) {
        let overBar = moved?(screenPoint) ?? false
        session.animatesToStartingPositionsOnCancelOrFail = !overBar && (sourceWindow?.frame.contains(screenPoint) ?? true)
    }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        dragging = false
        downPoint = nil
        hovering = false
        syncMovable()
        sourceWindow?.isMovable = true
        let cancelled = NSApp.currentEvent?.type == .keyDown
        end?(sourceWindow, screenPoint, !cancelled)
    }
}

struct WorkspaceTabFrames: PreferenceKey {
    static let defaultValue: [UUID: CGRect] = [:]

    static func reduce(value: inout [UUID: CGRect], nextValue: () -> [UUID: CGRect]) {
        value.merge(nextValue()) { $1 }
    }
}

struct WorkspaceTabDropTarget: Equatable {
    var window: UUID
    var position: Int
}

enum WorkspaceTabDropPosition {
    static func resolve(x: CGFloat, midpoints: [CGFloat], gap: CGFloat, current: Int?) -> Int {
        for (index, midpoint) in midpoints.enumerated() {
            let shift = current.map { index >= $0 ? gap : 0 } ?? 0
            if midpoint - shift > x { return index }
        }
        return midpoints.count
    }
}

@MainActor
@Observable
final class WorkspaceTabDropZones {
    private(set) var target: WorkspaceTabDropTarget?

    @ObservationIgnored private var anchors: [UUID: WeakView] = [:]
    @ObservationIgnored private var frames: [UUID: [UUID: CGRect]] = [:]

    private struct WeakView {
        weak var view: NSView?
    }

    func register(window: UUID, anchor: NSView) {
        anchors[window] = WeakView(view: anchor)
    }

    func setFrames(_ value: [UUID: CGRect], window: UUID) {
        frames[window] = value
    }

    func update(screenPoint: NSPoint, layout: WorkspaceLayout, gap: CGFloat) {
        target = locate(screenPoint, layout: layout, gap: gap)
    }

    func clear() {
        target = nil
    }

    private func locate(_ point: NSPoint, layout: WorkspaceLayout, gap: CGFloat) -> WorkspaceTabDropTarget? {
        guard let top = NSApp.orderedWindows.first(where: { $0.isVisible && $0.frame.contains(point) }) else { return nil }
        for (id, anchor) in anchors {
            guard let view = anchor.view, view.window === top else { continue }
            let rect = top.convertToScreen(view.convert(view.bounds, to: nil))
            guard rect.contains(point), let window = layout.window(id) else { return nil }
            let current = target?.window == id ? target?.position : nil
            let midpoints = window.tabs.map { frames[id]?[$0.id]?.midX ?? .infinity }
            return WorkspaceTabDropTarget(window: id, position: WorkspaceTabDropPosition.resolve(x: point.x - rect.minX, midpoints: midpoints, gap: gap, current: current))
        }
        return nil
    }
}

struct WorkspaceTabBarAnchor: NSViewRepresentable {
    let window: UUID
    let zones: WorkspaceTabDropZones

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        zones.register(window: window, anchor: view)
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        zones.register(window: window, anchor: nsView)
    }
}
