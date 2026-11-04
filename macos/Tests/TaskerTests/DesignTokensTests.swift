import AppKit
import SwiftUI
import Testing
@testable import Tasker

struct DesignTokensTests {
    private func resolved(_ token: DesignColor, dark: Bool) -> (Double, Double, Double, Double) {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)!
        var result: (Double, Double, Double, Double) = (0, 0, 0, 0)
        appearance.performAsCurrentDrawingAppearance {
            let color = NSColor(named: token.assetName, bundle: Bundle.main)!.usingColorSpace(.sRGB)!
            result = (Double(color.redComponent), Double(color.greenComponent), Double(color.blueComponent), Double(color.alphaComponent))
        }
        return result
    }

    private func matches(_ a: (Double, Double, Double, Double), _ hex: String) -> Bool {
        let b = DesignHex.components(hex)
        return abs(a.0 - b.0) < 0.005 && abs(a.1 - b.1) < 0.005 && abs(a.2 - b.2) < 0.005 && abs(a.3 - b.3) < 0.005
    }

    @Test func everyColorAssetMatchesTokenInBothThemes() {
        for token in DesignColor.allCases {
            #expect(matches(resolved(token, dark: false), token.lightHex), "\(token.rawValue) light")
            #expect(matches(resolved(token, dark: true), token.darkHex), "\(token.rawValue) dark")
        }
    }

    @Test func statusIconsHaveDrawing() {
        for name in StatusIconName.allCases {
            #expect(!name.primitives.isEmpty)
        }
        #expect(StatusIconName.allCases.count == 12)
    }

    @Test func layoutMatchesMockup() {
        #expect(DesignLayout.sidebarWidth == 232)
        #expect(DesignLayout.topBarHeight == 44)
    }
}
