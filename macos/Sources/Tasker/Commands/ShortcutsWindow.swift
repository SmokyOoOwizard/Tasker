import SwiftUI

struct ShortcutsWindow: View {
    static let id = "shortcuts"

    @Environment(\.localizer) private var l

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: DesignSpacing.gap10) {
                ForEach(Array(ShortcutTable.groups.enumerated()), id: \.offset) { _, group in
                    Text(l.string(group.title).uppercased(with: l.locale))
                        .font(.design(DesignTypography.smallSize))
                        .tracking(0.6)
                        .foregroundStyle(DesignColor.mut.color)
                        .padding(.top, DesignSpacing.gap10)
                    ForEach(Array(group.rows.enumerated()), id: \.offset) { _, row in
                        HStack(spacing: DesignSpacing.gap8) {
                            Text(l.string(row.title))
                            Spacer(minLength: DesignSpacing.gap10)
                            HStack(spacing: 4) {
                                ForEach(Array(row.keys.enumerated()), id: \.offset) { _, key in
                                    KeyCap(text: key)
                                }
                            }
                        }
                        .frame(minHeight: 24)
                        DesignDivider()
                    }
                }
            }
            .padding(DesignSpacing.pagePadX)
        }
        .font(.design(DesignTypography.baseSize))
        .foregroundStyle(DesignColor.tx.color)
        .background(DesignColor.panel.color)
        .navigationTitle(l.string(.shortcutsTitle))
        .frame(width: 640, height: 560)
    }
}
