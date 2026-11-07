import SwiftUI

enum PaletteMetrics {
    static let width: CGFloat = 680
    static let inputHeight: CGFloat = 48
    static let hintHeight: CGFloat = 28
    static let rowHeight: CGFloat = 34
    static let snippetRowHeight: CGFloat = 52
    static let sectionHeight: CGFloat = 26
    static let listPadding: CGFloat = 6
    static let maxListHeight: CGFloat = 360
    static let emptyHeight: CGFloat = 64

    static func listHeight(_ sections: [PaletteSection]) -> CGFloat {
        let content = sections.reduce(CGFloat(0)) { total, section in
            total + (section.title == nil ? 0 : sectionHeight) + section.entries.reduce(CGFloat(0)) { $0 + ($1.snippet == nil ? rowHeight : snippetRowHeight) }
        }
        guard content > 0 else { return emptyHeight }
        return min(content + listPadding * 2, maxListHeight)
    }

    static func height(_ sections: [PaletteSection]) -> CGFloat {
        inputHeight + hintHeight + 2 * DesignLayout.borderWidth + listHeight(sections)
    }
}

struct PaletteView: View {
    let model: PaletteModel
    let close: () -> Void
    @FocusState private var focused: Bool

    var body: some View {
        let l = model.localizer
        VStack(spacing: 0) {
            HStack(spacing: DesignSpacing.gap10) {
                MockupIconView(icon: .search)
                    .foregroundStyle(DesignColor.mut.color)
                TextField(l.string(.palettePlaceholder), text: Bindable(model).query)
                    .textFieldStyle(.plain)
                    .font(.design(DesignTypography.titleSize))
                    .focused($focused)
                    .onSubmit { model.runSelected() }
                    .onExitCommand(perform: close)
                    .onKeyPress(.upArrow) { model.move(-1); return .handled }
                    .onKeyPress(.downArrow) { model.move(1); return .handled }
                    .onKeyPress(phases: .down) { press in
                        guard press.modifiers.contains(.control) else { return .ignored }
                        switch LatinKeys.character(for: NSApp.currentEvent) ?? press.characters {
                        case "n": model.move(1); return .handled
                        case "p": model.move(-1); return .handled
                        default: return .ignored
                        }
                    }
            }
            .padding(.horizontal, DesignSpacing.pagePadX)
            .frame(height: PaletteMetrics.inputHeight)
            DesignDivider()
            HStack {
                Text(model.searching ? l.string(.paletteSearching) : model.failed ? l.string(.paletteSearchFailed) : l.string(.paletteHint))
                    .foregroundStyle(model.failed ? DesignColor.red.color : DesignColor.dim.color)
                Spacer(minLength: 0)
            }
            .font(.design(DesignTypography.captionSize))
            .padding(.horizontal, DesignSpacing.pagePadX)
            .frame(height: PaletteMetrics.hintHeight)
            DesignDivider()
            PaletteList(model: model)
                .frame(height: PaletteMetrics.listHeight(model.sections))
        }
        .frame(width: PaletteMetrics.width, height: PaletteMetrics.height(model.sections), alignment: .top)
        .background(DesignColor.panel.color)
        .clipShape(RoundedRectangle(cornerRadius: DesignRadius.dialog))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.dialog).strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
        .font(.design(DesignTypography.baseSize))
        .foregroundStyle(DesignColor.tx.color)
        .tint(DesignColor.acc.color)
        .task {
            focused = true
            try? await Task.sleep(for: .milliseconds(80))
            focused = true
        }
    }
}

private struct PaletteList: View {
    let model: PaletteModel

    var body: some View {
        let l = model.localizer
        if model.isEmpty {
            Text(l.string(.paletteEmpty))
                .foregroundStyle(DesignColor.mut.color)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(indexed(model.sections), id: \.section.id) { item in
                            if let title = item.section.title {
                                Text(title)
                                    .font(.design(DesignTypography.captionSize, weight: DesignTypography.semiboldWeight))
                                    .foregroundStyle(DesignColor.dim.color)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, DesignSpacing.pagePadX)
                                    .frame(height: PaletteMetrics.sectionHeight, alignment: .bottom)
                                    .padding(.bottom, 2)
                            }
                            ForEach(Array(item.section.entries.enumerated()), id: \.element.id) { position, entry in
                                PaletteRow(entry: entry, selected: item.start + position == model.selected)
                                    .id(entry.id)
                                    .onHover { inside in if inside { model.select(item.start + position) } }
                                    .onTapGesture { model.run(at: item.start + position) }
                            }
                        }
                    }
                    .padding(.vertical, PaletteMetrics.listPadding)
                }
                .onChange(of: model.selected) {
                    let entries = model.entries
                    if entries.indices.contains(model.selected) {
                        proxy.scrollTo(entries[model.selected].id)
                    }
                }
            }
        }
    }

    private struct Indexed {
        var section: PaletteSection
        var start: Int
    }

    private func indexed(_ sections: [PaletteSection]) -> [Indexed] {
        var start = 0
        return sections.map { section in
            defer { start += section.entries.count }
            return Indexed(section: section, start: start)
        }
    }
}

private struct PaletteRow: View {
    let entry: PaletteEntry
    let selected: Bool

    var body: some View {
        HStack(spacing: DesignSpacing.gap10) {
            icon
                .frame(width: DesignLayout.iconColumn)
                .foregroundStyle(DesignColor.mut.color)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: DesignSpacing.gap10) {
                    if let reference = entry.reference {
                        Text(reference)
                            .font(.design(DesignTypography.monoSize, mono: true))
                            .foregroundStyle(DesignColor.dim.color)
                    }
                    Text(entry.title)
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
                if let snippet = entry.snippet {
                    Text(highlighted(snippet))
                        .font(.design(DesignTypography.captionSize))
                        .foregroundStyle(DesignColor.dim.color)
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
            }
            Spacer(minLength: DesignSpacing.gap8)
            if let detail = entry.detail {
                Text(detail)
                    .font(.design(DesignTypography.captionSize))
                    .foregroundStyle(DesignColor.dim.color)
                    .lineLimit(1)
            }
            if let shortcut = entry.shortcut {
                Text(shortcut)
                    .font(.design(DesignTypography.kbdSize, mono: true))
                    .foregroundStyle(DesignColor.mut.color)
                    .padding(.horizontal, 5)
                    .frame(height: 17)
                    .overlay(RoundedRectangle(cornerRadius: DesignRadius.kbd).strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
            }
        }
        .padding(.horizontal, DesignSpacing.pagePadX)
        .frame(height: entry.snippet == nil ? PaletteMetrics.rowHeight : PaletteMetrics.snippetRowHeight)
        .background(
            RoundedRectangle(cornerRadius: DesignRadius.control)
                .fill(selected ? DesignColor.sel.color : .clear)
                .padding(.horizontal, DesignSpacing.sidebarPadX)
        )
        .opacity(entry.isEnabled ? 1 : 0.45)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private func highlighted(_ snippet: PaletteSnippet) -> AttributedString {
        var result = AttributedString(snippet.text)
        let utf16 = snippet.text.utf16
        for range in snippet.ranges {
            guard let lower = utf16.index(utf16.startIndex, offsetBy: range.lowerBound, limitedBy: utf16.endIndex),
                  let upper = utf16.index(utf16.startIndex, offsetBy: range.upperBound, limitedBy: utf16.endIndex),
                  let start = AttributedString.Index(lower, within: result),
                  let end = AttributedString.Index(upper, within: result) else { continue }
            result[start..<end].foregroundColor = DesignColor.acc.color
        }
        return result
    }

    @ViewBuilder
    private var icon: some View {
        switch entry.icon {
        case .mockup(let icon):
            MockupIconView(icon: icon)
        case .system(let name):
            Image(systemName: name).font(.system(size: 12))
        case .status(let glyph, let color):
            GlyphView(glyph: glyph, color: Color(designHex: color))
        }
    }
}
