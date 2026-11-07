import SwiftUI
import TaskerAPI

struct TaskCommentsSection: View {
    let comments: TaskCommentsModel
    let drafts: TaskDrafts?
    let taskId: UUID
    let prefixes: Set<String>
    let compact: Bool
    let open: (String) -> Void
    @Environment(\.localizer) private var l
    @FocusState private var composerFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            if let focus = comments.focusItem {
                focusBar(focus)
            }
            if comments.loaded, comments.items.isEmpty {
                Text(l.string(.taskDetailCommentsEmpty))
                    .foregroundStyle(DesignColor.mut.color)
                    .padding(.top, 4)
                    .padding(.bottom, 12)
            }
            ForEach(comments.rows(maxLevel: compact ? TaskCommentsModel.compactLevels : TaskCommentsModel.fullLevels)) { row in
                rowView(row)
            }
            newComment.padding(.top, 14)
        }
        .confirmationDialog(
            l.string(.taskDetailCommentDeleteTitle),
            isPresented: Binding(get: { comments.deleting != nil }, set: { if !$0 { comments.deleting = nil } }),
            titleVisibility: .visible,
            presenting: comments.deleting
        ) { item in
            Button(l.string(.taskDetailCommentDelete), role: .destructive) {
                Task { await comments.delete(item) }
            }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(comments.hasReplies(item.id) ? .taskDetailCommentDeleteKeeps : .taskDetailCommentDeleteGone))
        }
    }

    private var header: some View {
        HStack(spacing: DesignSpacing.gap8) {
            Text(l.string(.taskDetailComments).uppercased(with: l.locale))
                .font(.design(DesignTypography.smallSize))
                .tracking(0.6)
                .foregroundStyle(DesignColor.mut.color)
            if comments.count > 0 {
                Text(verbatim: String(comments.count))
                    .font(.design(DesignTypography.smallSize))
                    .monospacedDigit()
                    .foregroundStyle(DesignColor.mut.color)
            }
            Spacer(minLength: 0)
            GhostButton(title: l.string(.taskDetailCommentAdd), icon: .plus) { composerFocused = true }
        }
        .padding(.top, 26)
        .padding(.bottom, DesignSpacing.gap8)
    }

    private func focusBar(_ focus: TaskCommentItem) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Button(l.string(.taskDetailCommentWholeBranch)) { comments.focus = nil }
                .buttonStyle(.plain)
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.acc.color)
            Text(l.string(.taskDetailCommentFocusTrail, focus.author?.name ?? l.string(.taskDetailCommentDeleted)))
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.mut.color)
        }
        .padding(.horizontal, DesignSpacing.gap10)
        .padding(.vertical, DesignSpacing.gap8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(DesignColor.hov.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
        .padding(.bottom, DesignSpacing.gap6)
    }

    private func rowView(_ row: TaskCommentRow) -> some View {
        HStack(alignment: .top, spacing: 0) {
            ForEach(0..<row.level, id: \.self) { _ in
                CommentGuide()
            }
            switch row.kind {
            case .comment(let item, let replies, let last, let collapsed, let hasLine):
                TaskCommentRowView(
                    comments: comments, item: item, replies: replies, lastAuthor: last, collapsed: collapsed, hasLine: hasLine,
                    drafts: drafts, taskId: taskId, prefixes: prefixes, open: open)
            case .composer(let item):
                TaskCommentReply(comments: comments, item: item, slot: slot("comment.reply.\(item.id.uuidString)"))
                    .padding(.leading, DesignLayout.twistyWidth)
                    .padding(.trailing, DesignSpacing.gap8)
                    .padding(.top, 4)
                    .padding(.bottom, DesignSpacing.gap8)
            case .continuation(let item, let replies):
                Button {
                    comments.openBranch(item.id)
                } label: {
                    Text(l.string(.taskDetailCommentContinue, replies))
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.acc.color)
                        .padding(.horizontal, DesignSpacing.gap10)
                        .frame(height: 26)
                        .background(DesignColor.accbg.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
                }
                .buttonStyle(.plain)
                .padding(.leading, DesignLayout.twistyWidth)
                .padding(.top, 2)
                .padding(.bottom, DesignSpacing.gap8)
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var newComment: some View {
        HStack(alignment: .top, spacing: DesignSpacing.gap8) {
            CommentAvatar(author: nil, mine: true)
                .padding(.top, DesignSpacing.gap8)
            CommentEditor(
                placeholder: l.string(.taskDetailCommentPlaceholder), hint: l.string(.taskDetailCommentHintSend),
                confirm: l.string(.taskDetailCommentSend), accent: false, slot: slot("comment.new"), initial: "",
                externalFocus: $composerFocused,
                cancel: nil
            ) { text in
                await comments.add(parent: comments.focus, body: text)
            }
        }
    }

    private func slot(_ name: String) -> TaskDraftSlot? {
        drafts.map { TaskDraftSlot(drafts: $0, task: taskId, slot: name) }
    }
}

struct CommentGuide: View {
    var body: some View {
        Color.clear
            .frame(width: DesignLayout.treeIndent)
            .overlay(alignment: .leading) {
                Rectangle()
                    .fill(DesignColor.line2.color)
                    .frame(width: DesignLayout.borderWidth)
                    .padding(.leading, 7)
            }
    }
}

struct CommentAvatar: View {
    let author: TaskCommentAuthor?
    let mine: Bool

    private static let size: CGFloat = 22

    private var initials: String {
        String((author?.name ?? "").prefix(2)).uppercased()
    }

    var body: some View {
        if author?.kind == .agent {
            AgentGlyph()
                .frame(width: 14, height: 14)
                .foregroundStyle(agentForeground)
                .frame(width: Self.size, height: Self.size)
                .background(agentBackground, in: RoundedRectangle(cornerRadius: DesignRadius.kbd))
                .accessibilityHidden(true)
        } else if let author {
            Text(verbatim: initials)
                .font(.design(10, weight: DesignTypography.semiboldWeight))
                .foregroundStyle(mine ? DesignColor.acc.color : DesignColor.tx.color)
                .frame(width: Self.size, height: Self.size)
                .background(mine ? DesignColor.accbg.color : DesignColor.line2.color, in: Circle())
                .help(author.name)
        } else if mine {
            Image(systemName: "person.fill")
                .font(.system(size: 10))
                .foregroundStyle(DesignColor.acc.color)
                .frame(width: Self.size, height: Self.size)
                .background(DesignColor.accbg.color, in: Circle())
                .accessibilityHidden(true)
        } else {
            Circle()
                .strokeBorder(DesignColor.line2.color, style: StrokeStyle(lineWidth: DesignLayout.borderWidth, dash: [3, 3]))
                .frame(width: Self.size, height: Self.size)
        }
    }

    @Environment(\.colorScheme) private var scheme

    private var agentBackground: Color {
        Color(nsColor: NSColor.designMix(agentHex, scheme == .dark ? DesignTag.backgroundDarkMix : DesignTag.backgroundLightMix, with: scheme == .dark ? DesignTag.backgroundDarkBase : DesignTag.backgroundLightBase))
    }

    private var agentForeground: Color {
        Color(nsColor: NSColor.designMix(agentHex, scheme == .dark ? DesignTag.textDarkMix : DesignTag.textLightMix, with: scheme == .dark ? DesignTag.textDarkBase : DesignTag.textLightBase))
    }

    private var agentHex: String { DesignTag.palette[4] }
}

struct AgentGlyph: View {
    var body: some View {
        Canvas { context, size in
            let k = size.width / 14
            let frame = Path(roundedRect: CGRect(x: 2 * k, y: 4.5 * k, width: 10 * k, height: 7.5 * k), cornerRadius: 2 * k)
            var antenna = Path()
            antenna.move(to: CGPoint(x: 7 * k, y: 4.5 * k))
            antenna.addLine(to: CGPoint(x: 7 * k, y: 2 * k))
            context.stroke(frame, with: .foreground, lineWidth: 1.3 * k)
            context.stroke(antenna, with: .foreground, style: StrokeStyle(lineWidth: 1.3 * k, lineCap: .round))
            context.fill(Path(ellipseIn: CGRect(x: 4 * k, y: 7 * k, width: 2 * k, height: 2 * k)), with: .foreground)
            context.fill(Path(ellipseIn: CGRect(x: 8 * k, y: 7 * k, width: 2 * k, height: 2 * k)), with: .foreground)
        }
    }
}

struct TaskCommentRowView: View {
    let comments: TaskCommentsModel
    let item: TaskCommentItem
    let replies: Int
    let lastAuthor: String?
    let collapsed: Bool
    let hasLine: Bool
    let drafts: TaskDrafts?
    let taskId: UUID
    let prefixes: Set<String>
    let open: (String) -> Void
    @Environment(\.localizer) private var l
    @State private var hover = false

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            twisty
            CommentAvatar(author: item.deleted ? nil : item.author, mine: item.mine)
                .padding(.top, 7)
                .padding(.trailing, DesignSpacing.gap8)
            content
                .padding(.vertical, 7)
                .padding(.trailing, DesignSpacing.gap8)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(hover ? DesignColor.hov.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.control))
        .onHover { hover = $0 }
    }

    private var twisty: some View {
        ZStack(alignment: .top) {
            Color.clear.frame(width: DesignLayout.twistyWidth)
            if hasLine {
                Rectangle()
                    .fill(DesignColor.line2.color)
                    .frame(width: DesignLayout.borderWidth)
                    .padding(.top, 30)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.leading, 7)
            }
            if replies > 0 {
                Button {
                    comments.toggle(item.id)
                } label: {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 9, weight: .semibold))
                        .rotationEffect(.degrees(collapsed ? 0 : 90))
                        .foregroundStyle(DesignColor.mut.color)
                        .frame(width: DesignLayout.twistyWidth, height: DesignLayout.twistyWidth)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(l.string(collapsed ? .taskDetailCommentExpand : .taskDetailCommentCollapse))
                .accessibilityLabel(l.string(collapsed ? .taskDetailCommentExpand : .taskDetailCommentCollapse))
                .padding(.top, 10)
            }
        }
        .frame(width: DesignLayout.twistyWidth)
    }

    @ViewBuilder
    private var content: some View {
        VStack(alignment: .leading, spacing: 2) {
            if item.deleted {
                Text(l.string(.taskDetailCommentDeleted))
                    .italic()
                    .foregroundStyle(DesignColor.mut.color)
                    .frame(minHeight: 22, alignment: .leading)
            } else {
                headerLine
                if comments.editing == item.id {
                    editor
                } else {
                    MarkdownView(text: item.body, prefixes: prefixes, open: open)
                        .textSelection(.enabled)
                }
            }
            if collapsed {
                Button {
                    comments.toggle(item.id)
                } label: {
                    Text(collapsedText)
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.mut.color)
                        .padding(.horizontal, DesignSpacing.gap8)
                        .frame(height: 22)
                        .background(DesignColor.hov.color, in: RoundedRectangle(cornerRadius: DesignRadius.chip))
                        .overlay(RoundedRectangle(cornerRadius: DesignRadius.chip).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
                }
                .buttonStyle(.plain)
                .padding(.top, 2)
            }
            if !item.deleted, comments.editing != item.id {
                actions
            }
        }
    }

    private var collapsedText: String {
        if let lastAuthor {
            return l.string(.taskDetailCommentCollapsedLast, replies, lastAuthor)
        }
        return l.string(.taskDetailCommentCollapsed, replies)
    }

    private var headerLine: some View {
        HStack(spacing: DesignSpacing.gap6) {
            Text(verbatim: item.author?.name ?? "")
                .font(.design(DesignTypography.baseSize, weight: DesignTypography.semiboldWeight))
                .lineLimit(1)
            if item.author?.kind == .agent {
                TagLabel(text: l.string(.taskDetailCommentAgent), hex: DesignTag.palette[4])
            }
            if item.mine {
                Text(l.string(.taskDetailCommentYou))
            }
            Text(verbatim: l.commentTime(item.createdAt))
                .lineLimit(1)
            if item.editedAt != nil {
                Text(l.string(.taskDetailCommentEdited))
                    .help(item.editedAt.map(l.dateTime) ?? "")
            }
        }
        .font(.design(DesignTypography.smallSize))
        .foregroundStyle(DesignColor.mut.color)
        .frame(minHeight: 22)
    }

    private var editor: some View {
        CommentEditor(
            placeholder: "", hint: l.string(.taskDetailCommentHintSave), confirm: l.string(.taskDetailCommentSave), accent: true,
            slot: drafts.map { TaskDraftSlot(drafts: $0, task: taskId, slot: "comment.edit.\(item.id.uuidString)") },
            initial: item.body,
            autoFocus: true,
            cancel: { comments.editing = nil }
        ) { text in
            await comments.update(item, body: text)
        }
        .padding(.top, 4)
    }

    private var actions: some View {
        HStack(spacing: DesignSpacing.gap10) {
            action(l.string(.taskDetailCommentReply), DesignColor.mut.color) { comments.startReply(item.id) }
            if item.mine, hover {
                action(l.string(.taskDetailCommentEdit), DesignColor.mut.color) { comments.startEdit(item.id) }
                action(l.string(.taskDetailCommentDelete), DesignColor.red.color) { comments.deleting = item }
            }
        }
        .frame(height: 22)
    }

    private func action(_ title: String, _ color: Color, _ perform: @escaping () -> Void) -> some View {
        Button(action: perform) {
            Text(title)
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(color)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

struct TaskCommentReply: View {
    let comments: TaskCommentsModel
    let item: TaskCommentItem
    let slot: TaskDraftSlot?
    @Environment(\.localizer) private var l

    var body: some View {
        CommentEditor(
            placeholder: l.string(.taskDetailCommentReplyPlaceholder), hint: l.string(.taskDetailCommentHintSend),
            confirm: l.string(.taskDetailCommentReply), accent: true, slot: slot, initial: "",
            heading: l.string(.taskDetailCommentReplyTo, item.author?.name ?? ""),
            autoFocus: true,
            cancel: { comments.replyTo = nil }
        ) { text in
            await comments.add(parent: item.id, body: text)
        }
    }
}

struct CommentEditor: View {
    let placeholder: String
    let hint: String
    let confirm: String
    let accent: Bool
    let slot: TaskDraftSlot?
    let initial: String
    var heading: String?
    var autoFocus = false
    var externalFocus: FocusState<Bool>.Binding?
    let cancel: (() -> Void)?
    let submit: (String) async -> Bool
    @Environment(\.localizer) private var l
    @State private var text = ""
    @State private var sending = false
    @FocusState private var focused: Bool

    init(
        placeholder: String, hint: String, confirm: String, accent: Bool, slot: TaskDraftSlot?, initial: String, heading: String? = nil,
        autoFocus: Bool = false, externalFocus: FocusState<Bool>.Binding? = nil, cancel: (() -> Void)?, submit: @escaping (String) async -> Bool
    ) {
        self.placeholder = placeholder
        self.hint = hint
        self.confirm = confirm
        self.accent = accent
        self.slot = slot
        self.initial = initial
        self.heading = heading
        self.autoFocus = autoFocus
        self.externalFocus = externalFocus
        self.cancel = cancel
        self.submit = submit
    }

    private var ready: Bool {
        !sending && !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private var isFocused: Bool { focused || (externalFocus?.wrappedValue ?? false) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let heading {
                HStack(spacing: DesignSpacing.gap6) {
                    Text(verbatim: heading)
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.mut.color)
                    Spacer(minLength: 0)
                    if let cancel {
                        Button(action: cancel) {
                            MockupIconView(icon: .x, size: 10)
                                .foregroundStyle(DesignColor.mut.color)
                                .frame(width: 20, height: 20)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(l.string(.taskDetailCommentReplyCancel))
                    }
                }
                .padding(.horizontal, DesignSpacing.gap10)
                .padding(.top, DesignSpacing.gap6)
            }
            ZStack(alignment: .topLeading) {
                if text.isEmpty {
                    Text(verbatim: placeholder)
                        .foregroundStyle(DesignColor.dim.color)
                        .padding(.horizontal, 5)
                        .allowsHitTesting(false)
                }
                editorField
            }
            .font(.design(DesignTypography.baseSize))
            .padding(.horizontal, 5)
            .padding(.vertical, DesignSpacing.gap6)
            DesignDivider()
            HStack(spacing: DesignSpacing.gap8) {
                Text(verbatim: hint)
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.mut.color)
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 0)
                if cancel != nil, heading == nil {
                    Button(l.string(.commonCancel)) { cancel?() }
                        .buttonStyle(.plain)
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.mut.color)
                }
                Button(action: send) {
                    Text(verbatim: confirm)
                        .font(.design(DesignTypography.smallSize, weight: DesignTypography.mediumWeight))
                        .foregroundStyle(ready ? DesignColor.onAcc.color : DesignColor.mut.color)
                        .padding(.horizontal, DesignSpacing.gap10)
                        .frame(height: 24)
                        .background(ready ? DesignColor.acc.color : DesignColor.line2.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
                }
                .buttonStyle(.plain)
                .disabled(!ready)
                .keyboardShortcut(isFocused ? KeyboardShortcut(.return, modifiers: .command) : nil)
            }
            .padding(.horizontal, DesignSpacing.gap8)
            .padding(.vertical, DesignSpacing.gap6)
        }
        .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).strokeBorder(accent || isFocused ? DesignColor.acc.color : DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
        .frame(maxWidth: .infinity)
        .onAppear {
            text = slot?.text ?? initial
            if autoFocus {
                DispatchQueue.main.async { focused = true }
            }
        }
        .onChange(of: text) { slot?.set(text == initial || text.isEmpty ? nil : text) }
    }

    @ViewBuilder
    private var editorField: some View {
        let field = TextEditor(text: $text)
            .scrollContentBackground(.hidden)
            .frame(minHeight: 54, maxHeight: 240)
            .fixedSize(horizontal: false, vertical: true)
            .onExitCommand { cancel?() }
        if let externalFocus {
            field.focused(externalFocus)
        } else {
            field.focused($focused)
        }
    }

    private func send() {
        guard ready else { return }
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        sending = true
        Task {
            let done = await submit(value)
            sending = false
            if done {
                text = ""
                slot?.set(nil)
            }
        }
    }
}
