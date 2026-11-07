import SwiftUI
import TaskerAPI

struct BoardWorkArea: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    let navigator: TaskNavigator

    var body: some View {
        HStack(spacing: 0) {
            ZStack {
                BoardScreen(client: client, project: project, navigator: navigator)
                    .opacity(navigator.card == nil ? 1 : 0)
                    .allowsHitTesting(navigator.card == nil)
                    .accessibilityHidden(navigator.card != nil)
                if let card = navigator.card {
                    TaskCardScreen(client: client, project: project.id, task: card, navigator: navigator)
                }
            }
            if navigator.card == nil, let peek = navigator.peek {
                TaskPeekPanel(client: client, project: project.id, task: peek, navigator: navigator)
                    .transition(.move(edge: .trailing))
            }
        }
        .animation(.easeInOut(duration: 0.18), value: navigator.peek)
    }
}

struct BoardScreen: View {
    let client: TaskerAPIClient
    let project: ProjectFields
    let navigator: TaskNavigator
    @Environment(LiveWorkspace.self) private var live
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l
    @State private var model: BoardViewModel?
    @State private var owner = UUID()
    @State private var outsideShown = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            if let model {
                BoardQueryBar(model: model) { outsideShown = true }
                ZStack {
                    BoardCanvas(model: model, navigator: navigator, focus: { focused = true })
                    BoardStateOverlay(model: model)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onKeyPress(.escape) {
            guard navigator.peek != nil else { return .ignored }
            navigator.closePeek()
            return .handled
        }
        .task(id: session.boardId) {
            model = session.attachBoard(owner: owner, project: project.id, client: client, localizer: l)
        }
        .onChange(of: live.revision) { model?.scheduleRefresh() }
        .onChange(of: l.language) { model?.localizer = l }
        .onChange(of: navigator.card) { _, card in
            if card == nil { Task { @MainActor in focused = true } }
        }
        .onAppear { focused = true }
        .onDisappear { session.detachBoard(owner: owner) }
        .sheet(isPresented: $outsideShown) {
            if let model { BoardOutsideSheet(model: model) }
        }
    }
}

private struct BoardStateOverlay: View {
    let model: BoardViewModel
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(\.localizer) private var l

    var body: some View {
        switch model.phase {
        case .loading:
            LiveMessageView(text: l.string(.boardLoading))
                .background(DesignColor.bg.color)
        case .failed(let message):
            VStack(spacing: DesignSpacing.gap10) {
                Text(l.string(.boardLoadFailed, message))
                    .foregroundStyle(DesignColor.mut.color)
                    .multilineTextAlignment(.center)
                Button(l.string(.taskListRetry)) { model.reload() }
            }
            .padding(DesignSpacing.pagePadX)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(DesignColor.bg.color)
        case .missing:
            LiveMessageView(text: l.string(.boardMissing))
                .background(DesignColor.bg.color)
        case .empty:
            VStack(spacing: DesignSpacing.gap10) {
                MockupIconView(icon: .board, size: 32)
                    .foregroundStyle(DesignColor.dim.color)
                Text(l.string(.boardNoBoards))
                    .foregroundStyle(DesignColor.mut.color)
                GhostButton(title: l.string(.paletteNewBoard), icon: .plus) {
                    session.openBoardEditor(project: model.project, board: nil, localizer: l)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(DesignColor.bg.color)
        case .ready:
            EmptyView()
        }
    }
}

struct BoardQueryBar<Query: View>: View {
    let model: BoardViewModel
    let showOutside: () -> Void
    @ViewBuilder var query: () -> Query
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(spacing: 0) {
            HStack(alignment: .center, spacing: DesignSpacing.gap8) {
                query()
                Spacer(minLength: 0)
                if model.phase == .ready {
                    Text(l.string(.boardCards, l.number(Double(model.shown))))
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.dim.color)
                        .monospacedDigit()
                        .lineLimit(1)
                        .fixedSize()
                }
            }
            .padding(.horizontal, DesignSpacing.pagePadX)
            .padding(.top, DesignSpacing.gap10)
            .padding(.bottom, DesignSpacing.gap8)
            .frame(minHeight: DesignLayout.topBarHeight)
            .overlay(alignment: .bottom) { DesignDivider() }
            if model.outside > 0 {
                HStack(spacing: DesignSpacing.gap6) {
                    MockupIconView(icon: .inbox, size: 13)
                    Text(l.string(.boardOutside, model.outside))
                    Button(l.string(.boardOutsideShow), action: showOutside)
                        .buttonStyle(.plain)
                        .underline()
                        .foregroundStyle(DesignColor.dim.color)
                }
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.mut.color)
                .padding(.horizontal, DesignSpacing.pagePadX)
                .padding(.vertical, 6)
            }
        }
    }
}

extension BoardQueryBar where Query == EmptyView {
    init(model: BoardViewModel, showOutside: @escaping () -> Void) {
        self.init(model: model, showOutside: showOutside) { EmptyView() }
    }
}

private struct BoardCanvas: View {
    let model: BoardViewModel
    let navigator: TaskNavigator
    let focus: () -> Void
    @State private var target: UUID?
    @State private var frames: [UUID: CGRect] = [:]

    var body: some View {
        GeometryReader { geometry in
            canvas(minHeight: max(160, geometry.size.height - 36))
        }
    }

    private func canvas(minHeight: CGFloat) -> some View {
        ScrollView([.horizontal, .vertical]) {
            HStack(alignment: .top, spacing: 14) {
                ForEach(model.columns) { column in
                    BoardColumnView(model: model, navigator: navigator, column: column, minHeight: minHeight, target: $target, focus: focus)
                }
            }
            .padding(.horizontal, DesignSpacing.pagePadX)
            .padding(.top, 6)
            .padding(.bottom, 30)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .coordinateSpace(name: "board")
        .onPreferenceChange(BoardColumnFrames.self) { frames = $0 }
        .background(DesignColor.bg.color)
        .overlay {
            BoardDropOverlay(
                frames: frames,
                target: { id in if target != id { target = id } },
                accepts: { column in model.dragging.map { model.canDrop(task: $0, on: column) } ?? false },
                drop: { column in
                    guard let task = model.dragging else { return false }
                    model.move(task: task, to: column)
                    return true
                })
        }
    }
}

private struct BoardIncomingCard: Identifiable {
    let row: TaskRow

    var id: String { "incoming-" + row.id.uuidString }
}

private struct BoardColumnView: View {
    let model: BoardViewModel
    let navigator: TaskNavigator
    let column: BoardColumnState
    let minHeight: CGFloat
    @Binding var target: UUID?
    let focus: () -> Void
    @Environment(\.localizer) private var l
    @State private var shakes: CGFloat = 0

    private var over: Bool { target == column.id }

    private var incoming: [BoardIncomingCard] {
        model.moving.compactMap { task, destination in
            guard destination == column.id, let place = model.location(of: task), place.column.id != destination else { return nil }
            return BoardIncomingCard(row: place.row)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: DesignSpacing.gap8) {
                Text(column.info.name)
                    .font(.design(DesignTypography.baseSize, weight: .medium))
                    .lineLimit(1)
                Text(verbatim: String(column.total))
                    .font(.design(DesignTypography.baseSize))
                    .foregroundStyle(DesignColor.dim.color)
                    .monospacedDigit()
            }
            .padding(.horizontal, 8)
            .padding(.top, 6)
            .padding(.bottom, 4)
            let chips = conditionChips
            if !chips.isEmpty {
                BoardFlow(spacing: 4) {
                    ForEach(chips, id: \.text) { chip in BoardChip(text: chip.text, accent: chip.accent) }
                }
                .padding(.horizontal, 8)
                .padding(.bottom, 8)
            }
            LazyVStack(spacing: 8) {
                ForEach(incoming) { card in
                    BoardCardBody(model: model, row: card.row, selected: false, pending: true)
                        .transition(.opacity.combined(with: .scale(scale: 0.94)))
                }
                ForEach(column.rows, id: \.id) { row in
                    BoardCardView(model: model, navigator: navigator, row: row, focus: focus)
                        .transition(.opacity.combined(with: .scale(scale: 0.94)))
                }
            }
            if column.hidden > 0 {
                Button {
                    model.showAll(column.id)
                } label: {
                    Text(l.string(.boardShowAll, l.number(Double(column.total))))
                        .font(.design(DesignTypography.smallSize))
                        .foregroundStyle(DesignColor.mut.color)
                        .padding(.horizontal, DesignSpacing.gap8)
                        .frame(height: 24)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .padding(.top, 4)
            }
        }
        .padding(6)
        .frame(width: 290, alignment: .topLeading)
        .frame(minHeight: minHeight, maxHeight: .infinity, alignment: .topLeading)
        .background(over ? DesignColor.accbg.color : .clear, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(over ? DesignColor.acc.color : .clear, lineWidth: 1))
        .animation(.easeOut(duration: 0.15), value: over)
        .modifier(BoardShake(shakes: shakes))
        .background(GeometryReader { proxy in
            Color.clear.preference(key: BoardColumnFrames.self, value: [column.id: proxy.frame(in: .named("board"))])
        })
        .onChange(of: model.refusal) { _, refusal in
            guard refusal?.column == column.id else { return }
            shakes = 0
            withAnimation(.linear(duration: 0.35)) { shakes = 1 }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(column.info.name)
    }

    private struct Chip {
        var text: String
        var accent: Bool
    }

    private var conditionChips: [Chip] {
        var result = column.info.conditions.map { Chip(text: model.conditionText($0), accent: true) }
        if column.info.statusIds.count > 1 || !column.info.conditions.isEmpty {
            let names = column.info.statusIds.map { model.vocabulary.status($0)?.name ?? "" }.joined(separator: " · ")
            result.append(Chip(text: names, accent: false))
        }
        return result
    }
}

private struct BoardChip: View {
    let text: String
    let accent: Bool

    var body: some View {
        Text(text)
            .font(.design(11.5))
            .foregroundStyle(accent ? DesignColor.acc.color : DesignColor.mut.color)
            .lineLimit(1)
            .padding(.horizontal, 6)
            .frame(height: 20)
            .background(accent ? DesignColor.accbg.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.chip))
            .overlay(
                RoundedRectangle(cornerRadius: DesignRadius.chip)
                    .strokeBorder(accent ? DesignColor.acc.color.opacity(0.4) : DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
    }
}

private struct BoardCardView: View {
    let model: BoardViewModel
    let navigator: TaskNavigator
    let row: TaskRow
    let focus: () -> Void
    @Environment(\.colorScheme) private var scheme
    @Environment(\.localizer) private var l
    @Environment(\.displayScale) private var scale

    var body: some View {
        let busy = model.moving[row.id] != nil
        BoardCardBody(model: model, row: row, selected: navigator.peek == row.id, pending: false)
            .opacity(model.dragging == row.id || busy ? 0.3 : 1)
            .overlay {
                BoardCardHandle(
                    task: row.id,
                    draggable: !busy,
                    image: { snapshot() },
                    click: {
                        focus()
                        navigator.togglePeek(row.id)
                    },
                    open: { navigator.open(row.id) },
                    begin: {
                        BoardDragState.active = true
                        model.dragging = row.id
                    },
                    end: {
                        BoardDragState.active = false
                        model.dragging = nil
                    })
            }
            .contextMenu {
                Menu(l.string(.boardMoveTo)) {
                    ForEach(model.columns) { column in
                        Button(column.info.name) { model.move(task: row.id, to: column.id) }
                            .disabled(!model.canDrop(task: row.id, on: column.id))
                    }
                }
            }
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction(named: l.string(.boardMoveTo)) {}
    }

    @MainActor
    private func snapshot() -> NSImage? {
        let renderer = ImageRenderer(content:
            BoardCardBody(model: model, row: row, selected: true, pending: false)
                .frame(width: 278)
                .environment(\.colorScheme, scheme)
                .environment(\.localizer, l))
        renderer.scale = scale
        return renderer.nsImage
    }
}

private struct BoardCardBody: View {
    let model: BoardViewModel
    let row: TaskRow
    let selected: Bool
    let pending: Bool
    @Environment(\.localizer) private var l

    var body: some View {
        let vocabulary = model.vocabulary
        let chips = vocabulary.chips(row, fieldIds: nil, limit: 2, l: l)
        let typeIndex = vocabulary.typeOrder.firstIndex(of: row.typeId) ?? 0
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 7) {
                GlyphView(glyph: vocabulary.glyph(status: row.statusId, type: row.typeId), color: DesignColor.mut.color, size: 13)
                Text(model.reference(row))
                    .font(.design(DesignTypography.monoSize, mono: true))
                    .foregroundStyle(DesignColor.dim.color)
                    .lineLimit(1)
                Spacer(minLength: 0)
                if pending {
                    ProgressView()
                        .controlSize(.mini)
                        .accessibilityLabel(l.string(.boardDropHint))
                }
            }
            Text(row.title)
                .font(.design(DesignTypography.baseSize))
                .foregroundStyle(DesignColor.tx.color)
                .lineSpacing(2)
                .lineLimit(4)
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.top, 6)
                .padding(.bottom, 8)
            BoardFlow(spacing: 6) {
                ForEach(chips, id: \.text) { chip in TagLabel(text: chip.text, hex: chip.colorHex) }
                TagLabel(text: vocabulary.types[row.typeId]?.name ?? "—", hex: DesignTag.palette[(typeIndex + 1) % DesignTag.palette.count])
            }
        }
        .padding(.horizontal, 11)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.panel))
        .overlay(
            RoundedRectangle(cornerRadius: DesignRadius.panel)
                .strokeBorder(selected ? DesignColor.acc.color : DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
        .opacity(pending ? 0.7 : 1)
    }
}

struct BoardTopActions: View {
    @Environment(WorkspaceTabSession.self) private var session
    @Environment(WorkspaceSessions.self) private var sessions
    @Environment(\.localizer) private var l

    var body: some View {
        if session.boardEditor == nil, let project = session.selection.current?.id, let model = session.boardModel, model.phase != .loading {
            HStack(spacing: DesignSpacing.gap8) {
                Menu {
                    ForEach(model.choices) { choice in
                        Toggle(choice.name, isOn: Binding(get: { choice.id == model.board?.id }, set: { _ in session.showBoard(choice.id) }))
                    }
                    Divider()
                    Button(l.string(.paletteNewBoard)) {
                        guard sessions.confirmDiscardingBoardEditor(in: session, replacing: BoardEditorTarget(project: project, board: nil), l) else { return }
                        session.openBoardEditor(project: project, board: nil, localizer: l)
                    }
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .board, size: 12)
                        Text(model.board?.name ?? l.string(.boardPick))
                        MockupIconView(icon: .chevronDown, size: 11)
                    }
                    .foregroundStyle(DesignColor.mut.color)
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .help(l.string(.boardPick))
                if let board = model.board {
                    GhostButton(title: l.string(.boardEdit), icon: .pen) {
                        guard sessions.confirmDiscardingBoardEditor(in: session, replacing: BoardEditorTarget(project: project, board: board.id), l) else { return }
                        session.openBoardEditor(project: project, board: board.id, localizer: l)
                    }
                    .help(l.string(.boardEditHelp) + " ⌥⌘E")
                }
            }
            .font(.design(DesignTypography.smallSize))
        }
    }
}

private struct BoardOutsideSheet: View {
    let model: BoardViewModel
    @Environment(\.dismiss) private var dismiss
    @Environment(\.localizer) private var l
    @State private var rows: [TaskRow]?

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            Text(l.string(.boardOutside, rows?.count ?? model.outside))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            ScrollView {
                LazyVStack(spacing: 0) {
                    if let rows {
                        ForEach(rows, id: \.id) { row in
                            HStack(spacing: DesignSpacing.gap8) {
                                GlyphView(glyph: model.vocabulary.glyph(status: row.statusId, type: row.typeId), color: DesignColor.mut.color)
                                Text(model.reference(row))
                                    .font(.design(DesignTypography.monoSize, mono: true))
                                    .foregroundStyle(DesignColor.dim.color)
                                Text(row.title).lineLimit(1)
                                Spacer(minLength: 0)
                                Text(model.vocabulary.status(row.statusId)?.name ?? "")
                                    .foregroundStyle(DesignColor.dim.color)
                            }
                            .font(.design(DesignTypography.baseSize))
                            .padding(.vertical, 6)
                            .overlay(alignment: .bottom) { DesignDivider() }
                        }
                    } else {
                        ProgressView().controlSize(.small).frame(maxWidth: .infinity).padding()
                    }
                }
            }
            .frame(height: 280)
            Text(l.string(.boardOutsideBody))
                .font(.design(DesignTypography.smallSize))
                .foregroundStyle(DesignColor.mut.color)
            HStack {
                Spacer()
                Button(l.string(.boardClose)) { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(DesignSpacing.pagePadX)
        .frame(width: 520)
        .task { rows = await model.outsideTasks() }
    }
}
