import AppKit
import SwiftUI
import TaskerAPI

struct AdminAgentsPage: View {
    let model: AdminModel
    @Environment(\.localizer) private var l
    @State private var editing: AgentEditTarget?
    @State private var deleting: AdminAgent?
    @State private var granting: AdminAgent?
    @State private var failure: String?

    var body: some View {
        SettingsPageFrame(
            title: .adminPageAgents, lead: l.string(.adminAgentsLead),
            add: (l.string(.adminAgentCreate), { editing = .create })
        ) {
            VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                if let mcp = model.mcp { McpCard(mcp: mcp) }
                if let failure { Text(failure).foregroundStyle(DesignColor.red.color) }
                VStack(spacing: 0) {
                    ForEach(model.agents) { agent in
                        SettingsRow {
                            HStack(spacing: 8) {
                                Text(agent.name)
                                if agent.isLocalDefault { SettingsChip(text: l.string(.adminAgentDefault)) }
                                SettingsChip(text: agent.rights.summary(l), accent: agent.rights != .full)
                            }
                        } meta: {
                            Text(verbatim: agent.id.uuidString.lowercased()).font(.design(DesignTypography.smallSize, mono: true))
                        } actions: {
                            if let command = model.mcp?.command(agent: agent.id) {
                                Button(l.string(.adminAgentCopy)) { copy(command) }
                                    .controlSize(.small)
                            }
                            SettingsIconButton(icon: .shield, help: l.string(.adminRightsEdit)) { granting = agent }
                            SettingsIconButton(icon: .pen, help: l.string(.adminAgentRename)) { editing = .rename(agent) }
                            SettingsIconButton(icon: .trash, help: l.string(.adminAgentDelete), destructive: true) { deleting = agent }
                        }
                    }
                    if model.agents.isEmpty {
                        Text(l.string(.adminAgentsEmpty)).foregroundStyle(DesignColor.mut.color).padding(.vertical, 9)
                    }
                }
            }
        }
        .sheet(item: $editing) { target in
            AgentEditSheet(model: model, target: target) { editing = nil }
        }
        .sheet(item: $granting) { agent in
            AgentRightsSheet(
                name: agent.name, initial: agent.rights, scope: l.string(.adminRightsScopeFolder),
                save: { await model.setRights(agent, $0) }, close: { granting = nil })
        }
        .confirmationDialog(
            l.string(.adminAgentDeleteTitle), isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting
        ) { agent in
            Button(l.string(.adminAgentDelete), role: .destructive) {
                Task { failure = await model.deleteAgent(agent) }
            }
            Button(l.string(.commonCancel), role: .cancel) {}
        } message: { agent in
            Text(l.string(.adminAgentDeleteBody, agent.name))
        }
    }

    private func copy(_ text: String) {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(text, forType: .string)
        model.onNotice?(l.string(.adminCommandCopied))
    }
}

enum AgentEditTarget: Identifiable {
    case create
    case rename(AdminAgent)

    var id: String {
        switch self {
        case .create: "create"
        case .rename(let agent): agent.id.uuidString
        }
    }
}

private struct McpCard: View {
    let mcp: AdminMcp
    @Environment(\.localizer) private var l

    var body: some View {
        SettingsCard(tint: mcp.isReady ? nil : DesignColor.amber.color.opacity(0.5)) {
            HStack(spacing: 6) {
                Circle().fill(mcp.isReady ? DesignColor.green.color : DesignColor.amber.color).frame(width: 8, height: 8)
                Text(l.string(statusKey)).font(.designEmphasis(DesignTypography.baseSize))
            }
            if let url = mcp.url {
                Text(verbatim: url).font(.design(DesignTypography.smallSize, mono: true)).foregroundStyle(DesignColor.mut.color).textSelection(.enabled)
            }
            if let workspace = mcp.workspace {
                Text(l.string(.adminMcpFolder, workspace)).foregroundStyle(DesignColor.mut.color)
            }
            if let error = mcp.error {
                Text(error).foregroundStyle(DesignColor.red.color).fixedSize(horizontal: false, vertical: true)
            }
            if let command = mcp.command(agent: nil) {
                Text(l.string(.adminMcpCommand)).foregroundStyle(DesignColor.mut.color)
                Text(verbatim: command)
                    .font(.design(DesignTypography.smallSize, mono: true))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                Button(l.string(.adminAgentCopy)) {
                    let board = NSPasteboard.general
                    board.clearContents()
                    board.setString(command, forType: .string)
                }
                .controlSize(.small)
            }
        }
    }

    private var statusKey: L10nKey {
        switch mcp.daemon {
        case .stopped: .adminMcpStopped
        case .starting: .adminMcpStarting
        case .failed: .adminMcpFailed
        case .running:
            switch mcp.workspaceState {
            case .open: .adminMcpReady
            case .opening: .adminMcpOpening
            case .none: .adminMcpFolderMissing
            default: .adminMcpFolderFailed
            }
        }
    }
}

private struct AgentEditSheet: View {
    let model: AdminModel
    let target: AgentEditTarget
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var error: String?
    @State private var busy = false

    private var original: AdminAgent? {
        if case .rename(let agent) = target { return agent }
        return nil
    }

    private var trimmed: String { name.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        SettingsSheet(
            title: l.string(original == nil ? .adminAgentCreate : .adminAgentRename), error: error,
            canSave: !trimmed.isEmpty && trimmed != original?.name, busy: busy, cancel: close, save: save
        ) {
            SettingsLabeled(title: l.string(.adminAgentName)) {
                TextField(text: $name) { EmptyView() }.textFieldStyle(.roundedBorder).onSubmit(save)
            }
        }
        .task { name = original?.name ?? "" }
    }

    private func save() {
        guard !trimmed.isEmpty, !busy else { return }
        busy = true
        Task {
            let failure: String?
            if let original {
                failure = await model.renameAgent(original, to: trimmed)
            } else {
                failure = await model.createAgent(trimmed)
            }
            busy = false
            if let failure { error = failure } else { close() }
        }
    }
}
