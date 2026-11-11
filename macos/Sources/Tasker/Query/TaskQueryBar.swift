import AppKit
import SwiftUI
import TaskerAPI

struct TaskQueryBarField: View {
    let model: TaskQueryModel
    @Environment(\.localizer) private var l
    @State private var height: CGFloat = 26

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: DesignSpacing.gap6) {
                MockupIconView(icon: .search, size: 13)
                    .foregroundStyle(DesignColor.dim.color)
                ZStack(alignment: .leading) {
                    if model.text.isEmpty {
                        Text(l.string(.queryPlaceholder))
                            .font(.design(DesignTypography.monoSize, mono: true))
                            .foregroundStyle(DesignColor.dim.color)
                            .lineLimit(1)
                            .allowsHitTesting(false)
                    }
                    TaskQueryField(model: model, label: l.string(.queryLabel))
                        .frame(height: 18)
                }
                if !model.text.isEmpty {
                    Button {
                        model.clear()
                    } label: {
                        MockupIconView(icon: .x, size: 11)
                            .foregroundStyle(DesignColor.dim.color)
                            .frame(width: 18, height: 18)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(l.string(.queryClear))
                    .accessibilityLabel(l.string(.queryClear))
                }
            }
            .padding(.horizontal, DesignSpacing.gap8)
            .frame(height: 26)
            .background(DesignColor.panel.color)
            .clipShape(RoundedRectangle(cornerRadius: DesignRadius.control))
            .overlay(
                RoundedRectangle(cornerRadius: DesignRadius.control)
                    .strokeBorder(model.error == nil ? DesignColor.line2.color : DesignColor.red.color, lineWidth: DesignLayout.borderWidth)
            )
            if let error = model.error {
                Text(l.refusal(error.refusal))
                    .font(.design(DesignTypography.captionSize))
                    .foregroundStyle(DesignColor.red.color)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(minWidth: 240, maxWidth: .infinity, alignment: .leading)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height = $0 }
        .overlay(alignment: .topLeading) {
            if model.showsHints {
                TaskQueryHintList(model: model)
                    .offset(y: height + 4)
            }
        }
    }
}

private struct TaskQueryHintList: View {
    let model: TaskQueryModel
    @Environment(\.localizer) private var l

    private static let rowHeight: CGFloat = 24
    private static let maxVisible = 9

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(Array(model.hints.enumerated()), id: \.element.id) { index, hint in
                            row(hint, selected: index == model.selected)
                                .id(hint.id)
                                .onHover { inside in if inside { model.select(index) } }
                                .onTapGesture { model.accept(index) }
                        }
                    }
                }
                .frame(height: min(CGFloat(model.hints.count), CGFloat(Self.maxVisible)) * Self.rowHeight)
                .onChange(of: model.selected) {
                    if let selected = model.selected, model.hints.indices.contains(selected) {
                        proxy.scrollTo(model.hints[selected].id)
                    }
                }
            }
            DesignDivider()
            Text(l.string(.queryHint))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.dim.color)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, DesignSpacing.gap8)
                .frame(height: 22)
        }
        .frame(width: 380)
        .background(DesignColor.panel.color)
        .clipShape(RoundedRectangle(cornerRadius: DesignRadius.control))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
        .shadow(color: .black.opacity(0.18), radius: 8, y: 3)
    }

    private func row(_ hint: TaskQueryHint, selected: Bool) -> some View {
        HStack(spacing: DesignSpacing.gap8) {
            Text(hint.label)
                .font(.design(DesignTypography.monoSize, mono: true))
                .foregroundStyle(DesignColor.tx.color)
                .lineLimit(1)
            Spacer(minLength: 0)
            Text(hint.detail ?? kind(hint.kind))
                .font(.design(DesignTypography.captionSize))
                .foregroundStyle(DesignColor.dim.color)
                .lineLimit(1)
        }
        .padding(.horizontal, DesignSpacing.gap8)
        .frame(height: Self.rowHeight)
        .background(selected ? DesignColor.line2.color : Color.clear)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
    }

    private func kind(_ kind: TaskQuerySuggestionKind) -> String {
        switch kind {
        case .property: return l.string(.queryKindProperty)
        case .field: return l.string(.queryKindField)
        case .link: return l.string(.queryKindLink)
        case .function: return l.string(.queryKindFunction)
        case .operator: return l.string(.queryKindOperator)
        case .value: return l.string(.queryKindValue)
        case .keyword: return l.string(.queryKindKeyword)
        }
    }
}

final class TaskQueryScrollView: NSScrollView {
    override func layout() {
        super.layout()
        guard let view = documentView as? NSTextView else { return }
        view.minSize = NSSize(width: contentSize.width, height: view.minSize.height)
        if view.frame.width < contentSize.width {
            view.setFrameSize(NSSize(width: contentSize.width, height: view.frame.height))
        }
    }
}

struct TaskQueryField: NSViewRepresentable {
    let model: TaskQueryModel
    let label: String

    func makeCoordinator() -> Coordinator { Coordinator(model: model) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = TaskQueryScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.borderType = .noBorder

        let view = NSTextView()
        view.drawsBackground = false
        view.isRichText = false
        view.allowsUndo = true
        view.isAutomaticQuoteSubstitutionEnabled = false
        view.isAutomaticDashSubstitutionEnabled = false
        view.isAutomaticTextReplacementEnabled = false
        view.isAutomaticSpellingCorrectionEnabled = false
        view.isContinuousSpellCheckingEnabled = false
        view.isGrammarCheckingEnabled = false
        view.font = Coordinator.font
        view.textColor = DesignColor.tx.nsColor
        view.insertionPointColor = DesignColor.tx.nsColor
        view.typingAttributes = Coordinator.base
        view.textContainerInset = NSSize(width: 0, height: 1)
        view.textContainer?.lineFragmentPadding = 0
        view.textContainer?.widthTracksTextView = false
        view.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        view.isHorizontallyResizable = true
        view.isVerticallyResizable = false
        view.minSize = NSSize(width: 0, height: 18)
        view.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: 18)
        view.autoresizingMask = [.height]
        view.setAccessibilityLabel(label)
        view.setAccessibilityIdentifier("task-query-field")
        view.string = model.text
        view.delegate = context.coordinator
        scroll.documentView = view
        context.coordinator.view = view
        context.coordinator.observe()
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.model = model
        context.coordinator.sync()
    }

    @MainActor
    final class Coordinator: NSObject, NSTextViewDelegate {
        static let font = NSFont.monospacedSystemFont(ofSize: DesignTypography.monoSize, weight: .regular)
        static var base: [NSAttributedString.Key: Any] { [.font: font, .foregroundColor: DesignColor.tx.nsColor] }

        var model: TaskQueryModel
        weak var view: NSTextView?
        private var syncing = false
        private var shownErrors: [TaskQueryIssue] = []

        init(model: TaskQueryModel) {
            self.model = model
        }

        func observe() {
            withObservationTracking {
                _ = model.revision
                _ = model.visibleErrors
            } onChange: { [weak self] in
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        self?.sync()
                        self?.observe()
                    }
                }
            }
        }

        func sync() {
            guard let view, !syncing else { return }
            syncing = true
            defer { syncing = false }
            if view.string != model.text {
                view.string = model.text
                view.setSelectedRange(NSRange(location: min(model.cursor, (model.text as NSString).length), length: 0))
                view.scrollRangeToVisible(view.selectedRange())
                shownErrors = []
                paint(view)
            } else if view.selectedRange().location != model.cursor, view.selectedRange().length == 0, view.window?.firstResponder !== view {
                view.setSelectedRange(NSRange(location: min(model.cursor, (model.text as NSString).length), length: 0))
            }
            if shownErrors != model.visibleErrors {
                shownErrors = model.visibleErrors
                paint(view)
            }
        }

        private func paint(_ view: NSTextView) {
            guard let storage = view.textStorage else { return }
            let full = NSRange(location: 0, length: storage.length)
            storage.beginEditing()
            storage.setAttributes(Self.base, range: full)
            for error in model.visibleErrors {
                let start = min(error.start, storage.length)
                let end = min(max(error.end, start), storage.length)
                let range = start == end
                    ? NSRange(location: max(0, start - 1), length: storage.length == 0 ? 0 : 1)
                    : NSRange(location: start, length: end - start)
                storage.addAttributes([.underlineStyle: NSUnderlineStyle.single.rawValue, .underlineColor: DesignColor.red.nsColor], range: range)
            }
            storage.endEditing()
        }

        func textDidChange(_ notification: Notification) {
            guard let view, !syncing else { return }
            syncing = true
            defer { syncing = false }
            let selection = view.selectedRange()
            shownErrors = []
            paint(view)
            view.setSelectedRange(selection)
            view.typingAttributes = Self.base
            model.edited(view.string, cursor: selection.location)
        }

        func textDidEndEditing(_ notification: Notification) {
            model.close()
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let view, !syncing else { return }
            let selection = view.selectedRange()
            if selection.length == 0 { model.caretMoved(selection.location) }
        }

        func textView(_ textView: NSTextView, shouldChangeTextIn range: NSRange, replacementString text: String?) -> Bool {
            guard let text, text.rangeOfCharacter(from: .newlines) != nil else { return true }
            let flat = text.components(separatedBy: .newlines).joined(separator: " ")
            textView.insertText(flat, replacementRange: range)
            return false
        }

        func textView(_ textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            switch selector {
            case #selector(NSResponder.moveUp(_:)):
                model.move(-1)
                return true
            case #selector(NSResponder.moveDown(_:)):
                model.move(1)
                return true
            case #selector(NSResponder.insertTab(_:)):
                if model.showsHints {
                    model.accept()
                    return true
                }
                return false
            case #selector(NSResponder.insertNewline(_:)):
                model.submit()
                return true
            case #selector(NSResponder.cancelOperation(_:)):
                if model.showsHints {
                    model.close()
                } else {
                    textView.window?.makeFirstResponder(nil)
                }
                return true
            case #selector(NSResponder.complete(_:)):
                model.showHints()
                return true
            default:
                return false
            }
        }
    }
}
