import SwiftUI

struct EditLockBadge: View {
    let state: EditLockState
    let holderText: (String) -> String
    let mineText: String

    var body: some View {
        switch state {
        case .other(let name):
            HStack(spacing: 4) {
                MockupIconView(icon: .lock, size: 11)
                Text(holderText(name))
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.red.color)
        case .mine:
            HStack(spacing: 4) {
                MockupIconView(icon: .pen, size: 11)
                Text(mineText)
            }
            .font(.design(DesignTypography.smallSize))
            .foregroundStyle(DesignColor.green.color)
        case .none:
            EmptyView()
        }
    }
}
