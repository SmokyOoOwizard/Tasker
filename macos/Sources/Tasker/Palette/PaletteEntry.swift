import Foundation

enum PaletteIcon {
    case mockup(MockupIcon)
    case system(String)
    case status(StatusGlyph, color: String)
}

struct PaletteSnippet: Equatable {
    var text: String
    var ranges: [Range<Int>]
}

struct PaletteEntry: Identifiable {
    let id: String
    var title: String
    var reference: String?
    var detail: String?
    var icon: PaletteIcon
    var keywords = ""
    var snippet: PaletteSnippet?
    var shortcut: String?
    var isEnabled = true
    var run: @MainActor () -> Void
}

struct PaletteSection: Identifiable {
    let id: String
    var title: String?
    var entries: [PaletteEntry]
}

protocol PaletteQueryProvider {
    @MainActor
    func sections(for text: String) -> [PaletteSection]
}
