import SwiftUI

@MainActor
@Observable
final class ShortcutsPresenter {
    var window: UUID?

    func show(in window: UUID) {
        self.window = window
    }

    func hide() {
        window = nil
    }
}

struct ShortcutsDialog: View {
    let close: () -> Void
    @Environment(\.localizer) private var l
    @Environment(\.colorScheme) private var scheme
    @FocusState private var focused: Bool

    private var rows: [ShortcutTable.Row] {
        ShortcutTable.groups.flatMap(\.rows)
    }

    var body: some View {
        ZStack {
            DesignColor.ovl.color
                .ignoresSafeArea()
                .contentShape(Rectangle())
                .onTapGesture(perform: close)
            VStack(alignment: .leading, spacing: 0) {
                Text(l.string(.shortcutsTitle))
                    .font(.design(15, weight: DesignTypography.semiboldWeight))
                    .padding(.horizontal, 20)
                    .padding(.top, 16)
                    .padding(.bottom, 4)
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                            HStack(spacing: DesignSpacing.gap8) {
                                Text(l.string(row.title))
                                Spacer(minLength: DesignSpacing.gap10)
                                HStack(spacing: 4) {
                                    ForEach(Array(row.keys.enumerated()), id: \.offset) { _, key in
                                        KeyCap(text: key)
                                    }
                                }
                            }
                            .padding(.vertical, 4)
                            .overlay(alignment: .bottom) { DesignDivider() }
                        }
                    }
                    .padding(.horizontal, 20)
                    .padding(.vertical, 10)
                }
                HStack {
                    Spacer(minLength: 0)
                    Button(action: close) {
                        Text(l.string(.shortcutsClose))
                            .foregroundStyle(DesignColor.onAcc.color)
                            .padding(.horizontal, 14)
                            .frame(height: 28)
                            .background(DesignColor.acc.color, in: RoundedRectangle(cornerRadius: DesignRadius.control))
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .keyboardShortcut(.defaultAction)
                }
                .padding(.horizontal, 20)
                .padding(.top, 12)
                .padding(.bottom, 16)
            }
            .font(.design(DesignTypography.baseSize))
            .foregroundStyle(DesignColor.tx.color)
            .frame(width: 640)
            .frame(maxHeight: 560)
            .fixedSize(horizontal: false, vertical: true)
            .background(DesignColor.panel.color, in: RoundedRectangle(cornerRadius: DesignRadius.dialog))
            .overlay(RoundedRectangle(cornerRadius: DesignRadius.dialog).strokeBorder(DesignColor.line.color, lineWidth: DesignLayout.borderWidth))
            .designShadow(DesignShadow.popup, scheme: scheme)
        }
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(.escape) {
            close()
            return .handled
        }
        .onAppear { focused = true }
    }
}
