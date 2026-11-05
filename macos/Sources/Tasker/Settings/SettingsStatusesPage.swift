import SwiftUI
import TaskerAPI

enum StatusSwatch {
    static let colors = ["#8993a4", "#2684ff", "#7b83f5", "#9b59b6", "#f5a623", "#e8590c", "#36b37e", "#0ca678", "#de350b", "#e64980"]
    static let fallback = "#7b83f5"
}

struct StatusTag: View {
    let name: String
    let color: Color
    let glyph: StatusGlyph

    var body: some View {
        HStack(spacing: 5) {
            GlyphView(glyph: glyph, color: color)
            Text(verbatim: name).lineLimit(1)
        }
        .padding(.horizontal, 7)
        .padding(.vertical, 2)
        .background(color.opacity(DesignTag.backgroundLightMix), in: RoundedRectangle(cornerRadius: DesignRadius.tag))
    }
}

extension StatusIconName {
    var titleKey: L10nKey {
        switch self {
        case .dashed: .settingsStatusIconNameDashed
        case .empty: .settingsStatusIconNameEmpty
        case .quarter: .settingsStatusIconNameQuarter
        case .half: .settingsStatusIconNameHalf
        case .three: .settingsStatusIconNameThree
        case .review: .settingsStatusIconNameReview
        case .pause: .settingsStatusIconNamePause
        case .blocked: .settingsStatusIconNameBlocked
        case .done: .settingsStatusIconNameDone
        case .cancel: .settingsStatusIconNameCancel
        case .flag: .settingsStatusIconNameFlag
        case .star: .settingsStatusIconNameStar
        }
    }
}

private enum StatusEditTarget: Identifiable {
    case new
    case edit(SettingsStatusItem)

    var id: String {
        switch self {
        case .new: "new"
        case .edit(let item): item.id.uuidString
        }
    }
}

struct StatusesSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var editing: StatusEditTarget?
    @State private var refusal: SettingsRefusal?
    @State private var deleting: SettingsStatusItem?

    var body: some View {
        let catalog = model.catalog
        SettingsPageFrame(
            title: .settingsPageStatuses, lead: l.string(.settingsStatusesLead),
            add: (l.string(.settingsStatusNew), { editing = .new })
        ) {
            VStack(spacing: 0) {
                ForEach(catalog?.statuses ?? []) { item in
                    SettingsRow {
                        HStack(spacing: 8) {
                            GlyphView(glyph: catalog?.glyph(of: item) ?? .named(.dashed), color: Color(designHex: item.color), size: 15)
                            Button(item.name) { editing = .edit(item) }.buttonStyle(.plain)
                        }
                    } meta: {
                        if !item.description.isEmpty { Text(item.description).lineLimit(2) }
                    } actions: {
                        Circle().fill(Color(designHex: item.color)).frame(width: 10, height: 10).padding(.horizontal, 6).padding(.top, 7)
                        SettingsIconButton(icon: .pen, help: l.string(.settingsEdit)) { editing = .edit(item) }
                        SettingsIconButton(icon: .trash, help: l.string(.settingsDelete), destructive: true) { deleting = item }
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            StatusEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $refusal) { item in
            SettingsRefusalSheet(refusal: item) { refusal = nil }
        }
        .confirmationDialog(
            l.string(.settingsStatusDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { item in
            Button(l.string(.settingsDelete), role: .destructive) { confirmDelete(item) }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { item in
            Text(l.string(.settingsStatusDeleteBody, item.name))
        }
    }

    private func confirmDelete(_ item: SettingsStatusItem) {
        Task {
            if case .failure(let error) = await model.deleteStatus(item) {
                if let detail = SettingsModel.inUseRefusal(error) {
                    refusal = SettingsRefusal(refusal: detail)
                } else {
                    model.onFailure?(model.message(error))
                }
            }
        }
    }
}

private struct StatusEditSheet: View {
    let model: SettingsModel
    let target: StatusEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var color = StatusSwatch.fallback
    @State private var icon = ""
    @State private var text = ""
    @State private var error: String?
    @State private var locked = false
    @State private var busy = false

    private var original: SettingsStatusItem? {
        if case .edit(let item) = target { return item }
        return nil
    }

    private var tint: Color { Color(designHex: color) }

    private var previewGlyph: StatusGlyph {
        if let named = StatusIconName(rawValue: icon) { return .named(named) }
        guard let original, let catalog = model.catalog else { return .named(.dashed) }
        return catalog.glyph(of: SettingsStatusItem(id: original.id, name: "", color: color, description: "", icon: "", version: ""))
    }

    var body: some View {
        SettingsSheet(
            title: l.string(original == nil ? .settingsStatusNewTitle : .settingsStatusEditTitle), width: 500, error: error, canSave: !locked, busy: busy,
            cancel: close, save: save
        ) {
            SettingsLabeled(title: l.string(.settingsStatusName)) {
                TextField(text: $name) { EmptyView() }.textFieldStyle(.roundedBorder)
            }
            SettingsLabeled(title: l.string(.settingsStatusColor)) {
                HStack(spacing: 6) {
                    ForEach(StatusSwatch.colors, id: \.self) { swatch in
                        Button {
                            color = swatch
                        } label: {
                            Circle().fill(Color(designHex: swatch)).frame(width: 18, height: 18)
                                .overlay(Circle().strokeBorder(DesignColor.tx.color, lineWidth: color.lowercased() == swatch ? 2 : 0).padding(-3))
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(swatch)
                    }
                    ColorPicker("", selection: Binding(get: { tint }, set: { color = Self.hex($0) }), supportsOpacity: false).labelsHidden()
                }
            }
            SettingsLabeled(title: l.string(.settingsStatusIcon)) {
                BoardFlowLayout(spacing: 6) {
                    iconTile(nil)
                    ForEach(StatusIconName.allCases, id: \.self) { iconTile($0) }
                }
            }
            SettingsLabeled(title: l.string(.settingsStatusPreview)) {
                StatusTag(name: name.isEmpty ? l.string(.settingsStatusName) : name, color: tint, glyph: previewGlyph)
            }
            SettingsLabeled(title: l.string(.settingsStatusDescription)) {
                TextEditor(text: $text)
                    .font(.design(DesignTypography.baseSize))
                    .frame(height: 70)
                    .padding(4)
                    .background(DesignColor.bg.color, in: RoundedRectangle(cornerRadius: DesignRadius.card))
                    .overlay(RoundedRectangle(cornerRadius: DesignRadius.card).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
            }
        }
        .task {
            guard let original else { return }
            name = original.name
            color = original.color
            icon = original.icon
            text = original.description
            if let held = await model.hold(.status(original.id)) {
                error = held
                locked = true
            }
        }
        .onDisappear { model.dropSoon() }
    }

    private func iconTile(_ named: StatusIconName?) -> some View {
        let on = (named?.rawValue ?? "") == icon
        return Button {
            icon = named?.rawValue ?? ""
        } label: {
            Group {
                if let named {
                    StatusIconView(name: named, color: tint, size: 18)
                } else {
                    Text(l.string(.settingsStatusIconAuto)).font(.design(DesignTypography.smallSize))
                }
            }
            .frame(minWidth: 30, minHeight: 28)
            .padding(.horizontal, named == nil ? 6 : 0)
            .background(on ? DesignColor.sel.color : DesignColor.hov.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).strokeBorder(on ? DesignColor.acc.color : .clear, lineWidth: 1.5))
        }
        .buttonStyle(.plain)
        .help(named.map { l.string($0.titleKey) } ?? l.string(.settingsStatusIconAutoHelp))
        .accessibilityLabel(named.map { l.string($0.titleKey) } ?? l.string(.settingsStatusIconAuto))
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private static func hex(_ color: Color) -> String {
        let value = NSColor(color).usingColorSpace(.sRGB) ?? .gray
        return String(format: "#%02x%02x%02x", Int((value.redComponent * 255).rounded()), Int((value.greenComponent * 255).rounded()), Int((value.blueComponent * 255).rounded()))
    }

    private func validate() -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return l.string(.settingsStatusNameRequired) }
        let others = (model.catalog?.statuses ?? []).filter { $0.id != original?.id }.map { $0.name.lowercased() }
        if others.contains(trimmed.lowercased()) { return l.string(.settingsStatusNameTaken) }
        if !SettingsRules.validColor(color) { return l.string(.settingsStatusColorInvalid) }
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
            let failure: String?
            if let original {
                failure = await model.updateStatus(original, name: trimmed, color: color.lowercased(), description: text, icon: icon)
            } else {
                failure = await model.createStatus(name: trimmed, color: color.lowercased(), description: text, icon: icon)
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
