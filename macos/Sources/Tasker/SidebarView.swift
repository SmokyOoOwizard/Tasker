import SwiftUI

struct SidebarView: View {
    @Binding var selection: AppSection

    static let width: CGFloat = DesignLayout.sidebarWidth

    var body: some View {
        VStack(alignment: .leading, spacing: DesignSpacing.gap2) {
            ProjectSwitcher()
            SearchField()
            ForEach(AppSection.allCases.filter { $0 != .settings && $0 != .admin }) { section in
                SidebarRow(section: section, selection: $selection)
            }
            Spacer(minLength: 0)
            ForEach([AppSection.settings, .admin]) { section in
                SidebarRow(section: section, selection: $selection)
            }
            DesignDivider().padding(.vertical, DesignSpacing.gap6)
            DaemonStatusChip()
            ProfileButton()
            LanguageMenu()
        }
        .padding(.horizontal, DesignSpacing.sidebarPadX)
        .padding(.bottom, DesignSpacing.sidebarPadY)
        .padding(.top, DesignSpacing.gap6)
        .frame(width: Self.width)
        .frame(maxHeight: .infinity)
        .background(DesignColor.side.color)
        .overlay(alignment: .trailing) {
            DesignDivider(vertical: true)
        }
    }
}

private struct SearchField: View {
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        Button {} label: {
            HStack(spacing: DesignSpacing.gap8) {
                MockupIconView(icon: .search)
                Text(l.string(.sidebarSearch))
                    .lineLimit(1)
                Spacer(minLength: 0)
                KeyCap(text: l.string(.sidebarSearchShortcut))
            }
            .foregroundStyle(hovering ? DesignColor.tx.color : DesignColor.mut.color)
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, DesignLayout.searchPadY)
            .background(RoundedRectangle(cornerRadius: DesignRadius.control).fill(hovering ? DesignColor.hov.color : .clear))
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.control).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .padding(.bottom, DesignSpacing.gap8)
    }
}

private struct KeyCap: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.design(DesignTypography.kbdSize, mono: true))
            .foregroundStyle(DesignColor.mut.color)
            .padding(.horizontal, 5)
            .frame(height: 17)
            .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.kbd))
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.kbd).strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
            .overlay(alignment: .bottom) {
                Rectangle().fill(DesignColor.line2.color).frame(height: 2).padding(.horizontal, 2)
            }
    }
}

private struct SidebarRow: View {
    let section: AppSection
    @Binding var selection: AppSection
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        let selected = selection == section
        Button {
            selection = section
        } label: {
            HStack(spacing: DesignSpacing.gap9) {
                MockupIconView(icon: section.icon)
                    .frame(width: DesignLayout.iconColumn)
                Text(l.string(section.titleKey))
                Spacer(minLength: 0)
            }
            .foregroundStyle(selected ? DesignColor.tx.color : DesignColor.mut.color)
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, DesignSpacing.rowPadY)
            .background(
                RoundedRectangle(cornerRadius: DesignRadius.control)
                    .fill(selected ? DesignColor.sel.color : hovering ? DesignColor.hov.color : .clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct ProfileButton: View {
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        Button {} label: {
            HStack(spacing: DesignSpacing.gap8) {
                Image(systemName: "person.crop.circle.fill")
                    .font(.system(size: 18))
                    .foregroundStyle(DesignColor.mut.color)
                Text(l.string(.sidebarProfile))
                Spacer(minLength: 0)
                MockupIconView(icon: .more)
                    .foregroundStyle(DesignColor.mut.color)
            }
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, 4)
            .background(RoundedRectangle(cornerRadius: DesignRadius.control).fill(hovering ? DesignColor.hov.color : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

private struct LanguageMenu: View {
    @AppStorage(AppLanguage.storageKey) private var storedLanguage = AppLanguage.system.rawValue
    @Environment(\.localizer) private var l

    var body: some View {
        let current = AppLanguage(storedValue: storedLanguage)
        Menu {
            ForEach(AppLanguage.allCases) { option in
                Button {
                    storedLanguage = option.rawValue
                } label: {
                    if option == current {
                        Label(l.string(option.labelKey), systemImage: "checkmark")
                    } else {
                        Text(l.string(option.labelKey))
                    }
                }
            }
        } label: {
            HStack(spacing: 4) {
                Text(l.language.rawValue.uppercased())
                Image(systemName: "chevron.down")
                    .font(.system(size: 8))
            }
            .font(.design(DesignTypography.captionSize))
            .foregroundStyle(DesignColor.tx.color)
            .padding(.horizontal, 8)
            .padding(.vertical, 1)
            .background(DesignColor.sel.color, in: RoundedRectangle(cornerRadius: 7))
            .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(DesignColor.line2.color, lineWidth: DesignLayout.borderWidth))
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(l.string(.sidebarLanguage))
        .padding(.horizontal, DesignSpacing.rowPadX)
        .padding(.top, 2)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
