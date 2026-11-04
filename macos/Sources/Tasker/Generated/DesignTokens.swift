import SwiftUI

enum DesignColor: String, CaseIterable, Sendable {
    case bg
    case side
    case panel
    case hov
    case sel
    case line
    case line2
    case tx
    case mut
    case dim
    case acc
    case accbg
    case red
    case green
    case amber
    case ovl
    case qv
    case qo
    case onAcc

    var assetName: String { "token-" + rawValue }
    var color: Color { Color(assetName) }

    var lightHex: String {
        switch self {
        case .bg: "#faf8f4"
        case .side: "#f3f0ea"
        case .panel: "#ffffff"
        case .hov: "#f1eee7"
        case .sel: "#fff3d1"
        case .line: "#e8e4db"
        case .line2: "#d9d4c8"
        case .tx: "#37352f"
        case .mut: "#6f6b61"
        case .dim: "#9b968c"
        case .acc: "#c2410c"
        case .accbg: "#fdeee3"
        case .red: "#d1432d"
        case .green: "#2f9e6e"
        case .amber: "#b7791f"
        case .ovl: "rgba(55,53,47,0.28)"
        case .qv: "#7c3aed"
        case .qo: "#b45309"
        case .onAcc: "#ffffff"
        }
    }

    var darkHex: String {
        switch self {
        case .bg: "#0f1013"
        case .side: "#0c0d10"
        case .panel: "#14151a"
        case .hov: "#17181e"
        case .sel: "#1c1e26"
        case .line: "#202228"
        case .line2: "#2c2f38"
        case .tx: "#e4e5e9"
        case .mut: "#8b8f99"
        case .dim: "#5d616b"
        case .acc: "#7b83f5"
        case .accbg: "rgba(123,131,245,0.14)"
        case .red: "#e5604d"
        case .green: "#4cb782"
        case .amber: "#e0a23b"
        case .ovl: "rgba(5,6,8,0.55)"
        case .qv: "#c9a6f7"
        case .qo: "#e0a23b"
        case .onAcc: "#ffffff"
        }
    }
}

enum DesignShadow {
    static let popup = DesignShadowSpec(x: 0.0, y: 16.0, blur: 48.0, lightColor: "rgba(60,50,30,0.16)", darkColor: "rgba(0,0,0,0.55)")
}

enum DesignRadius {
    static let xs: CGFloat = 2.0
    static let s: CGFloat = 3.0
    static let kbd: CGFloat = 5.0
    static let chip: CGFloat = 6.0
    static let control: CGFloat = 7.0
    static let card: CGFloat = 8.0
    static let badge: CGFloat = 9.0
    static let panel: CGFloat = 10.0
    static let dialog: CGFloat = 12.0
    static let pill: CGFloat = 999.0
}

enum DesignSpacing {
    static let gap2: CGFloat = 2.0
    static let gap6: CGFloat = 6.0
    static let gap8: CGFloat = 8.0
    static let gap9: CGFloat = 9.0
    static let gap10: CGFloat = 10.0
    static let sidebarPadX: CGFloat = 8.0
    static let sidebarPadY: CGFloat = 10.0
    static let rowPadX: CGFloat = 8.0
    static let rowPadY: CGFloat = 5.0
    static let pagePadX: CGFloat = 20.0
}

enum DesignLayout {
    static let sidebarWidth: CGFloat = 232.0
    static let topBarHeight: CGFloat = 44.0
    static let tabBarHeight: CGFloat = 34.0
    static let taskRowHeight: CGFloat = 38.0
    static let logoSize: CGFloat = 20.0
    static let iconColumn: CGFloat = 16.0
    static let searchPadY: CGFloat = 6.0
    static let borderWidth: CGFloat = 1.0
    static let iconStroke: CGFloat = 1.4
    static let iconBox: CGFloat = 16.0
    static let statusIconBox: CGFloat = 14.0
}

enum DesignTypography {
    static let sansFamily = "-apple-system, BlinkMacSystemFont, \"Inter\", \"Segoe UI\", system-ui, sans-serif"
    static let monoFamily = "ui-monospace, SFMono-Regular, Menlo, monospace"
    static let baseSize: CGFloat = 13.0
    static let smallSize: CGFloat = 12.0
    static let captionSize: CGFloat = 11.0
    static let monoSize: CGFloat = 12.0
    static let kbdSize: CGFloat = 11.0
    static let logoSize: CGFloat = 11.0
    static let titleSize: CGFloat = 17.0
    static let pageTitleSize: CGFloat = 22.0
    static let regularWeight: Font.Weight = .regular
    static let mediumWeight: Font.Weight = .medium
    static let semiboldWeight: Font.Weight = .semibold
    static let boldWeight: Font.Weight = .bold
    static let emphasisWeight: CGFloat = 0.265
    static let lineHeight: CGFloat = 1.45
}

enum StatusIconName: String, CaseIterable, Sendable {
    case dashed
    case empty
    case quarter
    case half
    case three
    case review
    case pause
    case blocked
    case done
    case cancel
    case flag
    case star

    static let box: CGFloat = 14.0

    var primitives: [StatusIconPrimitive] {
        switch self {
        case .dashed:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4, dash: [2.0, 2.1])),
            ]
        case .empty:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
            ]
        case .quarter:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .pie(cx: 7.0, cy: 7.0, r: 5.5, fraction: 0.25), paint: StatusIconPaint(fill: true)),
            ]
        case .half:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .pie(cx: 7.0, cy: 7.0, r: 5.5, fraction: 0.5), paint: StatusIconPaint(fill: true)),
            ]
        case .three:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .pie(cx: 7.0, cy: 7.0, r: 5.5, fraction: 0.75), paint: StatusIconPaint(fill: true)),
            ]
        case .review:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 2.2), paint: StatusIconPaint(fill: true)),
            ]
        case .pause:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .path([.move(5.6, 4.8), .line(5.6, 9.2), .move(8.4, 4.8), .line(8.4, 9.2)]), paint: StatusIconPaint(strokeWidth: 1.4, cap: .round)),
            ]
        case .blocked:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .path([.move(3.3, 10.7), .line(10.7, 3.3)]), paint: StatusIconPaint(strokeWidth: 1.4)),
            ]
        case .done:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 6.2), paint: StatusIconPaint(fill: true)),
                StatusIconPrimitive(shape: .path([.move(4.2, 7.2), .line(6.2, 9.2), .line(9.8, 5.2)]), paint: StatusIconPaint(strokeWidth: 1.5, cap: .round, join: .round, color: .onAcc)),
            ]
        case .cancel:
            [
                StatusIconPrimitive(shape: .circle(cx: 7.0, cy: 7.0, r: 5.5), paint: StatusIconPaint(strokeWidth: 1.4)),
                StatusIconPrimitive(shape: .path([.move(4.9, 4.9), .line(9.1, 9.1), .move(9.1, 4.9), .line(4.9, 9.1)]), paint: StatusIconPaint(strokeWidth: 1.3, cap: .round)),
            ]
        case .flag:
            [
                StatusIconPrimitive(shape: .path([.move(3.5, 12.5), .line(3.5, 2.0), .move(3.5, 2.6), .line(9.8, 2.6), .line(8.4, 5.2), .line(9.8, 7.8), .line(3.5, 7.8)]), paint: StatusIconPaint(fill: 0.25, strokeWidth: 1.3, cap: .round, join: .round)),
            ]
        case .star:
            [
                StatusIconPrimitive(shape: .path([.move(7.0, 1.8), .line(8.6, 5.1), .line(12.2, 5.6), .line(9.6, 8.1), .line(10.2, 11.7), .line(7.0, 10.0), .line(3.8, 11.7), .line(4.4, 8.1), .line(1.8, 5.6), .line(5.4, 5.1), .close]), paint: StatusIconPaint(fill: 0.25, strokeWidth: 1.2, join: .round)),
            ]
        }
    }
}
