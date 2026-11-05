import SwiftUI
import TaskerAPI

private enum StatusSetEditTarget: Identifiable {
    case new
    case edit(SettingsStatusSetItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let item): item.id.uuidString
        }
    }
}

struct StatusSetsSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var editing: StatusSetEditTarget?
    @State private var refusal: SettingsRefusal?
    @State private var deleting: SettingsStatusSetItem?

    var body: some View {
        let catalog = model.catalog
        SettingsPageFrame(
            title: .settingsPageStatusSets, lead: l.string(.settingsStatusSetsLead),
            add: (l.string(.settingsStatusSetNew), { editing = .new })
        ) {
            VStack(spacing: 0) {
                ForEach(catalog?.statusSets ?? []) { item in
                    SettingsRow {
                        Button(item.name) { editing = .edit(item) }.buttonStyle(.plain)
                    } meta: {
                        BoardFlowLayout(spacing: 10) {
                            ForEach(Array(item.statusIds.enumerated()), id: \.offset) { _, id in
                                if let status = catalog?.status(id) {
                                    HStack(spacing: 4) {
                                        GlyphView(glyph: catalog?.glyph(of: status, in: item) ?? .named(.dashed), color: Color(designHex: status.color), size: 12)
                                        Text(status.name)
                                    }
                                }
                            }
                        }
                    } actions: {
                        SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(item) }
                        SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { deleting = item }
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            StatusSetEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
        .confirmationDialog(
            l.string(.settingsStatusSetDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { item in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(item) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(.settingsStatusSetDeleteBody, item.name))
        }
    }

    private func confirmDelete(_ item: SettingsStatusSetItem) {
        Task {
            if case .failure(let error) = await model.deleteStatusSet(item) {
                if let detail = SettingsModel.inUseRefusal(error) {
                    refusal = SettingsRefusal(refusal: detail)
                } else {
                    model.onNotice?(model.message(error))
                }
            }
        }
    }
}

private struct StatusSetEditSheet: View {
    let model: SettingsModel
    let target: StatusSetEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var ids: [UUID] = []
    @State private var picking = false
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false
    @State private var refusal: SettingsRefusal?

    private var original: SettingsStatusSetItem? {
        if case .edit(let item) = target { return item }
        return nil
    }

    private var catalog: SettingsCatalog? { model.catalog }

    private var asSet: SettingsStatusSetItem {
        SettingsStatusSetItem(id: original?.id ?? UUID(), name: name, statusIds: ids, version: original?.version ?? "")
    }

    var body: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsStatusSetNewTitle : .settingsStatusSetEditTitle), width: 680, error: error, canSave: !locked, busy: busy,
            cancel: close, save: save
        ) {
            TextField(l.string(.settingsStatusSetName), text: $name).textFieldStyle(.roundedBorder)
            SettingsLabeled(title: l.string(.settingsStatusSetPath)) { path }
            SettingsLabeled(title: l.string(.settingsStatusSetOrder)) { list }
            Button {
                picking = true
            } label: {
                HStack(spacing: 4) {
                    MockupIconView(icon: .plus, size: 11)
                    Text(l.string(.settingsStatusSetAdd))
                }
            }
            .popover(isPresented: $picking, arrowEdge: .bottom) { picker }
            if let original {
                let uses = model.statusSetUsage(of: original)
                if !uses.isEmpty {
                    Text(l.string(.settingsStatusSetUsedBy, uses.map { use in
                        switch use.kind {
                        case .type: l.string(.settingsStatusSetUsedType, use.name)
                        default: l.string(.settingsStatusSetUsedBoard, use.name)
                        }
                    }.joined(separator: ", ")))
                    .foregroundStyle(DesignColor.mut.color)
                    .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .task {
            guard let original else { return }
            name = original.name
            ids = original.statusIds
            if let held = await model.hold(.statusSet(original.id)) {
                error = held
                locked = true
            }
        }
        .onDisappear { model.dropSoon() }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
    }

    private var path: some View {
        BoardFlowLayout(spacing: 6) {
            if ids.isEmpty {
                Text(l.string(.settingsStatusSetPathEmpty)).foregroundStyle(DesignColor.dim.color)
            }
            ForEach(Array(ids.enumerated()), id: \.offset) { index, id in
                if index > 0 { Text(verbatim: "→").foregroundStyle(DesignColor.dim.color) }
                if let status = catalog?.status(id) {
                    HStack(spacing: 4) {
                        GlyphView(glyph: catalog?.glyph(of: status, in: asSet) ?? .named(.dashed), color: Color(designHex: status.color), size: 14)
                        Text(status.name)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(DesignSpacing.gap8)
        .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
    }

    private var list: some View {
        List {
            if ids.isEmpty {
                Text(l.string(.settingsStatusSetEmpty)).foregroundStyle(DesignColor.dim.color)
            }
            ForEach(Array(ids.enumerated()), id: \.element) { index, id in
                row(index, id)
            }
            .onMove { source, destination in ids.move(fromOffsets: source, toOffset: destination) }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .frame(height: 240)
        .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
        .overlay(RoundedRectangle(cornerRadius: DesignRadius.card).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
    }

    private func row(_ index: Int, _ id: UUID) -> some View {
        let status = catalog?.status(id)
        return HStack(spacing: 8) {
            Text(verbatim: "⋮⋮").foregroundStyle(DesignColor.dim.color).help(l.string(.settingsStatusSetDrag))
            Text(verbatim: String(index + 1)).font(.design(DesignTypography.smallSize, mono: true)).foregroundStyle(DesignColor.dim.color).frame(width: 22, alignment: .trailing)
            if let status {
                GlyphView(glyph: catalog?.glyph(of: status, in: asSet) ?? .named(.dashed), color: Color(designHex: status.color), size: 15)
                Text(status.name)
                Text(status.description)
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.dim.color)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            SettingsIconButton(icon: .up, help: l.string(.settingsEnumUp)) { ids = SettingsRules.moved(ids, from: index, to: index - 1) }
                .disabled(index == 0)
            SettingsIconButton(icon: .down, help: l.string(.settingsEnumDown)) { ids = SettingsRules.moved(ids, from: index, to: index + 1) }
                .disabled(index == ids.count - 1)
            SettingsIconButton(icon: .x, help: l.string(.settingsStatusSetRemove)) { ids.removeAll { $0 == id } }
        }
    }

    private var picker: some View {
        let statuses = catalog?.statuses ?? []
        let options = statuses.map {
            ChoicePopover.Option(
                id: $0.id.uuidString, name: $0.name, detail: $0.description, glyph: catalog?.glyph(of: $0, in: asSet), tint: Color(designHex: $0.color))
        }
        return ChoicePopover(
            title: l.string(.settingsStatusSetAdd), options: options, selected: ids.map(\.uuidString), placeholder: l.string(.settingsStatusSetSearch)
        ) { chosen in
            let wanted = chosen.compactMap(UUID.init(uuidString:))
            ids = ids.filter(wanted.contains) + wanted.filter { !ids.contains($0) }
        }
    }

    private func validate() -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return l.string(.settingsStatusSetNameRequired) }
        if ids.isEmpty { return l.string(.settingsStatusSetNeedStatus) }
        let others = (catalog?.statusSets ?? []).filter { $0.id != original?.id }.map { $0.name.lowercased() }
        if others.contains(trimmed.lowercased()) { return l.string(.settingsStatusSetNameTaken) }
        return nil
    }

    private func save() {
        guard !locked, !busy else { return }
        if let problem = validate() {
            error = problem
            return
        }
        error = nil
        busy = true
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        Task {
            if let original {
                if let failure = await model.updateStatusSet(original, name: trimmed, statusIds: ids) {
                    if let inUse = failure.inUse {
                        refusal = SettingsRefusal(refusal: inUse)
                    } else {
                        error = failure.message
                    }
                    busy = false
                } else {
                    close()
                }
            } else if let failure = await model.createStatusSet(name: trimmed, statusIds: ids) {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }
}
