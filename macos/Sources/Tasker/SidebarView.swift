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
            ProfileButton()
        }
        .padding(.horizontal, DesignSpacing.sidebarPadX)
        .padding(.bottom, DesignSpacing.sidebarPadY)
        .padding(.top, DesignSpacing.sidebarPadY)
        .frame(width: Self.width)
        .frame(maxHeight: .infinity)
        .background(DesignColor.side.color)
        .overlay(alignment: .trailing) {
            DesignDivider(vertical: true)
        }
    }
}

private struct ProjectSwitcher: View {
    @Environment(\.localizer) private var l
    @State private var hovering = false

    var body: some View {
        Menu {
            Button(l.string(.appName)) {}
        } label: {
            HStack(spacing: DesignSpacing.gap8) {
                Text(l.string(.appLogoLetter))
                    .font(.design(DesignTypography.logoSize, weight: DesignTypography.boldWeight))
                    .foregroundStyle(DesignColor.onAcc.color)
                    .frame(width: DesignLayout.logoSize, height: DesignLayout.logoSize)
                    .background(DesignColor.acc.color, in: RoundedRectangle(cornerRadius: DesignRadius.chip))
                Text(l.string(.appName))
                    .font(.designEmphasis(DesignTypography.baseSize))
                Spacer(minLength: 0)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9))
                    .foregroundStyle(DesignColor.mut.color)
            }
            .padding(.horizontal, DesignSpacing.rowPadX)
            .padding(.vertical, DesignLayout.searchPadY)
            .background(RoundedRectangle(cornerRadius: DesignRadius.control).fill(hovering ? DesignColor.hov.color : .clear))
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .onHover { hovering = $0 }
        .help(l.string(.sidebarProject))
        .padding(.top, 38)
        .padding(.bottom, DesignSpacing.gap6)
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
