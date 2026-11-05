import SwiftUI
import TaskerAPI

struct ProjectSettingsPage: View {
    let model: SettingsModel
    @Environment(LiveWorkspace.self) private var live
    @Environment(\.localizer) private var l
    @State private var name = ""
    @State private var error: String?
    @State private var renaming = false
    @State private var deleting = false

    var body: some View {
        let project = model.catalog?.project
        SettingsPageFrame(title: .settingsPageProject, lead: project.map { l.string(.settingsProjectCreated, $0.createdAt.formatted(date: .long, time: .omitted)) }) {
            VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                SettingsCard {
                    Text(l.string(.projectName)).font(.designEmphasis(DesignTypography.baseSize))
                    HStack(spacing: DesignSpacing.gap8) {
                        TextField(text: $name) { EmptyView() }
                            .textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 320)
                            .onSubmit(rename)
                        Button(l.string(.settingsProjectRename), action: rename)
                            .disabled(renaming || name.trimmingCharacters(in: .whitespaces) == project?.name)
                    }
                    if let error { Text(error).foregroundStyle(DesignColor.red.color) }
                }
                SettingsCard(tint: DesignColor.red.color.opacity(0.4)) {
                    Text(l.string(.settingsProjectDeleteHeading))
                        .font(.designEmphasis(DesignTypography.baseSize))
                        .foregroundStyle(DesignColor.red.color)
                    Text(l.string(.settingsProjectDeleteHint)).foregroundStyle(DesignColor.mut.color)
                    Button(l.string(.settingsProjectDeleteButton), role: .destructive) { deleting = true }
                }
            }
        }
        .onAppear { name = project?.name ?? "" }
        .onChange(of: project?.name) { _, value in if !renaming { name = value ?? "" } }
        .sheet(isPresented: $deleting) {
            if let project { ProjectDeleteSheet(model: model, project: project) { deleting = false } }
        }
    }

    private func rename() {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !renaming, trimmed != model.catalog?.project.name else { return }
        if trimmed.isEmpty {
            error = l.string(.projectNameEmpty)
            return
        }
        let others = (live.projects ?? []).filter { $0.id != model.project }.map(\.name)
        if others.contains(where: { $0.caseInsensitiveCompare(trimmed) == .orderedSame }) {
            error = l.string(.projectExists)
            return
        }
        error = nil
        renaming = true
        Task {
            if let held = await model.hold(.project) {
                error = held
                renaming = false
                return
            }
            error = await model.renameProject(trimmed)
            await model.drop()
            renaming = false
        }
    }
}

private struct ProjectDeleteSheet: View {
    let model: SettingsModel
    let project: SettingsProjectItem
    let close: () -> Void
    @Environment(\.localizer) private var l
    @State private var typed = ""
    @State private var error: String?
    @State private var busy = false
    @State private var locked = false

    private var matches: Bool { typed == project.name }

    var body: some View {
        SettingsSheet(
            title: l.string(.settingsProjectDeleteTitle, project.name), error: error, saveTitle: l.string(.settingsProjectDeleteForever),
            canSave: matches && !locked, busy: busy, destructive: true, cancel: close, save: submit
        ) {
            Text(l.string(.settingsProjectDeleteType, project.name)).fixedSize(horizontal: false, vertical: true)
            TextField(text: $typed) { EmptyView() }.textFieldStyle(.roundedBorder).onSubmit(submit)
        }
        .task {
            if let held = await model.hold(.project) {
                error = held
                locked = true
            }
        }
        .onDisappear { model.dropSoon() }
    }

    private func submit() {
        guard matches, !busy, !locked else { return }
        busy = true
        Task {
            if let failure = await model.deleteProject() {
                error = failure
                busy = false
            } else {
                close()
            }
        }
    }
}
