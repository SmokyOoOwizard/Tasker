import AppKit
import Testing
@testable import Tasker

@MainActor
struct GroupHeaderLayoutTests {
    private func labels(_ cell: GroupCellView) -> (name: NSTextField, count: NSTextField) {
        let fields = cell.subviews.compactMap { $0 as? NSTextField }
        return (fields[0], fields[1])
    }

    private func laidOut(width: CGFloat, count: String) -> GroupCellView {
        let cell = GroupCellView()
        cell.frame = NSRect(x: 0, y: 0, width: width, height: 28)
        cell.configure(name: "Готово", count: count, glyph: .named(.dashed), tint: .systemGreen, expanded: true)
        cell.layoutSubtreeIfNeeded()
        return cell
    }

    @Test(arguments: ["1", "120", "10000"])
    func countIsFullyVisible(count: String) {
        let cell = laidOut(width: 600, count: count)
        let (name, number) = labels(cell)
        let needed = ceil(number.attributedStringValue.size().width)
        #expect(number.frame.width >= needed)
        #expect(number.frame.maxX <= cell.bounds.width)
        #expect(name.frame.width >= ceil(name.attributedStringValue.size().width))
    }

    @Test(arguments: [110.0, 120.0, 130.0])
    func narrowColumnShrinksNameNotCount(width: CGFloat) {
        let cell = laidOut(width: width, count: "10000")
        let (name, number) = labels(cell)
        #expect(number.frame.width >= ceil(number.attributedStringValue.size().width))
        #expect(name.frame.maxX <= number.frame.minX)
        #expect(name.frame.width < ceil(name.attributedStringValue.size().width) + 4)
        #expect(number.frame.maxX <= cell.bounds.width)
    }
}
