import AppKit
import SwiftUI

struct DesignShadowSpec: Sendable {
    let x: CGFloat
    let y: CGFloat
    let blur: CGFloat
    let lightColor: String
    let darkColor: String
}

enum StatusIconCommand: Sendable {
    case move(CGFloat, CGFloat)
    case line(CGFloat, CGFloat)
    case close
}

enum StatusIconShape: Sendable {
    case circle(cx: CGFloat, cy: CGFloat, r: CGFloat)
    case pie(cx: CGFloat, cy: CGFloat, r: CGFloat, fraction: CGFloat)
    case path([StatusIconCommand])
}

struct StatusIconPaint: Sendable {
    var fillOpacity: Double?
    var strokeWidth: CGFloat?
    var dash: [CGFloat] = []
    var cap: CGLineCap = .butt
    var join: CGLineJoin = .miter
    var color: DesignColor?

    init(fill: Bool = false, strokeWidth: CGFloat? = nil, dash: [CGFloat] = [], cap: CGLineCap = .butt, join: CGLineJoin = .miter, color: DesignColor? = nil) {
        self.fillOpacity = fill ? 1 : nil
        self.strokeWidth = strokeWidth
        self.dash = dash
        self.cap = cap
        self.join = join
        self.color = color
    }

    init(fill: Double, strokeWidth: CGFloat? = nil, dash: [CGFloat] = [], cap: CGLineCap = .butt, join: CGLineJoin = .miter, color: DesignColor? = nil) {
        self.fillOpacity = fill
        self.strokeWidth = strokeWidth
        self.dash = dash
        self.cap = cap
        self.join = join
        self.color = color
    }
}

struct StatusIconPrimitive: Sendable {
    let shape: StatusIconShape
    let paint: StatusIconPaint
}

extension StatusIconShape {
    func path(scale k: CGFloat) -> Path {
        switch self {
        case let .circle(cx, cy, r):
            return Path(ellipseIn: CGRect(x: (cx - r) * k, y: (cy - r) * k, width: 2 * r * k, height: 2 * r * k))
        case let .pie(cx, cy, r, fraction):
            var path = Path()
            let center = CGPoint(x: cx * k, y: cy * k)
            path.move(to: center)
            path.addArc(center: center, radius: r * k, startAngle: .degrees(-90), endAngle: .degrees(-90 + 360 * Double(fraction)), clockwise: false)
            path.closeSubpath()
            return path
        case let .path(commands):
            var path = Path()
            for command in commands {
                switch command {
                case let .move(x, y): path.move(to: CGPoint(x: x * k, y: y * k))
                case let .line(x, y): path.addLine(to: CGPoint(x: x * k, y: y * k))
                case .close: path.closeSubpath()
                }
            }
            return path
        }
    }
}

struct StatusIconView: View {
    let name: StatusIconName
    let color: Color
    var size: CGFloat = StatusIconName.box

    var body: some View {
        Canvas { context, canvasSize in
            let k = min(canvasSize.width, canvasSize.height) / StatusIconName.box
            for primitive in name.primitives {
                let path = primitive.shape.path(scale: k)
                let paint = primitive.paint
                let tint = paint.color?.color ?? color
                if let opacity = paint.fillOpacity {
                    context.fill(path, with: .color(tint.opacity(opacity)))
                }
                if let width = paint.strokeWidth {
                    context.stroke(path, with: .color(tint), style: StrokeStyle(lineWidth: width * k, lineCap: paint.cap, lineJoin: paint.join, dash: paint.dash.map { $0 * k }))
                }
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

extension Font {
    static func design(_ size: CGFloat, weight: Font.Weight = DesignTypography.regularWeight, mono: Bool = false) -> Font {
        .system(size: size, weight: weight, design: mono ? .monospaced : .default)
    }

    static func designEmphasis(_ size: CGFloat) -> Font {
        Font(NSFont.systemFont(ofSize: size, weight: NSFont.Weight(rawValue: DesignTypography.emphasisWeight)))
    }
}

extension View {
    func designShadow(_ spec: DesignShadowSpec, scheme: ColorScheme) -> some View {
        shadow(color: Color(designHex: scheme == .dark ? spec.darkColor : spec.lightColor), radius: spec.blur / 2, x: spec.x, y: spec.y)
    }
}

extension Color {
    init(designHex: String) {
        let (r, g, b, a) = DesignHex.components(designHex)
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }
}

enum DesignHex {
    static func components(_ text: String) -> (Double, Double, Double, Double) {
        if text.hasPrefix("#"), text.count == 7, let value = Int(text.dropFirst(), radix: 16) {
            return (Double((value >> 16) & 255) / 255, Double((value >> 8) & 255) / 255, Double(value & 255) / 255, 1)
        }
        if text.hasPrefix("rgba(") {
            let parts = text.dropFirst(5).dropLast().split(separator: ",").compactMap { Double($0) }
            if parts.count == 4 {
                return (parts[0] / 255, parts[1] / 255, parts[2] / 255, parts[3])
            }
        }
        return (0, 0, 0, 1)
    }
}

struct DesignDivider: View {
    var vertical = false

    var body: some View {
        Rectangle()
            .fill(DesignColor.line.color)
            .frame(width: vertical ? DesignLayout.borderWidth : nil, height: vertical ? nil : DesignLayout.borderWidth)
    }
}
