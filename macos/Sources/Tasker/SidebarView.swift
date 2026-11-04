import SwiftUI

struct SidebarView: View {
    @Binding var selection: AppSection

    static let width: CGFloat = 232

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            ProjectSwitcher()
            SearchField()
            ForEach(AppSection.allCases.filter { $0 != .settings && $0 != .admin }) { section in
                SidebarRow(section: section, selection: $selection)
            }
            Spacer(minLength: 0)
            ForEach([AppSection.settings, .admin]) { section in
                SidebarRow(section: section, selection: $selection)
            }
            Divider().padding(.vertical, 6)
            ProfileButton()
        }
        .padding(.horizontal, 8)
        .padding(.bottom, 10)
        .padding(.top, 10)
        .frame(width: Self.width)
        .frame(maxHeight: .infinity)
        .background(VisualEffectBackground(material: .sidebar))
        .overlay(alignment: .trailing) {
            Divider()
        }
    }
}

private struct ProjectSwitcher: View {
    var body: some View {
        Menu {
            Button(Strings.appName) {}
        } label: {
            HStack(spacing: 8) {
                Text("T")
                    .font(.system(size: 11, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 20, height: 20)
                    .background(Color.accentColor, in: RoundedRectangle(cornerRadius: 6))
                Text(Strings.appName)
                    .fontWeight(.medium)
                Spacer(minLength: 0)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9))
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .help(Strings.projectMenuTitle)
        .padding(.top, 38)
        .padding(.bottom, 6)
    }
}

private struct SearchField: View {
    var body: some View {
        Button {} label: {
            HStack(spacing: 8) {
                MockupIconView(icon: .search)
                Text(Strings.searchPlaceholder)
                Spacer(minLength: 0)
                Text(Strings.searchShortcut)
                    .font(.system(size: 11, design: .monospaced))
                    .padding(.horizontal, 5)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 5))
            }
            .foregroundStyle(.secondary)
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(.quaternary))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.bottom, 8)
    }
}

private struct SidebarRow: View {
    let section: AppSection
    @Binding var selection: AppSection
    @State private var hovering = false

    var body: some View {
        let selected = selection == section
        Button {
            selection = section
        } label: {
            HStack(spacing: 9) {
                MockupIconView(icon: section.icon)
                    .frame(width: 16)
                Text(section.title)
                Spacer(minLength: 0)
            }
            .foregroundStyle(selected ? .primary : .secondary)
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .background(
                RoundedRectangle(cornerRadius: 7)
                    .fill(selected ? Color.primary.opacity(0.10) : hovering ? Color.primary.opacity(0.05) : .clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct ProfileButton: View {
    var body: some View {
        Button {} label: {
            HStack(spacing: 8) {
                Image(systemName: "person.crop.circle.fill")
                    .font(.system(size: 18))
                    .foregroundStyle(.secondary)
                Text(Strings.profile)
                Spacer(minLength: 0)
                MockupIconView(icon: .more)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
