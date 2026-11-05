import SwiftUI
import TaskerAPI

private enum LinkTypeEditTarget: Identifiable {
    case new
    case edit(SettingsLinkTypeItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let item): item.id.uuidString
        }
    }
}

struct LinkTypesSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var editing: LinkTypeEditTarget?
    @State private var refusal: SettingsRefusal?
    @State private var deleting: SettingsLinkTypeItem?

    var body: some View {
        SettingsPageFrame(
            title: .settingsPageLinkTypes, lead: l.string(.settingsLinkTypesLead),
            add: (l.string(.settingsLinkTypeNew), { editing = .new })
        ) {
            VStack(spacing: 0) {
                ForEach(model.catalog?.linkTypes ?? []) { item in
                    SettingsRow {
                        HStack(spacing: 6) {
                            Text(item.name)
                            if item.hierarchical { SettingsChip(text: l.string(.settingsLinkHierarchical), accent: true) }
                            if !item.allowCycles { SettingsChip(text: l.string(.settingsLinkNoCycles)) }
                        }
                    } meta: {
                        Text(item.isDirected ? "\(item.outward) / \(item.inward)" : l.string(.settingsLinkUndirected, item.outward))
                    } actions: {
                        SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(item) }
                        SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { deleting = item }
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            LinkTypeEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
        .confirmationDialog(
            l.string(.settingsLinkTypeDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { item in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(item) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(.settingsLinkTypeDeleteBody, item.name))
        }
    }

    private func confirmDelete(_ item: SettingsLinkTypeItem) {
        Task {
            if case .failure(let error) = await model.deleteLinkType(item) {
                if let detail = SettingsModel.inUseRefusal(error) {
                    refusal = SettingsRefusal(refusal: detail)
                } else {
                    model.onFailure?(model.message(error))
                }
            }
        }
    }
}

private struct LinkTypeEditSheet: View {
    let model: SettingsModel
    let target: LinkTypeEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var outward = ""
    @State private var inward = ""
    @State private var allowCycles = false
    @State private var hierarchical = false
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false

    private var original: SettingsLinkTypeItem? {
        if case .edit(let item) = target { return item }
        return nil
    }

    var body: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsLinkTypeNewTitle : .settingsLinkTypeEditTitle), error: error, canSave: !locked, busy: busy,
            cancel: close, save: save
        ) {
            SettingsLabeled(title: l.string(.settingsLinkName)) { TextField(text: $name) { EmptyView() }.textFieldStyle(.roundedBorder) }
            SettingsLabeled(title: l.string(.settingsLinkOutward)) {
                TextField(l.string(.settingsLinkOutwardHint), text: $outward).textFieldStyle(.roundedBorder)
            }
            SettingsLabeled(title: l.string(.settingsLinkInward)) {
                TextField(l.string(.settingsLinkInwardHint), text: $inward).textFieldStyle(.roundedBorder)
            }
            Toggle(l.string(.settingsLinkAllowCycles), isOn: $allowCycles).disabled(hierarchical)
            Toggle(l.string(.settingsLinkHierarchicalLabel), isOn: $hierarchical)
                .onChange(of: hierarchical) { if hierarchical { allowCycles = false } }
        }
        .task {
            guard let original else { return }
            name = original.name
            outward = original.outward
            inward = original.inward
            allowCycles = original.allowCycles
            hierarchical = original.hierarchical
            if let held = await model.hold(.linkType(original.id)) {
                error = held
                locked = true
            }
        }
        .onDisappear { model.dropSoon() }
    }

    private func save() {
        guard !locked, !busy else { return }
        let trimmedName = name.trimmingCharacters(in: .whitespaces)
        let trimmedOut = outward.trimmingCharacters(in: .whitespaces)
        let trimmedIn = inward.trimmingCharacters(in: .whitespaces)
        if trimmedName.isEmpty || trimmedOut.isEmpty {
            error = l.string(.settingsLinkRequired)
            return
        }
        if hierarchical, trimmedIn.isEmpty || trimmedIn.lowercased() == trimmedOut.lowercased() {
            error = l.string(.settingsLinkHierarchicalSides)
            return
        }
        let others = (model.catalog?.linkTypes ?? []).filter { $0.id != original?.id }.map { $0.name.lowercased() }
        if others.contains(trimmedName.lowercased()) {
            error = l.string(.settingsLinkNameTaken)
            return
        }
        error = nil
        busy = true
        Task {
            let failure: String?
            if let original {
                failure = await model.updateLinkType(original, name: trimmedName, outward: trimmedOut, inward: trimmedIn, allowCycles: allowCycles, hierarchical: hierarchical)
            } else {
                failure = await model.createLinkType(name: trimmedName, outward: trimmedOut, inward: trimmedIn, allowCycles: allowCycles, hierarchical: hierarchical)
            }
            if let failure {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }
}
