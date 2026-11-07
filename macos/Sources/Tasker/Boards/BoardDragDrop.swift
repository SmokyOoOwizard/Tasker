import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct BoardCardHandle: NSViewRepresentable {
    let task: UUID
    let draggable: Bool
    let image: @MainActor () -> NSImage?
    let click: @MainActor () -> Void
    let open: @MainActor () -> Void
    var parents: [UUID] = []
    let openParent: @MainActor (UUID) -> Void
    let begin: @MainActor () -> Void
    let end: @MainActor () -> Void

    func makeNSView(context: Context) -> BoardCardHandleView {
        BoardCardHandleView()
    }

    func updateNSView(_ view: BoardCardHandleView, context: Context) {
        view.task = task
        view.draggable = draggable
        view.imageProvider = image
        view.onClick = click
        view.onOpen = open
        view.parents = parents
        view.onOpenParent = openParent
        view.onBegin = begin
        view.onEnd = end
    }
}

final class BoardCardHandleView: NSView, NSDraggingSource {
    var task = UUID()
    var draggable = true
    var imageProvider: @MainActor () -> NSImage? = { nil }
    var onClick: @MainActor () -> Void = {}
    var onOpen: @MainActor () -> Void = {}
    var parents: [UUID] = []
    var onOpenParent: @MainActor (UUID) -> Void = { _ in }
    var onBegin: @MainActor () -> Void = {}
    var onEnd: @MainActor () -> Void = {}

    private static let threshold: CGFloat = 5
    private var origin = NSPoint.zero
    private var dragging = false

    override var isFlipped: Bool { true }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: draggable ? .openHand : .arrow)
    }

    override func mouseDown(with event: NSEvent) {
        origin = event.locationInWindow
        dragging = false
    }

    override func mouseUp(with event: NSEvent) {
        guard !dragging else { return }
        let point = convert(event.locationInWindow, from: nil)
        if let index = BoardParentLayout.line(at: point.y, count: parents.count) {
            let parent = parents[index]
            MainActor.assumeIsolated { onOpenParent(parent) }
            return
        }
        MainActor.assumeIsolated {
            if event.clickCount >= 2 { onOpen() } else { onClick() }
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard draggable, !dragging else { return }
        let point = event.locationInWindow
        guard hypot(point.x - origin.x, point.y - origin.y) >= Self.threshold else { return }
        dragging = true
        let item = NSPasteboardItem()
        item.setString(task.uuidString, forType: .string)
        let dragItem = NSDraggingItem(pasteboardWriter: item)
        let image = MainActor.assumeIsolated { imageProvider() }
        dragItem.setDraggingFrame(bounds, contents: image ?? NSImage(size: bounds.size))
        MainActor.assumeIsolated { onBegin() }
        beginDraggingSession(with: [dragItem], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        .move
    }

    func ignoreModifierKeys(for session: NSDraggingSession) -> Bool { true }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        MainActor.assumeIsolated { onEnd() }
    }
}

enum BoardParentLayout {
    static let top: CGFloat = 10
    static let lineHeight: CGFloat = 16
    static let spacing: CGFloat = 2
    static let gap: CGFloat = 7

    static func line(at y: CGFloat, count: Int) -> Int? {
        guard count > 0, y >= top else { return nil }
        let step = lineHeight + spacing
        let index = Int((y - top) / step)
        guard index < count, (y - top) - CGFloat(index) * step <= lineHeight else { return nil }
        return index
    }
}

@MainActor
enum BoardDragState {
    static var active = false
}

struct BoardColumnFrames: PreferenceKey {
    static let defaultValue: [UUID: CGRect] = [:]

    static func reduce(value: inout [UUID: CGRect], nextValue: () -> [UUID: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

struct BoardDropOverlay: NSViewRepresentable {
    let frames: [UUID: CGRect]
    let target: (UUID?) -> Void
    let accepts: (UUID) -> Bool
    let drop: (UUID) -> Bool

    func makeNSView(context: Context) -> BoardDropView {
        let view = BoardDropView()
        view.registerForDraggedTypes([.string])
        return view
    }

    func updateNSView(_ view: BoardDropView, context: Context) {
        view.frames = frames
        view.onTarget = target
        view.canAccept = accepts
        view.onDrop = drop
    }
}

final class BoardDropView: NSView {
    var frames: [UUID: CGRect] = [:]
    var onTarget: (UUID?) -> Void = { _ in }
    var canAccept: (UUID) -> Bool = { _ in false }
    var onDrop: (UUID) -> Bool = { _ in false }

    private var scroller: Timer?
    private var scrollSpeed: CGFloat = 0

    override var isFlipped: Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? {
        return MainActor.assumeIsolated { BoardDragState.active } ? super.hitTest(point) : nil
    }

    private func column(_ info: NSDraggingInfo) -> UUID? {
        let point = convert(info.draggingLocation, from: nil)
        return frames.first { $0.value.minX - 7 <= point.x && point.x < $0.value.maxX + 7 }?.key
    }

    private func operation(_ info: NSDraggingInfo) -> NSDragOperation {
        let column = column(info)
        onTarget(column.flatMap { canAccept($0) ? $0 : nil })
        updateScroll(convert(info.draggingLocation, from: nil))
        guard let column, canAccept(column) else { return [] }
        return .move
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        return operation(sender)
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { operation(sender) }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        onTarget(nil)
        stopScroll()
    }

    override func draggingEnded(_ sender: NSDraggingInfo) {
        onTarget(nil)
        stopScroll()
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        stopScroll()
        onTarget(nil)
        guard let column = column(sender), canAccept(column) else { return false }
        return onDrop(column)
    }

    private func updateScroll(_ point: NSPoint) {
        let edge: CGFloat = 60
        if point.x < edge {
            scrollSpeed = -max(4, (edge - point.x) / 3)
        } else if point.x > bounds.width - edge {
            scrollSpeed = max(4, (point.x - (bounds.width - edge)) / 3)
        } else {
            scrollSpeed = 0
        }
        if scrollSpeed == 0 {
            stopScroll()
        } else if scroller == nil {
            scroller = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.step() }
            }
        }
    }

    private func stopScroll() {
        scroller?.invalidate()
        scroller = nil
        scrollSpeed = 0
    }

    private func step() {
        guard let content = window?.contentView, let scroll = Self.scrollView(in: content, containing: self) else { return }
        let clip = scroll.contentView
        let maxX = max(0, (scroll.documentView?.frame.width ?? 0) - clip.bounds.width)
        let x = min(max(0, clip.bounds.origin.x + scrollSpeed), maxX)
        clip.setBoundsOrigin(NSPoint(x: x, y: clip.bounds.origin.y))
        scroll.reflectScrolledClipView(clip)
    }

    private static func scrollView(in root: NSView, containing view: NSView) -> NSScrollView? {
        let target = view.convert(view.bounds, to: nil)
        var best: NSScrollView?
        func visit(_ node: NSView) {
            if let scroll = node as? NSScrollView, scroll.convert(scroll.bounds, to: nil).intersects(target),
               (scroll.documentView?.frame.width ?? 0) > scroll.contentView.bounds.width + 1 {
                if best == nil || scroll.bounds.width > best!.bounds.width { best = scroll }
            }
            node.subviews.forEach(visit)
        }
        visit(root)
        return best
    }
}

struct BoardShake: GeometryEffect {
    var shakes: CGFloat

    var animatableData: CGFloat {
        get { shakes }
        set { shakes = newValue }
    }

    func effectValue(size: CGSize) -> ProjectionTransform {
        ProjectionTransform(CGAffineTransform(translationX: 5 * sin(shakes * .pi * 4), y: 0))
    }
}

struct BoardFlow: Layout {
    var spacing: CGFloat = 4

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrange(proposal.width ?? .infinity, subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let result = arrange(bounds.width, subviews)
        for (index, frame) in result.frames.enumerated() {
            subviews[index].place(at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY), proposal: ProposedViewSize(frame.size))
        }
    }

    private func arrange(_ width: CGFloat, _ subviews: Subviews) -> (size: CGSize, frames: [CGRect]) {
        var frames: [CGRect] = []
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowHeight: CGFloat = 0
        var maxX: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(ProposedViewSize(width: width.isFinite ? width : nil, height: nil))
            if x > 0, x + size.width > width {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            frames.append(CGRect(origin: CGPoint(x: x, y: y), size: size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
            maxX = max(maxX, x - spacing)
        }
        return (CGSize(width: maxX, height: y + rowHeight), frames)
    }
}
