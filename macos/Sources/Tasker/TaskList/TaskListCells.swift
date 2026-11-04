import AppKit
import SwiftUI

extension DesignColor {
    var nsColor: NSColor { NSColor(named: assetName) ?? .labelColor }
}

extension NSColor {
    convenience init(designHex: String) {
        let (r, g, b, a) = DesignHex.components(designHex)
        self.init(srgbRed: r, green: g, blue: b, alpha: a)
    }

    static func designMix(_ hex: String, _ amount: CGFloat, with base: String) -> NSColor {
        let (r1, g1, b1, _) = DesignHex.components(hex)
        let (r2, g2, b2, _) = DesignHex.components(base)
        let k = Double(amount)
        return NSColor(srgbRed: r1 * k + r2 * (1 - k), green: g1 * k + g2 * (1 - k), blue: b1 * k + b2 * (1 - k), alpha: 1)
    }

    static func designTag(_ hex: String, text: Bool) -> NSColor {
        NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            switch (text, dark) {
            case (false, false): return designMix(hex, DesignTag.backgroundLightMix, with: DesignTag.backgroundLightBase)
            case (false, true): return designMix(hex, DesignTag.backgroundDarkMix, with: DesignTag.backgroundDarkBase)
            case (true, false): return designMix(hex, DesignTag.textLightMix, with: DesignTag.textLightBase)
            case (true, true): return designMix(hex, DesignTag.textDarkMix, with: DesignTag.textDarkBase)
            }
        }
    }
}

@MainActor
enum TaskListFonts {
    static let base = NSFont.systemFont(ofSize: DesignTypography.baseSize)
    static let emphasis = NSFont.systemFont(ofSize: DesignTypography.baseSize, weight: NSFont.Weight(rawValue: DesignTypography.emphasisWeight))
    static let small = NSFont.systemFont(ofSize: DesignTypography.smallSize)
    static let caption = NSFont.systemFont(ofSize: DesignTypography.captionSize)
    static let mono = NSFont.monospacedSystemFont(ofSize: DesignTypography.monoSize, weight: .regular)
}

final class StatusGlyphView: NSView {
    var glyph: StatusGlyph = .named(.dashed) { didSet { if glyph != oldValue { needsDisplay = true } } }
    var tint: NSColor = .labelColor { didSet { needsDisplay = true } }

    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        StatusGlyphRenderer.draw(glyph, tint: tint, in: bounds, context: context)
    }

    static func image(_ glyph: StatusGlyph, tint: NSColor, size: CGFloat = DesignLayout.statusIconBox) -> NSImage {
        NSImage(size: NSSize(width: size, height: size), flipped: true) { rect in
            if let context = NSGraphicsContext.current?.cgContext {
                StatusGlyphRenderer.draw(glyph, tint: tint, in: rect, context: context)
            }
            return true
        }
    }
}

enum StatusGlyphRenderer {
    static func draw(_ glyph: StatusGlyph, tint: NSColor, in rect: CGRect, context: CGContext) {
        let side = min(rect.width, rect.height)
        let k = side / StatusIconName.box
        context.saveGState()
        context.translateBy(x: rect.midX - side / 2, y: rect.midY - side / 2)
        for primitive in glyph.primitives {
            let path = primitive.shape.path(scale: k).cgPath
            let paint = primitive.paint
            let color = (paint.color?.nsColor ?? tint).cgColor
            if let opacity = paint.fillOpacity {
                context.addPath(path)
                context.setFillColor(color.copy(alpha: color.alpha * opacity) ?? color)
                context.fillPath()
            }
            if let width = paint.strokeWidth {
                context.addPath(path)
                context.setStrokeColor(color)
                context.setLineWidth(width * k)
                context.setLineCap(paint.cap)
                context.setLineJoin(paint.join)
                context.setLineDash(phase: 0, lengths: paint.dash.map { $0 * k })
                context.strokePath()
            }
        }
        context.restoreGState()
    }
}

final class TaskTagView: NSView {
    private var text = NSAttributedString()
    private var fill = NSColor.clear

    override var isFlipped: Bool { true }

    func configure(_ chip: TaskChip) {
        let hex = chip.colorHex
        fill = .designTag(hex, text: false)
        text = NSAttributedString(string: chip.text, attributes: [.font: TaskListFonts.small, .foregroundColor: NSColor.designTag(hex, text: true)])
        needsDisplay = true
    }

    var width: CGFloat { ceil(text.size().width) + DesignLayout.tagPadX * 2 }

    override func draw(_ dirtyRect: NSRect) {
        fill.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: DesignRadius.tag, yRadius: DesignRadius.tag).fill()
        let size = text.size()
        text.draw(at: NSPoint(x: DesignLayout.tagPadX, y: (bounds.height - size.height) / 2))
    }
}

final class TaskRowView: NSTableRowView {
    private var hovering: Bool {
        guard let outline = superview as? TaskOutlineView, outline.hoveredRow >= 0 else { return false }
        return outline.row(for: self) == outline.hoveredRow
    }

    override func drawBackground(in dirtyRect: NSRect) {
        (hovering ? DesignColor.hov : DesignColor.bg).nsColor.setFill()
        bounds.fill()
        let line = isGroupRowStyle ? DesignColor.line.nsColor : DesignColor.line.nsColor.withAlphaComponent(0.55)
        line.setFill()
        NSRect(x: 0, y: bounds.maxY - DesignLayout.borderWidth, width: bounds.width, height: DesignLayout.borderWidth).fill()
    }

    override func drawSelection(in dirtyRect: NSRect) {
        DesignColor.sel.nsColor.setFill()
        bounds.fill()
        DesignColor.line.nsColor.withAlphaComponent(0.55).setFill()
        NSRect(x: 0, y: bounds.maxY - DesignLayout.borderWidth, width: bounds.width, height: DesignLayout.borderWidth).fill()
        DesignColor.acc.nsColor.setFill()
        let bar = NSRect(x: 0, y: 6, width: DesignLayout.rowSelectionBar, height: bounds.height - 12)
        NSBezierPath(roundedRect: bar, xRadius: DesignRadius.xs, yRadius: DesignRadius.xs).fill()
    }

    override var interiorBackgroundStyle: NSView.BackgroundStyle { .normal }
}

struct TaskCellContent {
    var reference: String
    var title: String
    var repeated: Bool
    var typeName: String?
    var chips: [TaskChip]
    var date: String?
    var glyph: StatusGlyph
    var statusColor: NSColor
    var statusName: String
}

final class TaskCellView: NSTableCellView {
    static let identifier = NSUserInterfaceItemIdentifier("TaskCell")

    let statusButton = StatusButton()
    private let reference = TaskCellView.label(TaskListFonts.mono, DesignColor.dim)
    private let title = TaskCellView.label(TaskListFonts.base, DesignColor.tx)
    private let repeated = TaskCellView.label(TaskListFonts.caption, DesignColor.dim)
    private let type = TaskCellView.label(TaskListFonts.small, DesignColor.mut)
    private let date = TaskCellView.label(TaskListFonts.small, DesignColor.mut)
    private var tags: [TaskTagView] = []
    private var content: TaskCellContent?

    override init(frame: NSRect) {
        super.init(frame: frame)
        identifier = Self.identifier
        for view in [statusButton, reference, title, repeated, type, date] as [NSView] { addSubview(view) }
        title.lineBreakMode = .byTruncatingTail
        date.alignment = .right
    }

    required init?(coder: NSCoder) { nil }

    private static func label(_ font: NSFont, _ color: DesignColor) -> NSTextField {
        let field = NSTextField(labelWithString: "")
        field.font = font
        field.textColor = color.nsColor
        field.lineBreakMode = .byClipping
        field.maximumNumberOfLines = 1
        field.cell?.usesSingleLineMode = true
        return field
    }

    func configure(_ content: TaskCellContent, repeatedText: String, repeatedHint: String, statusHint: String) {
        self.content = content
        reference.stringValue = content.reference
        reference.isHidden = content.reference.isEmpty
        title.stringValue = content.title
        title.toolTip = content.title
        repeated.stringValue = repeatedText
        repeated.isHidden = !content.repeated
        repeated.toolTip = repeatedHint
        type.stringValue = content.typeName ?? ""
        type.isHidden = content.typeName == nil
        date.stringValue = content.date ?? ""
        date.isHidden = content.date == nil
        statusButton.glyph.glyph = content.glyph
        statusButton.glyph.tint = content.statusColor
        statusButton.toolTip = statusHint
        statusButton.setAccessibilityLabel(content.statusName)
        while tags.count < content.chips.count {
            let tag = TaskTagView()
            addSubview(tag)
            tags.append(tag)
        }
        for (index, tag) in tags.enumerated() {
            tag.isHidden = index >= content.chips.count
            if index < content.chips.count { tag.configure(content.chips[index]) }
        }
        needsLayout = true
    }

    override func layout() {
        super.layout()
        let height = bounds.height
        let gap = DesignSpacing.gap10
        var x: CGFloat = 0
        let button = DesignLayout.statusButton
        statusButton.frame = NSRect(x: x, y: (height - button) / 2, width: button, height: button)
        x += button + gap
        if !reference.isHidden {
            let width = max(DesignLayout.refMinWidth, ceil(reference.intrinsicContentSize.width))
            reference.frame = centered(x, width, reference)
            x += width + gap
        }
        var right = bounds.width - DesignSpacing.pagePadX
        if !date.isHidden {
            let width = max(DesignLayout.dateMinWidth, ceil(date.intrinsicContentSize.width))
            right -= width
            date.frame = centered(right, width, date)
            right -= gap
        }
        for tag in tags.reversed() where !tag.isHidden {
            let width = tag.width
            guard right - width > x + 80 else {
                tag.isHidden = true
                continue
            }
            right -= width
            tag.frame = NSRect(x: right, y: (height - DesignLayout.tagHeight) / 2, width: width, height: DesignLayout.tagHeight)
            right -= gap
        }
        if !type.isHidden {
            let width = ceil(type.intrinsicContentSize.width)
            if right - width > x + 80 {
                right -= width
                type.frame = centered(right, width, type)
                right -= gap
            } else {
                type.frame = .zero
            }
        }
        var titleWidth = max(0, right - x)
        if !repeated.isHidden {
            let width = ceil(repeated.intrinsicContentSize.width)
            titleWidth = max(0, min(ceil(title.intrinsicContentSize.width), titleWidth - width - 4))
            repeated.frame = centered(x + titleWidth + 4, width, repeated)
        }
        title.frame = centered(x, titleWidth, title)
    }

    private func centered(_ x: CGFloat, _ width: CGFloat, _ field: NSTextField) -> NSRect {
        let h = ceil(field.intrinsicContentSize.height)
        return NSRect(x: x, y: (bounds.height - h) / 2, width: width, height: h)
    }
}

final class StatusButton: NSButton {
    let glyph = StatusGlyphView()
    private var tracking: NSTrackingArea?

    init() {
        super.init(frame: .zero)
        title = ""
        isBordered = false
        setButtonType(.momentaryChange)
        focusRingType = .none
        addSubview(glyph)
    }

    required init?(coder: NSCoder) { nil }

    override func layout() {
        super.layout()
        let side = DesignLayout.statusIconBox
        glyph.frame = NSRect(x: (bounds.width - side) / 2, y: (bounds.height - side) / 2, width: side, height: side)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInActiveApp, .inVisibleRect], owner: self)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseEntered(with event: NSEvent) {
        needsDisplay = true
    }

    override func mouseExited(with event: NSEvent) {
        needsDisplay = true
    }

    private var hovering: Bool {
        guard let window else { return false }
        return bounds.contains(convert(window.mouseLocationOutsideOfEventStream, from: nil))
    }

    override func draw(_ dirtyRect: NSRect) {
        guard hovering else { return }
        DesignColor.line.nsColor.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: DesignRadius.chip, yRadius: DesignRadius.chip).fill()
    }
}

final class GroupCellView: NSTableCellView {
    static let identifier = NSUserInterfaceItemIdentifier("GroupCell")

    private let chevron = NSImageView()
    private let glyph = StatusGlyphView()
    private let name = NSTextField(labelWithString: "")
    private let count = NSTextField(labelWithString: "")

    override init(frame: NSRect) {
        super.init(frame: frame)
        identifier = Self.identifier
        name.font = TaskListFonts.emphasis
        name.textColor = DesignColor.mut.nsColor
        count.font = TaskListFonts.base
        count.textColor = DesignColor.dim.nsColor
        chevron.contentTintColor = DesignColor.mut.nsColor
        chevron.imageScaling = .scaleProportionallyDown
        for view in [chevron, glyph, name, count] as [NSView] { addSubview(view) }
    }

    required init?(coder: NSCoder) { nil }

    func configure(name text: String, count value: String, glyph shape: StatusGlyph, tint: NSColor, expanded: Bool) {
        name.stringValue = text
        count.stringValue = value
        glyph.glyph = shape
        glyph.tint = tint
        let symbol = NSImage(systemSymbolName: expanded ? "chevron.down" : "chevron.right", accessibilityDescription: nil)
        chevron.image = symbol?.withSymbolConfiguration(.init(pointSize: 9, weight: .semibold))
        needsLayout = true
    }

    override func layout() {
        super.layout()
        let height = bounds.height
        let gap = DesignSpacing.gap8
        var x: CGFloat = 0
        chevron.frame = NSRect(x: x, y: (height - 12) / 2, width: 12, height: 12)
        x += 12 + gap
        let side = DesignLayout.statusIconBox
        glyph.frame = NSRect(x: x, y: (height - side) / 2, width: side, height: side)
        x += side + gap
        let nameWidth = min(ceil(name.intrinsicContentSize.width), max(0, bounds.width - x - 60))
        let h = ceil(name.intrinsicContentSize.height)
        name.frame = NSRect(x: x, y: (height - h) / 2, width: nameWidth, height: h)
        x += nameWidth + gap
        count.frame = NSRect(x: x, y: (height - h) / 2, width: ceil(count.intrinsicContentSize.width), height: h)
    }
}
