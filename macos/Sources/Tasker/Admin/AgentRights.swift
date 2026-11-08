import SwiftUI
import TaskerAPI

struct AgentRightsValue: Hashable, Sendable {
    var editTasks: Bool
    var deleteTasks: Bool
    var configureProject: Bool
    var manageSeries: Bool

    static let full = AgentRightsValue(editTasks: true, deleteTasks: true, configureProject: true, manageSeries: true)
    static let readOnly = AgentRightsValue(editTasks: false, deleteTasks: false, configureProject: false, manageSeries: false)

    var count: Int { [editTasks, deleteTasks, configureProject, manageSeries].filter { $0 }.count }

    var wire: Components.Schemas.AgentRights {
        .init(editTasks: editTasks, deleteTasks: deleteTasks, configureProject: configureProject, manageSeries: manageSeries)
    }

    func summary(_ l: Localizer) -> String {
        switch count {
        case 4: l.string(.adminRightsFull)
        case 0: l.string(.adminRightsReadOnly)
        default: l.string(.adminRightsLimited, String(count))
        }
    }
}

struct AgentRightsEditor: View {
    @Binding var rights: AgentRightsValue
    let scope: String
    @Environment(\.localizer) private var l

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap8) {
            Text(l.string(.adminRightsLead)).foregroundStyle(DesignColor.mut.color).fixedSize(horizontal: false, vertical: true)
            row(.adminRightsEditTasks, .adminRightsEditTasksHint, $rights.editTasks)
            row(.adminRightsDeleteTasks, .adminRightsDeleteTasksHint, $rights.deleteTasks)
            row(.adminRightsConfigureProject, .adminRightsConfigureProjectHint, $rights.configureProject)
            row(.adminRightsManageSeries, .adminRightsManageSeriesHint, $rights.manageSeries)
            HStack(spacing: DesignSpacing.gap8) {
                Button(l.string(.adminRightsAllowAll)) { rights = .full }
                    .controlSize(.small)
                    .disabled(rights == .full)
                Button(l.string(.adminRightsDenyAll)) { rights = .readOnly }
                    .controlSize(.small)
                    .disabled(rights == .readOnly)
            }
            Text(scope).font(.design(DesignTypography.smallSize)).foregroundStyle(DesignColor.mut.color)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func row(_ title: L10nKey, _ hint: L10nKey, _ value: Binding<Bool>) -> some View {
        Toggle(isOn: value) {
            VStack(alignment: .leading, spacing: 2) {
                Text(l.string(title))
                Text(l.string(hint)).font(.design(DesignTypography.smallSize)).foregroundStyle(DesignColor.mut.color)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .toggleStyle(.checkbox)
    }
}

struct AgentRightsSheet: View {
    let name: String
    let initial: AgentRightsValue
    let scope: String
    let save: (AgentRightsValue) async -> String?
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var rights = AgentRightsValue.full
    @State private var error: String?
    @State private var busy = false

    var body: some View {
        SettingsSheet(
            title: l.string(.adminRightsTitle, name), error: error, canSave: rights != initial, busy: busy, cancel: close, save: submit
        ) {
            AgentRightsEditor(rights: $rights, scope: scope)
        }
        .task { rights = initial }
    }

    private func submit() {
        guard !busy else { return }
        busy = true
        Task {
            let failure = await save(rights)
            busy = false
            if let failure { error = failure } else { close() }
        }
    }
}
