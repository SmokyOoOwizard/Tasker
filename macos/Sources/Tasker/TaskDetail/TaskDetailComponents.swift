import SwiftUI

struct GlyphView: View {
    let glyph: StatusGlyph
    let color: Color
    var size: CGFloat = DesignLayout.statusIconBox

    var body: some View {
        Canvas { context, canvasSize in
            let k = min(canvasSize.width, canvasSize.height) / StatusIconName.box
            for primitive in glyph.primitives {
                let path = primitive.shape.path(scale: k)
                let paint = primitive.paint
                let tint = paint.color?.color ?? color
                if let opacity = paint.fillOpacity {
                    context.fill(path, with: .color(tint.opacity(opacity)))
                }
                if let width = paint.strokeWidth {
                    context.stroke(path, with: .color(tint), style: StrokeStyle(lineWidth: width * k, lineCap: paint.cap, lineJoin: paint.join, dash: paint.dash.map { $0 * k }))
                }
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

struct TagLabel: View {
    let text: String
    let hex: String
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let dark = scheme == .dark
        let background = NSColor.designMix(hex, dark ? DesignTag.backgroundDarkMix : DesignTag.backgroundLightMix, with: dark ? DesignTag.backgroundDarkBase : DesignTag.backgroundLightBase)
        let foreground = NSColor.designMix(hex, dark ? DesignTag.textDarkMix : DesignTag.textLightMix, with: dark ? DesignTag.textDarkBase : DesignTag.textLightBase)
        Text(text)
            .font(.design(DesignTypography.captionSize, weight: DesignTypography.mediumWeight))
            .foregroundStyle(Color(nsColor: foreground))
            .padding(.horizontal, DesignLayout.tagPadX)
            .frame(height: DesignLayout.tagHeight)
            .background(Color(nsColor: background), in: RoundedRectangle(cornerRadius: DesignRadius.tag))
            .lineLimit(1)
    }
}

struct SectionHeader<Trailing: View>: View {
    let title: String
    @ViewBuilder var trailing: () -> Trailing
    @Environment(\.localizer) private var l

    var body: some View {
        HStack(spacing: DesignSpacing.gap8) {
            Text(title.uppercased(with: l.locale))
                .font(.design(DesignTypography.smallSize))
                .tracking(0.6)
                .foregroundStyle(DesignColor.mut.color)
            Spacer(minLength: 0)
            trailing()
        }
        .padding(.top, 26)
        .padding(.bottom, DesignSpacing.gap8)
    }
}

struct GhostButton: View {
    let title: String
    var icon: MockupIcon?
    var destructive = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let icon { MockupIconView(icon: icon, size: 11) }
                Text(title)
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(destructive ? DesignColor.red.color : DesignColor.mut.color)
            .padding(.horizontal, DesignSpacing.gap8)
            .frame(height: 24)
            .background(hover ? DesignColor.hov.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.control))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

struct PropertyRow<Value: View>: View {
    let label: Text
    var tappable = true
    @ViewBuilder var value: () -> Value
    @State private var hover = false

    var body: some View {
        HStack(alignment: .center, spacing: DesignSpacing.gap8) {
            label
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.mut.color)
                .frame(width: 84, alignment: .leading)
                .lineLimit(1)
            value()
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, DesignSpacing.rowPadX)
        .padding(.vertical, DesignSpacing.rowPadY)
        .frame(minHeight: 30)
        .background(hover && tappable ? DesignColor.hov.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.control))
        .onHover { hover = $0 }
    }
}

struct PropertyGap: View {
    var body: some View {
        Color.clear.frame(height: DesignSpacing.gap10)
    }
}

struct LinkedTaskRow: View {
    let brief: TaskLinkedBrief
    let vocabulary: TaskListVocabulary
    let open: (UUID) -> Void
    let remove: () -> Void
    @State private var hover = false
    @Environment(\.localizer) private var l

    var body: some View {
        HStack(spacing: DesignSpacing.gap10) {
            let status = vocabulary.status(brief.statusId)
            GlyphView(
                glyph: brief.typeId.map { vocabulary.glyph(status: brief.statusId, type: $0) } ?? vocabulary.glyph(status: brief.statusId),
                color: status.map { Color(designHex: $0.color) } ?? DesignColor.dim.color)
            Text(vocabulary.reference(brief.seriesNumbers, id: brief.id))
                .font(.design(DesignTypography.monoSize, mono: true))
                .foregroundStyle(DesignColor.dim.color)
            Text(brief.title)
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture { open(brief.id) }
            Button(action: remove) {
                MockupIconView(icon: .x, size: 12)
                    .foregroundStyle(DesignColor.dim.color)
                    .frame(width: 20, height: 20)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .opacity(hover ? 1 : 0)
            .help(l.string(.taskDetailRemove))
        }
        .padding(.horizontal, DesignSpacing.rowPadX)
        .padding(.vertical, DesignSpacing.rowPadY)
        .background(hover ? DesignColor.hov.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.control))
        .onHover { hover = $0 }
    }
}

struct ProgressBar: View {
    let fraction: Double

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(DesignColor.line2.color)
                Capsule().fill(DesignColor.green.color).frame(width: proxy.size.width * max(0, min(1, fraction)))
            }
        }
        .frame(height: 4)
    }
}

struct MarkdownView: View {
    let text: String
    let prefixes: Set<String>
    let open: (String) -> Void

    var body: some View {
        let blocks = MarkdownParser.parse(text)
        VStack(alignment: .leading, spacing: DesignSpacing.gap8) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                view(block)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .environment(\.openURL, OpenURLAction { url in
            if let ref = TaskReference.ref(from: url) {
                open(ref)
                return .handled
            }
            return .systemAction
        })
    }

    @ViewBuilder
    private func view(_ block: MarkdownBlock) -> some View {
        switch block {
        case .heading(let level, let text):
            Text(MarkdownParser.inline(text, prefixes: prefixes))
                .font(.design(level == 1 ? DesignTypography.titleSize : DesignTypography.baseSize + CGFloat(max(0, 3 - level)), weight: DesignTypography.semiboldWeight))
                .padding(.top, DesignSpacing.gap6)
        case .paragraph(let text):
            Text(MarkdownParser.inline(text, prefixes: prefixes))
                .lineSpacing(3)
        case .listItem(let marker, let level, let text):
            HStack(alignment: .firstTextBaseline, spacing: DesignSpacing.gap6) {
                Text(marker).foregroundStyle(DesignColor.mut.color)
                Text(MarkdownParser.inline(text, prefixes: prefixes)).lineSpacing(3)
            }
            .padding(.leading, CGFloat(level) * 16 + 4)
        case .quote(let text):
            HStack(spacing: DesignSpacing.gap10) {
                Rectangle().fill(DesignColor.line2.color).frame(width: 3)
                Text(MarkdownParser.inline(text, prefixes: prefixes))
                    .foregroundStyle(DesignColor.mut.color)
                    .lineSpacing(3)
            }
            .fixedSize(horizontal: false, vertical: true)
        case .code(let text):
            Text(verbatim: text)
                .font(.design(DesignTypography.monoSize, mono: true))
                .padding(DesignSpacing.gap10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(DesignColor.hov.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
        case .rule:
            DesignDivider().padding(.vertical, DesignSpacing.gap6)
        case .table(let header, let alignments, let rows):
            tableView(header: header, alignments: alignments, rows: rows)
        }
    }

    private func tableView(header: [String], alignments: [MarkdownColumnAlignment], rows: [[String]]) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            Grid(alignment: .topLeading, horizontalSpacing: 0, verticalSpacing: 0) {
                GridRow {
                    ForEach(Array(header.enumerated()), id: \.offset) { column, cell in
                        tableCell(cell, alignment: alignments[column], bold: true)
                    }
                }
                .background(DesignColor.hov.color)
                ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                    DesignDivider()
                    GridRow {
                        ForEach(Array(row.enumerated()), id: \.offset) { column, cell in
                            tableCell(cell, alignment: alignments[column], bold: false)
                        }
                    }
                }
            }
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).stroke(DesignColor.line2.color, lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: DesignRadius.control))
        }
    }

    private func tableCell(_ text: String, alignment: MarkdownColumnAlignment, bold: Bool) -> some View {
        let horizontal: Alignment = alignment == .center ? .center : (alignment == .trailing ? .trailing : .leading)
        let multiline: TextAlignment = alignment == .center ? .center : (alignment == .trailing ? .trailing : .leading)
        return Text(MarkdownParser.inline(text, prefixes: prefixes))
            .fontWeight(bold ? .semibold : .regular)
            .multilineTextAlignment(multiline)
            .padding(.horizontal, DesignSpacing.gap10)
            .padding(.vertical, DesignSpacing.gap6)
            .frame(maxWidth: .infinity, alignment: horizontal)
    }
}
