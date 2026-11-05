import Foundation

enum PaletteIcon {
    case mockup(MockupIcon)
    case system(String)
    case status(StatusGlyph, color: String)
}

struct PaletteEntry: Identifiable {
    let id: String
    var title: String
    var reference: String?
    var detail: String?
    var icon: PaletteIcon
    var keywords = ""
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
