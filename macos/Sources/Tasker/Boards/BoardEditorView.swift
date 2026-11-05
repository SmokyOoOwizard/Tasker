import SwiftUI
import TaskerAPI

struct BoardFlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrange(width: proposal.width ?? .infinity, subviews: subviews).size
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let result = arrange(width: bounds.width, subviews: subviews)
        for (index, origin) in result.origins.enumerated() {
            subviews[index].place(at: CGPoint(x: bounds.minX + origin.x, y: bounds.minY + origin.y), proposal: .unspecified)
        }
    }

    private func arrange(width: CGFloat, subviews: Subviews) -> (size: CGSize, origins: [CGPoint]) {
        var origins: [CGPoint] = []
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowHeight: CGFloat = 0
        var maxX: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            origins.append(CGPoint(x: x, y: y))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
            maxX = max(maxX, x - spacing)
        }
        return (CGSize(width: maxX, height: y + rowHeight), origins)
    }
}

struct BoardEditorPage: View {
    @Bindable var model: BoardEditorModel
    let close: () -> Void
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(spacing: 0) {
            header
            DesignDivider()
            switch model.phase {
            case .loading:
                BoardEditorMessage(text: l.string(.boardLoading))
            case .missing:
                BoardEditorMessage(text: l.string(.boardMissing))
            case .failed(let message):
                BoardEditorMessage(text: l.string(.boardLoadFailed, message))
            case .ready:
                form
            }
        }
        .onChange(of: model.draft) { model.clearIssues() }
    }

    private var header: some View {
        HStack(spacing: DesignSpacing.gap10) {
            Text(l.string(model.isNew ? .boardTitleNew : .boardTitleEdit))
                .font(.design(DesignTypography.titleSize, weight: DesignTypography.semiboldWeight))
            lockBadge
            Spacer(minLength: 0)
            Button(l.string(model.isDirty ? .commonCancel : .boardClose), action: close)
                .keyboardShortcut(.cancelAction)
            Button(l.string(model.isNew ? .onboardingCreate : .commonSave)) {
                Task { await model.save() }
            }
            .keyboardShortcut(.defaultAction)
            .disabled(!model.canSave)
        }
        .padding(.horizontal, DesignSpacing.pagePadX)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private var lockBadge: some View {
        if let holder = model.holder {
            HStack(spacing: 4) {
                MockupIconView(icon: .lock, size: 11)
                Text(l.string(.boardHolder, holder))
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.red.color)
        } else if !model.isNew, model.lock == .mine {
            HStack(spacing: 4) {
                MockupIconView(icon: .pen, size: 11)
                Text(l.string(.boardEditingMine))
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.green.color)
        }
    }

    private var form: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                if let holder = model.holder {
                    BoardNotice(text: l.string(.boardLockedBy, holder), icon: .lock)
                }
                ForEach(model.issues.board, id: \.self) { BoardNotice(text: $0, icon: .warn) }
                if model.staleVersion {
                    Button(l.string(.boardReload)) { model.reloadFromServer() }
                }
                field(.boardName) {
                    TextField(l.string(.boardNamePlaceholder), text: $model.draft.name)
                        .textFieldStyle(.roundedBorder)
                }
                field(.boardSets) { sets }
                ForEach(Array(model.draft.columns.enumerated()), id: \.element.id) { index, column in
                    BoardColumnCard(model: model, index: index, column: column)
                }
                Button {
                    model.edit { draft, _ in draft.columns.append(BoardColumnDraft(name: l.string(.boardNewColumn))) }
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .plus, size: 11)
                        Text(l.string(.boardAddColumn))
                    }
                }
                .disabled(model.catalog.sets.isEmpty)
            }
            .disabled(model.holder != nil)
            .frame(maxWidth: 760, alignment: .leading)
            .padding(DesignSpacing.pagePadX)
            .frame(maxWidth: .infinity)
        }
    }

    private func field<Content: View>(_ key: L10nKey, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(l.string(key)).foregroundStyle(DesignColor.mut.color)
            content()
        }
    }

    @ViewBuilder
    private var sets: some View {
        if model.catalog.sets.isEmpty {
            Text(l.string(.boardNoSets)).foregroundStyle(DesignColor.mut.color)
        } else {
            BoardFlowLayout(spacing: DesignSpacing.gap10) {
                ForEach(model.catalog.sets) { set in
                    Toggle(set.name, isOn: Binding(
                        get: { model.draft.setIds.contains(set.id) },
                        set: { _ in model.edit { draft, catalog in BoardEditing.toggleSet(set.id, in: &draft, catalog: catalog) } }))
                        .toggleStyle(.checkbox)
                }
            }
        }
    }
}

private struct BoardEditorMessage: View {
    let text: String

    var body: some View {
        Text(text)
            .foregroundStyle(DesignColor.mut.color)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct BoardNotice: View {
    let text: String
    let icon: MockupIcon

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            MockupIconView(icon: icon, size: 12)
            Text(text).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .foregroundStyle(DesignColor.red.color)
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(DesignColor.red.color.opacity(0.1), in: RoundedRectangle(cornerRadius: DesignRadius.control))
    }
}

private struct BoardColumnCard: View {
    @Bindable var model: BoardEditorModel
    let index: Int
    let column: BoardColumnDraft
    @Environment(\.localizer) private var l

    private var catalog: BoardCatalog { model.catalog }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
            HStack(spacing: DesignSpacing.gap6) {
                TextField(l.string(.boardColumnName), text: Binding(
                    get: { column.name },
                    set: { value in model.edit { draft, _ in if draft.columns.indices.contains(index) { draft.columns[index].name = value } } }))
                    .textFieldStyle(.roundedBorder)
                iconButton(.up, help: .boardColumnMoveUp, enabled: index > 0) {
                    model.edit { draft, _ in BoardEditing.move(column: index, by: -1, in: &draft) }
                }
                iconButton(.down, help: .boardColumnMoveDown, enabled: index < model.draft.columns.count - 1) {
                    model.edit { draft, _ in BoardEditing.move(column: index, by: 1, in: &draft) }
                }
                iconButton(.trash, help: .boardColumnRemove, enabled: true, destructive: true) {
                    model.edit { draft, _ in draft.columns.remove(at: index) }
                }
            }
            ForEach(model.issues.columns[column.id] ?? [], id: \.self) { message in
                Text(message)
                    .foregroundStyle(DesignColor.red.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
            statuses
            drops
            conditions
        }
        .padding(DesignSpacing.gap10 + 2)
        .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
        .overlay(
            RoundedRectangle(cornerRadius: DesignRadius.card)
                .strokeBorder(model.issues.columns[column.id] == nil ? DesignColor.line.color : DesignColor.red.color, lineWidth: DesignLayout.borderWidth))
    }

    private func iconButton(_ icon: MockupIcon, help: L10nKey, enabled: Bool, destructive: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            MockupIconView(icon: icon, size: 12)
                .foregroundStyle(destructive ? DesignColor.red.color : DesignColor.mut.color)
                .frame(width: 24, height: 24)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.35)
        .help(l.string(help))
    }

    private func label(_ key: L10nKey) -> some View {
        Text(l.string(key)).foregroundStyle(DesignColor.mut.color)
    }

    private var statuses: some View {
        VStack(alignment: .leading, spacing: 4) {
            label(.boardColumnStatuses)
            let ids = catalog.statusIds(inSets: model.draft.setIds)
            if ids.isEmpty {
                Text(l.string(.boardNoStatuses)).foregroundStyle(DesignColor.dim.color)
            } else {
                BoardFlowLayout(spacing: DesignSpacing.gap6) {
                    ForEach(ids, id: \.self) { id in
                        if let status = catalog.status(id) {
                            statusChip(status, on: column.statusIds.contains(id))
                        }
                    }
                }
            }
        }
    }

    private func statusChip(_ status: BoardStatusInfo, on: Bool) -> some View {
        Button {
            model.edit { draft, _ in BoardEditing.toggleStatus(status.id, column: index, in: &draft) }
        } label: {
            HStack(spacing: 5) {
                Circle().fill(Color(designHex: status.color)).frame(width: DesignLayout.statusDot, height: DesignLayout.statusDot)
                Text(status.name)
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 3)
            .background(on ? DesignColor.accbg.color : .clear, in: RoundedRectangle(cornerRadius: DesignRadius.pill))
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.pill).strokeBorder(on ? DesignColor.acc.color : DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
            .foregroundStyle(on ? DesignColor.acc.color : DesignColor.mut.color)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    @ViewBuilder
    private var drops: some View {
        let sets = model.draft.setIds.compactMap { catalog.set($0) }
        if !sets.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                label(.boardDrop)
                BoardFlowLayout(spacing: DesignSpacing.gap10) {
                    ForEach(sets) { set in
                        HStack(spacing: 4) {
                            Text(set.name + ":").foregroundStyle(DesignColor.mut.color)
                            Picker(selection: Binding<UUID?>(
                                get: { column.drop[set.id] },
                                set: { value in model.edit { draft, _ in BoardEditing.setDrop(value, set: set.id, column: index, in: &draft) } })) {
                                Text(l.string(.boardDropAuto)).tag(UUID?.none)
                                ForEach(column.statusIds.filter { set.statusIds.contains($0) }, id: \.self) { id in
                                    Text(catalog.status(id)?.name ?? "").tag(UUID?.some(id))
                                }
                            } label: { EmptyView() }
                            .labelsHidden()
                            .fixedSize()
                        }
                    }
                }
            }
        }
    }

    private var conditions: some View {
        VStack(alignment: .leading, spacing: 6) {
            label(.boardConditions)
            ForEach(column.conditions) { condition in
                BoardConditionRow(model: model, column: index, condition: condition)
            }
            if catalog.fields.isEmpty {
                Text(l.string(.boardNoFields)).foregroundStyle(DesignColor.dim.color)
            } else {
                Button {
                    model.edit { draft, catalog in
                        if let value = catalog.newCondition(), draft.columns.indices.contains(index) { draft.columns[index].conditions.append(value) }
                    }
                } label: {
                    HStack(spacing: 4) {
                        MockupIconView(icon: .plus, size: 11)
                        Text(l.string(.boardAddCondition))
                    }
                }
            }
        }
    }
}

private struct BoardConditionRow: View {
    @Bindable var model: BoardEditorModel
    let column: Int
    let condition: BoardCondition
    @Environment(\.localizer) private var l

    private var catalog: BoardCatalog { model.catalog }
    private var field: BoardFieldInfo? { catalog.field(condition.fieldId) }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: DesignSpacing.gap6) {
                Picker(selection: Binding(
                    get: { condition.fieldId },
                    set: { id in
                        guard let target = catalog.field(id) else { return }
                        update { BoardEditing.changeField(&$0, to: target, catalog: catalog) }
                    })) {
                    ForEach(catalog.fields) { Text($0.name).tag($0.id) }
                } label: { EmptyView() }
                .labelsHidden()
                .fixedSize()
                Picker(selection: Binding(
                    get: { condition.op },
                    set: { op in update { BoardEditing.changeOperator(&$0, to: op, field: field, catalog: catalog) } })) {
                    ForEach(field.map { catalog.operators(for: $0) } ?? [condition.op], id: \.self) { Text(operatorTitle($0)).tag($0) }
                } label: { EmptyView() }
                .labelsHidden()
                .fixedSize()
                if condition.takesValue { valueEditor }
                Button {
                    model.edit { draft, _ in draft.columns[column].conditions.removeAll { $0.id == condition.id } }
                } label: {
                    MockupIconView(icon: .x, size: 11)
                        .foregroundStyle(DesignColor.mut.color)
                        .frame(width: 22, height: 22)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(l.string(.boardConditionRemove))
            }
            if let message = model.issues.conditions[condition.id] {
                Text(message).foregroundStyle(DesignColor.red.color)
            }
        }
    }

    @ViewBuilder
    private var valueEditor: some View {
        if let field, field.type == .enum {
            Picker(selection: Binding(
                get: { condition.value.lowercased() },
                set: { value in update { $0.value = value } })) {
                ForEach(field.enumId.flatMap { catalog.enums[$0] } ?? []) { Text($0.name).tag($0.id.uuidString.lowercased()) }
            } label: { EmptyView() }
            .labelsHidden()
            .fixedSize()
        } else if let field, field.type == .bool {
            Picker(selection: Binding(get: { condition.value }, set: { value in update { $0.value = value } })) {
                Text(l.string(.taskDetailYes)).tag("true")
                Text(l.string(.taskDetailNo)).tag("false")
            } label: { EmptyView() }
            .labelsHidden()
            .fixedSize()
        } else {
            TextField(l.string(.boardValuePlaceholder), text: Binding(get: { condition.value }, set: { value in update { $0.value = value } }))
                .textFieldStyle(.roundedBorder)
                .frame(width: 130)
        }
    }

    private func update(_ change: @escaping (inout BoardCondition) -> Void) {
        model.edit { draft, _ in
            guard draft.columns.indices.contains(column), let index = draft.columns[column].conditions.firstIndex(where: { $0.id == condition.id }) else { return }
            change(&draft.columns[column].conditions[index])
        }
    }

    private func operatorTitle(_ op: FieldOperator) -> String {
        switch op {
        case .notEqual: "≠"
        case .greaterOrEqual: "≥"
        case .lessOrEqual: "≤"
        case .set: l.string(.boardOpSet)
        case .unset: l.string(.boardOpUnset)
        case .attached: l.string(.boardOpAttached)
        case .detached: l.string(.boardOpDetached)
        default: BoardEditing.symbol(op)
        }
    }
}
