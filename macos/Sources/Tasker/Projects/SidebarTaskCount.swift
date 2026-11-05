import SwiftUI
import TaskerAPI

struct SidebarTaskCount: View {
    @Environment(LiveWorkspace.self) private var live
    @Environment(ProjectSelection.self) private var selection
    @State private var count: Int?

    private struct Scope: Equatable {
        var project: UUID?
        var client: ObjectIdentifier?
    }

    private var scope: Scope {
        Scope(project: selection.current?.id, client: live.client.map(ObjectIdentifier.init))
    }

    var body: some View {
        let scope = scope
        Group {
            if let count {
                Text(String(count))
                    .font(.design(DesignTypography.captionSize))
                    .foregroundStyle(DesignColor.dim.color)
                    .monospacedDigit()
                    .lineLimit(1)
                    .fixedSize()
            }
        }
        .task(id: scope) {
            count = nil
            guard let project = scope.project, let client = live.client else { return }
            await LiveQuery.follow(client, ProjectQuery(id: project)) { data in
                count = data.project?.stats.tasks
            }
        }
    }
}
