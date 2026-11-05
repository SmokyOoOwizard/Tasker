import SwiftUI

enum MockupIcon: Sendable {
    case list
    case board
    case gear
    case shield
    case search
    case more
    case check
    case up
    case down
    case x
    case inbox
    case plus
    case trash
    case back
    case chevronDown
    case warn
    case lock
    case pen
}

struct MockupIconShape: Shape {
    let icon: MockupIcon

    func path(in rect: CGRect) -> Path {
        let k = rect.width / DesignLayout.iconBox
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * k, y: rect.minY + y * k) }
        func line(_ path: inout Path, _ x1: CGFloat, _ y1: CGFloat, _ x2: CGFloat, _ y2: CGFloat) {
            path.move(to: p(x1, y1))
            path.addLine(to: p(x2, y2))
        }
        func box(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> Path {
            Path(roundedRect: CGRect(x: rect.minX + x * k, y: rect.minY + y * k, width: w * k, height: h * k), cornerRadius: k)
        }
        var path = Path()
        switch icon {
        case .list:
            for y in [4, 8, 12] as [CGFloat] { line(&path, 3, y, 13, y) }
        case .board:
            path.addPath(box(2, 3, 3.5, 10))
            path.addPath(box(6.5, 3, 3.5, 7))
            path.addPath(box(11, 3, 3, 5))
        case .gear:
            path.addEllipse(in: CGRect(x: rect.minX + 6 * k, y: rect.minY + 6 * k, width: 4 * k, height: 4 * k))
            line(&path, 8, 1.5, 8, 3.5)
            line(&path, 8, 12.5, 8, 14.5)
            line(&path, 1.5, 8, 3.5, 8)
            line(&path, 12.5, 8, 14.5, 8)
            line(&path, 3.4, 3.4, 4.8, 4.8)
            line(&path, 11.2, 11.2, 12.6, 12.6)
            line(&path, 3.4, 12.6, 4.8, 11.2)
            line(&path, 11.2, 4.8, 12.6, 3.4)
        case .shield:
            path.move(to: p(8, 1.6))
            path.addLine(to: p(13, 3.6))
            path.addLine(to: p(13, 7.5))
            path.addCurve(to: p(8, 14.4), control1: p(13, 10.6), control2: p(10.9, 13))
            path.addCurve(to: p(3, 7.5), control1: p(5.1, 13), control2: p(3, 10.6))
            path.addLine(to: p(3, 3.6))
            path.closeSubpath()
            path.move(to: p(5.9, 8))
            path.addLine(to: p(7.4, 9.5))
            path.addLine(to: p(10.2, 6.5))
        case .search:
            path.addEllipse(in: CGRect(x: rect.minX + 2.5 * k, y: rect.minY + 2.5 * k, width: 9 * k, height: 9 * k))
            line(&path, 10.5, 10.5, 14, 14)
        case .more:
            for x in [3.5, 8, 12.5] as [CGFloat] { line(&path, x, 8, x, 8.01) }
        case .check:
            path.move(to: p(3, 8.5))
            path.addLine(to: p(6.2, 11.5))
            path.addLine(to: p(13, 4.5))
        case .up:
            path.move(to: p(4, 9.5))
            path.addLine(to: p(8, 5.5))
            path.addLine(to: p(12, 9.5))
        case .down:
            path.move(to: p(4, 6.5))
            path.addLine(to: p(8, 10.5))
            path.addLine(to: p(12, 6.5))
        case .x:
            line(&path, 4, 4, 12, 12)
            line(&path, 12, 4, 4, 12)
        case .plus:
            line(&path, 8, 3, 8, 13)
            line(&path, 3, 8, 13, 8)
        case .trash:
            line(&path, 3, 4.5, 13, 4.5)
            path.move(to: p(6.5, 4.5))
            path.addLine(to: p(6.5, 3))
            path.addLine(to: p(9.5, 3))
            path.addLine(to: p(9.5, 4.5))
            path.move(to: p(4.5, 4.5))
            path.addLine(to: p(5, 13))
            path.addLine(to: p(11, 13))
            path.addLine(to: p(11.5, 4.5))
        case .back:
            path.move(to: p(10, 3.5))
            path.addLine(to: p(5.5, 8))
            path.addLine(to: p(10, 12.5))
        case .chevronDown:
            path.move(to: p(3.5, 6))
            path.addLine(to: p(8, 10.5))
            path.addLine(to: p(12.5, 6))
        case .warn:
            path.move(to: p(8, 2.5))
            path.addLine(to: p(14, 13))
            path.addLine(to: p(2, 13))
            path.closeSubpath()
            line(&path, 8, 6.5, 8, 9.5)
            line(&path, 8, 11.2, 8, 11.21)
        case .pen:
            path.move(to: p(3, 13))
            path.addLine(to: p(3.7, 10))
            path.addLine(to: p(11, 2.7))
            path.addLine(to: p(13.3, 5))
            path.addLine(to: p(6, 12.3))
            path.closeSubpath()
        case .lock:
            path.addPath(box(3.5, 7, 9, 6.5))
            path.move(to: p(5.5, 7))
            path.addLine(to: p(5.5, 5))
            path.addArc(center: p(8, 5), radius: 2.5 * k, startAngle: .degrees(180), endAngle: .degrees(0), clockwise: false)
            path.addLine(to: p(10.5, 7))
        case .inbox:
            path.move(to: p(2.5, 9.5))
            path.addLine(to: p(4, 3.5))
            path.addLine(to: p(12, 3.5))
            path.addLine(to: p(13.5, 9.5))
            path.addLine(to: p(13.5, 12.5))
            path.addLine(to: p(2.5, 12.5))
            path.closeSubpath()
            path.move(to: p(2.5, 9.5))
            path.addLine(to: p(5.5, 9.5))
            path.addLine(to: p(6.5, 11))
            path.addLine(to: p(9.5, 11))
            path.addLine(to: p(10.5, 9.5))
            path.addLine(to: p(13.5, 9.5))
        }
        return path
    }
}

struct MockupIconView: View {
    let icon: MockupIcon
    var size: CGFloat = 14

    var body: some View {
        MockupIconShape(icon: icon)
            .stroke(style: StrokeStyle(lineWidth: icon == .more ? 2.4 * size / DesignLayout.iconBox : DesignLayout.iconStroke * size / DesignLayout.iconBox, lineCap: .round, lineJoin: .round))
            .frame(width: size, height: size)
    }
}
