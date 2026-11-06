import SwiftUI

struct AdminProfilePage: View {
    let model: AdminModel
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var error: String?
    @FocusState private var focused: Bool

    private var changed: Bool { name.trimmingCharacters(in: .whitespacesAndNewlines) != model.userName }

    var body: some View {
        SettingsPageFrame(title: .adminPageProfile, lead: l.string(.adminProfileLead)) {
            SettingsCard {
                SettingsLabeled(title: l.string(.adminProfileName)) {
                    TextField(text: $name) { EmptyView() }
                        .textFieldStyle(.roundedBorder)
                        .focused($focused)
                        .onSubmit(save)
                }
                Text(l.string(.adminProfileHint, model.systemName))
                    .font(.design(DesignTypography.smallSize))
                    .foregroundStyle(DesignColor.mut.color)
                    .fixedSize(horizontal: false, vertical: true)
                if let error {
                    Text(error).foregroundStyle(DesignColor.red.color)
                }
                HStack {
                    Button(l.string(.adminProfileReset)) { reset() }
                        .disabled(model.configuredName == nil)
                    Spacer()
                    Button(l.string(.commonSave), action: save)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!changed)
                }
            }
        }
        .onAppear { name = model.userName }
        .onChange(of: model.userName) { if !focused { name = model.userName } }
    }

    private func save() {
        guard changed else { return }
        error = model.saveName(name)
        if error == nil { name = model.userName }
    }

    private func reset() {
        error = model.resetName()
        if error == nil { name = model.userName }
    }
}
