import SwiftUI
import TaskerAPI

struct MembersSettingsPage: View {
    let model: SettingsModel
    @Environment(\.localizer) private var l
    @State private var adding = false
    @State private var removing: SettingsMember?
    @State private var granting: SettingsMember?
    @State private var failure: String?

    var body: some View {
        SettingsPageFrame(
            title: .settingsPageMembers, lead: l.string(.settingsMembersLead),
            add: (l.string(.settingsMemberAdd), { adding = true })
        ) {
            VStack(alignment: .leading, spacing: DesignSpacing.gap8) {
                if let failure { Text(failure).foregroundStyle(DesignColor.red.color) }
                VStack(spacing: 0) {
                    ForEach(model.members) { member in
                        SettingsRow {
                            HStack(spacing: 8) {
                                Text(String(member.name.prefix(1)).uppercased())
                                    .font(.design(DesignTypography.smallSize, weight: DesignTypography.semiboldWeight))
                                    .foregroundStyle(DesignColor.onAcc.color)
                                    .frame(width: 20, height: 20)
                                    .background(member.isAgent ? DesignColor.amber.color : DesignColor.acc.color, in: Circle())
                                Text(member.name)
                                if let rights = member.rights {
                                    SettingsChip(text: rights.summary(l), accent: rights != .full)
                                }
                            }
                        } meta: {
                            Text(detail(member))
                        } actions: {
                            if member.isAgent {
                                SettingsIconButton(icon: .shield, help: l.string(.adminRightsEdit)) { granting = member }
                            }
                            SettingsIconButton(icon: .trash, help: l.string(.settingsMemberRemove), destructive: true) { removing = member }
                        }
                    }
                }
            }
        }
        .sheet(isPresented: $adding) {
            MemberAddSheet(model: model) { adding = false }
        }
        .sheet(item: $granting) { member in
            AgentRightsSheet(
                name: member.name, initial: member.rights ?? .full, scope: l.string(.adminRightsScopeProject),
                save: { await model.setMemberRights(member, $0) }, close: { granting = nil })
        }
        .confirmationDialog(
            l.string(.settingsMemberRemoveTitle), isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }), presenting: removing
        ) { member in
            Button(l.string(.settingsMemberRemove), role: .destructive) {
                Task { failure = await model.removeMember(member) }
            }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { member in
            Text(l.string(.settingsMemberRemoveBody, member.name))
        }
    }

    private func detail(_ member: SettingsMember) -> String {
        if member.isAgent {
            let owner = model.members.first { $0.id == member.ownerId }?.name
            return owner.map { l.string(.settingsMemberAgentOf, $0) } ?? l.string(.settingsMemberAgent)
        }
        return (member.email ?? "") + (member.isAdmin ? l.string(.settingsMemberAdmin) : "")
    }
}

private struct MemberAddSheet: View {
    let model: SettingsModel
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var candidates: [SettingsMember]?
    @State private var error: String?
    @State private var busy = false
    @State private var agent: SettingsMember?
    @State private var rights = AgentRightsValue.full

    var body: some View {
        if let agent {
            SettingsSheet(
                title: l.string(.adminRightsTitle, agent.name), error: error, saveTitle: l.string(.settingsMemberAdd), busy: busy,
                cancel: { self.agent = nil; error = nil }, save: { add(agent) }
            ) {
                AgentRightsEditor(rights: $rights, scope: l.string(.adminRightsScopeProject))
            }
        } else {
            list
        }
    }

    private var list: some View {
        SettingsSheet(title: l.string(.settingsMemberAddTitle), error: error, busy: busy, cancelTitle: l.string(.settingsUsedOk), cancel: close) {
            if let candidates {
                if candidates.isEmpty {
                    Text(l.string(.settingsMemberAllIn)).foregroundStyle(DesignColor.mut.color)
                }
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(candidates) { user in
                            Button {
                                if user.isAgent {
                                    rights = .full
                                    agent = user
                                } else {
                                    add(user)
                                }
                            } label: {
                                HStack {
                                    Text(user.name)
                                    Spacer()
                                    Text(user.isAgent ? l.string(.settingsMemberAgent) : (user.email ?? "")).foregroundStyle(DesignColor.mut.color)
                                }
                                .padding(.vertical, 6)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .disabled(busy)
                            .overlay(alignment: .bottom) { DesignDivider() }
                        }
                    }
                }
                .frame(maxHeight: 260)
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .task { candidates = await model.candidates() }
    }

    private func add(_ user: SettingsMember) {
        busy = true
        Task {
            if let failure = await model.addMember(user, rights: user.isAgent ? rights : nil) {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }
}
