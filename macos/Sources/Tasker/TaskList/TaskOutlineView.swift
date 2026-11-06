import AppKit
import SwiftUI

final class TaskOutlineView: NSOutlineView {
    var mode: TaskListMode = .groups
    var onOpen: ((Int) -> Void)?
    var onPeek: ((Int) -> Void)?
    var onEscape: (() -> Bool)?
    var onStatus: ((Int) -> Void)?
    var onToggleGroup: ((Int) -> Void)?
    var contextMenu: ((Int) -> NSMenu?)?
    private(set) var hoveredRow = -1
    private var tracking: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeInActiveApp, .inVisibleRect], owner: self)
        addTrackingArea(area)
        tracking = area
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        updateHover()
    }

    override func mouseEntered(with event: NSEvent) {
        super.mouseEntered(with: event)
        updateHover()
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        setHovered(-1)
    }

    func updateHover() {
        guard let window else { return setHovered(-1) }
        let point = convert(window.mouseLocationOutsideOfEventStream, from: nil)
        let inside = enclosingScrollView.map { $0.contentView.documentVisibleRect.contains(point) } ?? bounds.contains(point)
        setHovered(inside ? row(at: point) : -1)
    }

    private func setHovered(_ row: Int) {
        guard row != hoveredRow else { return }
        let old = hoveredRow
        hoveredRow = row
        for index in [old, row] where index >= 0 && index < numberOfRows {
            rowView(atRow: index, makeIfNecessary: false)?.needsDisplay = true
            (view(atColumn: 0, row: index, makeIfNecessary: false) as? TaskCellView)?.statusButton.needsDisplay = true
        }
    }

    private func leading(_ row: Int) -> CGFloat {
        let level = CGFloat(self.level(forRow: row))
        switch mode {
        case .tree: return DesignSpacing.pagePadX + level * DesignLayout.treeIndent
        case .flat, .groups: return DesignSpacing.pagePadX
        }
    }

    override func frameOfOutlineCell(atRow row: Int) -> NSRect {
        guard mode == .tree, row >= 0, row < numberOfRows else { return .zero }
        let rect = rect(ofRow: row)
        let side = DesignLayout.twistyWidth
        return NSRect(x: leading(row), y: rect.midY - side / 2, width: side, height: side)
    }

    override func frameOfCell(atColumn column: Int, row: Int) -> NSRect {
        let rect = rect(ofRow: row)
        var x = leading(row)
        if mode == .tree { x += DesignLayout.twistyWidth + DesignSpacing.gap10 / 2 }
        return NSRect(x: x, y: rect.minY, width: max(0, rect.width - x), height: rect.height)
    }

    override var acceptsFirstResponder: Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard window != nil else { return }
        DispatchQueue.main.async { [weak self] in
            guard let self, let window = self.window, window.isKeyWindow else { return }
            if window.firstResponder is NSText { return }
            window.makeFirstResponder(self)
        }
    }

    override func keyDown(with event: NSEvent) {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option])
        guard modifiers.isEmpty else { return super.keyDown(with: event) }
        switch LatinKeys.character(for: event) {
        case "j":
            move(by: 1)
        case "k":
            move(by: -1)
        case "s":
            if selectedRow >= 0 { onStatus?(selectedRow) }
        case "\u{1b}":
            if onEscape?() != true { super.keyDown(with: event) }
        case " ":
            if selectedRow >= 0 { onPeek?(selectedRow) }
        case "\r", "\u{3}":
            if selectedRow >= 0 {
                if item(atRow: selectedRow).map({ ($0 as? TaskNode)?.kind == .group }) == true {
                    onToggleGroup?(selectedRow)
                } else {
                    onOpen?(selectedRow)
                }
            }
        default:
            super.keyDown(with: event)
        }
    }

    private func move(by delta: Int) {
        guard numberOfRows > 0 else { return }
        let next = selectedRow < 0 ? 0 : min(max(selectedRow + delta, 0), numberOfRows - 1)
        selectRowIndexes(IndexSet(integer: next), byExtendingSelection: false)
        scrollRowToVisible(next)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let row = self.row(at: convert(event.locationInWindow, from: nil))
        if row >= 0, !selectedRowIndexes.contains(row) {
            selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
        }
        return contextMenu?(row)
    }
}

struct TaskOutline: NSViewRepresentable {
    let model: TaskListModel
    let localizer: Localizer
    let notice: (String) -> Void

    func makeCoordinator() -> TaskOutlineCoordinator {
        TaskOutlineCoordinator(model: model)
    }

    func makeNSView(context: Context) -> NSScrollView {
        context.coordinator.localizer = localizer
        context.coordinator.notice = notice
        return Self.makeScrollView(coordinator: context.coordinator, localizer: localizer)
    }

    static func makeScrollView(coordinator: TaskOutlineCoordinator, localizer: Localizer) -> NSScrollView {
        let outline = TaskOutlineView()
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("task"))
        column.resizingMask = .autoresizingMask
        outline.addTableColumn(column)
        outline.outlineTableColumn = column
        outline.headerView = nil
        outline.style = .plain
        outline.rowHeight = DesignLayout.taskRowHeight
        outline.intercellSpacing = .zero
        outline.indentationPerLevel = 0
        outline.backgroundColor = DesignColor.bg.nsColor
        outline.gridStyleMask = []
        outline.selectionHighlightStyle = .regular
        outline.floatsGroupRows = false
        outline.usesAutomaticRowHeights = false
        outline.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        outline.focusRingType = .none
        outline.allowsEmptySelection = true
        outline.autoresizesOutlineColumn = false
        outline.setAccessibilityLabel(localizer.string(.taskListTitle))

        let scroll = NSScrollView()
        scroll.documentView = outline
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = true
        scroll.backgroundColor = DesignColor.bg.nsColor
        scroll.contentView.postsBoundsChangedNotifications = true
        coordinator.localizer = localizer
        coordinator.attach(outline, scroll: scroll)
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.localizer = localizer
        context.coordinator.notice = notice
        context.coordinator.syncMode()
    }

    static func dismantleNSView(_ scroll: NSScrollView, coordinator: TaskOutlineCoordinator) {
        coordinator.detach()
    }
}

@MainActor
final class TaskOutlineCoordinator: NSObject, NSOutlineViewDataSource, NSOutlineViewDelegate {
    let model: TaskListModel
    var localizer = Localizer(choice: .system)
    var notice: (String) -> Void = { _ in }
    private weak var outline: TaskOutlineView?
    private var suppress = 0
    private var observer: NSObjectProtocol?
    private var shownMode: TaskListMode?
    private var restoreUntil: ContinuousClock.Instant?

    static let restoreWindow: Duration = .seconds(5)

    init(model: TaskListModel) {
        self.model = model
    }

    func attach(_ outline: TaskOutlineView, scroll: NSScrollView) {
        self.outline = outline
        outline.dataSource = self
        outline.delegate = self
        outline.target = self
        outline.action = #selector(clicked)
        outline.doubleAction = #selector(doubleClicked)
        outline.onOpen = { [weak self] row in self?.open(row) }
        outline.onPeek = { [weak self] row in self?.peek(row) }
        outline.onEscape = { [weak self] in self?.model.escape() ?? false }
        outline.onStatus = { [weak self] row in self?.showStatusMenu(row) }
        outline.onToggleGroup = { [weak self] row in self?.toggle(row) }
        outline.contextMenu = { [weak self] row in self?.menu(row) }
        outline.mode = model.mode
        shownMode = model.mode
        model.onChange = { [weak self] change in self?.apply(change) }
        model.changeOwner = self
        if model.scrollY > 0 || model.selectedRow != nil { restoreUntil = .now + Self.restoreWindow }
        observer = NotificationCenter.default.addObserver(forName: NSView.boundsDidChangeNotification, object: scroll.contentView, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.checkEnd()
                self?.noteViewState()
            }
        }
        apply(.reload)
    }

    func detach() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        if model.changeOwner === self { model.onChange = nil }
    }

    private func noteViewState() {
        guard restoreUntil == nil, let outline, let scroll = outline.enclosingScrollView else { return }
        model.noteViewState(selectedRow: node(outline.selectedRow)?.key, scrollY: scroll.contentView.bounds.origin.y)
    }

    private func scheduleRestore() {
        guard restoreUntil != nil else { return }
        DispatchQueue.main.async { [weak self] in self?.restoreView() }
    }

    private func restoreView() {
        guard let deadline = restoreUntil, let outline, let scroll = outline.enclosingScrollView, outline.numberOfRows > 0 else { return }
        guard ContinuousClock.now < deadline else {
            restoreUntil = nil
            return
        }
        var selectionPlaced = model.selectedRow == nil
        if let key = model.selectedRow, let row = rowFor(key: key) {
            selectionPlaced = true
            if outline.selectedRow != row {
                suppress += 1
                outline.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
                suppress -= 1
            }
        }
        let limit = max(0, outline.frame.height - scroll.contentView.bounds.height)
        scroll.contentView.scroll(to: NSPoint(x: 0, y: min(model.scrollY, limit)))
        scroll.reflectScrolledClipView(scroll.contentView)
        if selectionPlaced, model.scrollY <= limit { restoreUntil = nil }
    }

    func syncMode() {
        guard let outline, shownMode != model.mode else { return }
        shownMode = model.mode
        outline.mode = model.mode
    }

    private func node(_ row: Int) -> TaskNode? {
        guard let outline, row >= 0 else { return nil }
        return outline.item(atRow: row) as? TaskNode
    }

    func apply(_ change: TaskListChange) {
        guard let outline else { return }
        suppress += 1
        defer {
            suppress -= 1
            updateSticky()
            scheduleRestore()
        }
        switch change {
        case .reload:
            outline.mode = model.mode
            shownMode = model.mode
            let selected = node(outline.selectedRow)?.key
            outline.reloadData()
            expand(model.roots)
            if let selected, let row = rowFor(key: selected) {
                outline.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
            }
            checkEnd()
        case .rows(let nodes):
            var rows = IndexSet()
            for node in nodes {
                let row = outline.row(forItem: node)
                if row >= 0 { rows.insert(row) }
            }
            if !rows.isEmpty { outline.reloadData(forRowIndexes: rows, columnIndexes: IndexSet(integer: 0)) }
        case .inserted(let parent, let indexes):
            outline.insertItems(at: indexes, inParent: parent, withAnimation: [])
            if parent == nil {
                expand(indexes.compactMap { $0 < model.roots.count ? model.roots[$0] : nil })
            }
            checkEnd()
        case .children(let node):
            outline.reloadItem(node, reloadChildren: true)
            if model.shouldExpand(node) || outline.isItemExpanded(node) {
                outline.expandItem(node)
                expand(node.children ?? [])
            }
            checkEnd()
        }
    }

    private func expand(_ nodes: [TaskNode]) {
        guard let outline else { return }
        for node in nodes where node.children != nil && model.shouldExpand(node) {
            outline.expandItem(node)
            expand(node.children ?? [])
        }
    }

    private func rowFor(key: String) -> Int? {
        guard let outline else { return nil }
        for row in 0..<outline.numberOfRows where (outline.item(atRow: row) as? TaskNode)?.key == key {
            return row
        }
        return nil
    }

    func checkEnd() {
        guard let outline, let scroll = outline.enclosingScrollView else { return }
        outline.updateHover()
        updateSticky()
        let visible = outline.rows(in: scroll.contentView.documentVisibleRect)
        model.visibleCount = loadedOrdinal(of: visible.location + visible.length - 1)
        let end = visible.location + visible.length + 60
        if model.mode == .groups {
            for group in model.roots where outline.isItemExpanded(group) {
                let last = group.children?.last.map { outline.row(forItem: $0) } ?? outline.row(forItem: group)
                if last >= 0, last <= end { model.loadMore(group: group) }
            }
        } else if outline.numberOfRows == 0 || end >= outline.numberOfRows {
            model.loadMore()
        }
    }

    private func loadedOrdinal(of row: Int) -> Int {
        guard let node = node(row) else { return 0 }
        var top = node
        while let parent = top.parent, parent.kind == .task { top = parent }
        switch model.mode {
        case .flat, .tree:
            return (model.roots.firstIndex { $0 === top } ?? 0) + 1
        case .groups:
            var count = 0
            for group in model.roots {
                if group === top { return count + (group.children?.count ?? 0) }
                if group === top.parent, let index = group.children?.firstIndex(where: { $0 === top }) { return count + index + 1 }
                count += group.children?.count ?? 0
            }
            return count
        }
    }

    @objc private func clicked() {
        guard let outline, outline.clickedRow >= 0, node(outline.clickedRow)?.kind == .group else { return }
        toggle(outline.clickedRow)
    }

    @objc private func doubleClicked() {
        guard let outline, outline.clickedRow >= 0 else { return }
        if node(outline.clickedRow)?.kind == .task { open(outline.clickedRow) }
    }

    func outlineViewSelectionDidChange(_ notification: Notification) {
        guard suppress == 0, let outline else { return }
        noteViewState()
        guard let node = node(outline.selectedRow), node.kind == .task else {
            model.selectionCleared()
            return
        }
        model.selectionChanged(node)
    }

    private func open(_ row: Int) {
        guard let node = node(row), node.kind == .task else { return }
        model.open(node)
    }

    private func peek(_ row: Int) {
        guard let node = node(row), node.kind == .task else { return }
        model.peek(node)
    }

    private func toggle(_ row: Int) {
        guard let outline, let node = node(row) else { return }
        if outline.isItemExpanded(node) {
            outline.collapseItem(node)
        } else {
            outline.expandItem(node)
        }
    }

    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        guard let node = item as? TaskNode else { return model.roots.count }
        return node.children?.count ?? 0
    }

    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        guard let node = item as? TaskNode else { return model.roots[index] }
        return node.children?[index] ?? node
    }

    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
        guard let node = item as? TaskNode else { return false }
        return node.kind == .group || (model.mode == .tree && node.isExpandable)
    }

    func outlineView(_ outlineView: NSOutlineView, isGroupItem item: Any) -> Bool {
        (item as? TaskNode)?.kind == .group
    }

    func outlineView(_ outlineView: NSOutlineView, shouldSelectItem item: Any) -> Bool {
        true
    }

    func outlineView(_ outlineView: NSOutlineView, heightOfRowByItem item: Any) -> CGFloat {
        (item as? TaskNode)?.kind == .group ? DesignLayout.groupRowHeight : DesignLayout.taskRowHeight
    }

    func outlineView(_ outlineView: NSOutlineView, rowViewForItem item: Any) -> NSTableRowView? {
        let identifier = NSUserInterfaceItemIdentifier("TaskRow")
        if let view = outlineView.makeView(withIdentifier: identifier, owner: nil) as? TaskRowView { return view }
        let view = TaskRowView()
        view.identifier = identifier
        return view
    }

    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        guard let node = item as? TaskNode else { return nil }
        switch node.kind {
        case .group:
            let cell = outlineView.makeView(withIdentifier: GroupCellView.identifier, owner: nil) as? GroupCellView ?? GroupCellView()
            configure(cell, group: node, expanded: outlineView.isItemExpanded(node))
            return cell
        case .task:
            guard let row = node.row else { return nil }
            let cell = outlineView.makeView(withIdentifier: TaskCellView.identifier, owner: nil) as? TaskCellView ?? TaskCellView()
            cell.configure(content(row), repeatedText: localizer.string(.taskListRepeated), repeatedHint: localizer.string(.taskListRepeatedHint), statusHint: localizer.string(.taskListChangeStatus), conflictHint: localizer.string(.seriesNumberConflict))
            cell.statusButton.target = self
            cell.statusButton.action = #selector(statusClicked(_:))
            return cell
        }
    }

    private func configure(_ cell: GroupCellView, group node: TaskNode, expanded: Bool) {
        let status = node.groupStatus.flatMap(model.vocabulary.status)
        cell.configure(
            name: status?.name ?? "",
            count: String(node.groupCount ?? node.children?.count ?? 0),
            glyph: node.groupStatus.map(model.vocabulary.glyph(status:)) ?? .named(.dashed),
            tint: status.map { NSColor(designHex: $0.color) } ?? DesignColor.dim.nsColor,
            expanded: expanded)
    }

    private let sticky = StickyGroupHeader()

    func updateSticky() {
        guard let outline, let scroll = outline.enclosingScrollView else { return }
        let visible = scroll.contentView.documentVisibleRect
        guard model.mode == .groups, visible.minY > 0,
              let node = node(outline.row(at: NSPoint(x: visible.midX, y: visible.minY + 1))),
              let group = node.kind == .task ? node.parent : nil, group.kind == .group else {
            sticky.isHidden = true
            return
        }
        let height = DesignLayout.groupRowHeight
        var offset: CGFloat = 0
        if let index = model.roots.firstIndex(where: { $0 === group }), index + 1 < model.roots.count {
            let next = outline.row(forItem: model.roots[index + 1])
            if next >= 0 { offset = min(0, outline.rect(ofRow: next).minY - visible.minY - height) }
        }
        if sticky.superview !== outline || outline.subviews.last !== sticky {
            outline.addSubview(sticky, positioned: .above, relativeTo: nil)
        }
        sticky.group = group
        sticky.onClick = { [weak self] group in self?.collapseFromSticky(group) }
        configure(sticky.cell, group: group, expanded: true)
        sticky.frame = NSRect(x: 0, y: visible.minY + offset, width: outline.bounds.width, height: height)
        sticky.cell.frame = NSRect(x: DesignSpacing.pagePadX, y: 0, width: max(0, outline.bounds.width - DesignSpacing.pagePadX), height: height)
        sticky.isHidden = false
    }

    private func collapseFromSticky(_ group: TaskNode) {
        guard let outline else { return }
        outline.collapseItem(group)
        let row = outline.row(forItem: group)
        if row >= 0 { outline.scrollRowToVisible(row) }
        updateSticky()
    }

    var stickyGroup: TaskNode? { sticky.isHidden ? nil : sticky.group }

    private func content(_ row: TaskRow) -> TaskCellContent {
        let vocabulary = model.vocabulary
        let columns = model.preferences.columns
        let status = vocabulary.status(row.statusId)
        return TaskCellContent(
            reference: columns.key ? vocabulary.reference(row, preferring: model.keySeries) : "",
            conflict: columns.key && vocabulary.conflictedTasks.contains(row.id),
            title: row.title,
            repeated: row.repeated,
            typeName: columns.type ? vocabulary.types[row.typeId]?.name : nil,
            chips: columns.fields ? vocabulary.chips(row, fieldIds: columns.fieldIds, limit: TaskListModel.chipLimit, l: localizer) : [],
            date: columns.updated ? localizer.shortDate(row.updatedAt) : nil,
            glyph: vocabulary.glyph(status: row.statusId, type: row.typeId),
            statusColor: status.map { NSColor(designHex: $0.color) } ?? DesignColor.dim.nsColor,
            statusName: status?.name ?? "")
    }

    func outlineViewItemDidExpand(_ notification: Notification) {
        guard let node = notification.userInfo?["NSObject"] as? TaskNode else { return }
        refreshGroupRow(node)
        updateSticky()
        guard suppress == 0 else { return }
        model.setExpanded(node, true)
    }

    func outlineViewItemDidCollapse(_ notification: Notification) {
        guard let node = notification.userInfo?["NSObject"] as? TaskNode else { return }
        refreshGroupRow(node)
        guard suppress == 0 else { return }
        model.setExpanded(node, false)
        checkEnd()
    }

    private func refreshGroupRow(_ node: TaskNode) {
        guard node.kind == .group, let outline else { return }
        let row = outline.row(forItem: node)
        if row >= 0 { outline.reloadData(forRowIndexes: IndexSet(integer: row), columnIndexes: IndexSet(integer: 0)) }
    }

    func outlineView(_ outlineView: NSOutlineView, shouldExpandItem item: Any) -> Bool { true }

    @objc private func statusClicked(_ sender: NSButton) {
        guard let outline else { return }
        let row = outline.row(for: sender)
        guard row >= 0 else { return }
        outline.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
        showStatusMenu(row)
    }

    private func statusMenu(_ node: TaskNode) -> NSMenu? {
        guard let row = node.row else { return nil }
        let menu = NSMenu()
        menu.autoenablesItems = false
        let header = NSMenuItem(title: localizer.string(.taskListStatusTitle, model.vocabulary.reference(row)), action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        for (index, status) in model.statusOptions(node).enumerated() {
            let item = NSMenuItem(title: status.name, action: #selector(statusChosen(_:)), keyEquivalent: index < 9 ? String(index + 1) : "")
            item.keyEquivalentModifierMask = []
            item.target = self
            item.state = status.id == row.statusId ? .on : .off
            item.image = StatusGlyphView.image(model.vocabulary.glyph(status: status.id, type: row.typeId), tint: NSColor(designHex: status.color))
            item.representedObject = StatusChoice(node: node, status: status.id)
            menu.addItem(item)
        }
        return menu
    }

    private func showStatusMenu(_ row: Int) {
        guard let outline, let node = node(row), node.kind == .task, let menu = statusMenu(node) else { return }
        outline.scrollRowToVisible(row)
        let anchor: NSView
        let point: NSPoint
        if let cell = outline.view(atColumn: 0, row: row, makeIfNecessary: false) as? TaskCellView {
            anchor = cell.statusButton
            point = NSPoint(x: 0, y: anchor.isFlipped ? anchor.bounds.maxY + 4 : -4)
        } else {
            anchor = outline
            point = outline.rect(ofRow: row).origin
        }
        menu.popUp(positioning: nil, at: point, in: anchor)
    }

    @objc private func statusChosen(_ sender: NSMenuItem) {
        guard let choice = sender.representedObject as? StatusChoice else { return }
        model.changeStatus(choice.node, to: choice.status)
    }

    private func menu(_ row: Int) -> NSMenu? {
        let menu = NSMenu()
        menu.autoenablesItems = false
        if let node = node(row), node.kind == .task, let statuses = statusMenu(node) {
            let item = NSMenuItem(title: localizer.string(.taskListChangeStatus), action: nil, keyEquivalent: "")
            statuses.items.first.map(statuses.removeItem)
            item.submenu = statuses
            menu.addItem(item)
            menu.addItem(.separator())
        }
        let columns = model.preferences.columns
        let show = NSMenuItem(title: localizer.string(.taskListColumns), action: nil, keyEquivalent: "")
        show.isEnabled = false
        menu.addItem(show)
        for (key, title, on) in [(ColumnKey.key, L10nKey.taskListColumnKey, columns.key), (.type, .taskListColumnType, columns.type), (.fields, .taskListColumnFields, columns.fields), (.updated, .taskListColumnUpdated, columns.updated)] {
            let item = NSMenuItem(title: localizer.string(title), action: #selector(columnToggled(_:)), keyEquivalent: "")
            item.target = self
            item.state = on ? .on : .off
            item.representedObject = key.rawValue
            item.indentationLevel = 1
            menu.addItem(item)
        }
        let fields = model.vocabulary.fields
        if !fields.isEmpty {
            let chosen = columns.fieldIds ?? model.vocabulary.defaultChipFields
            let submenu = NSMenu()
            submenu.autoenablesItems = false
            let all = NSMenuItem(title: localizer.string(.taskListFieldsDefault), action: #selector(fieldsReset), keyEquivalent: "")
            all.target = self
            all.state = columns.fieldIds == nil ? .on : .off
            submenu.addItem(all)
            submenu.addItem(.separator())
            for field in fields {
                let item = NSMenuItem(title: field.name, action: #selector(fieldToggled(_:)), keyEquivalent: "")
                item.target = self
                item.state = chosen.contains(field.id) ? .on : .off
                item.representedObject = field.id
                submenu.addItem(item)
            }
            let item = NSMenuItem(title: localizer.string(.taskListFieldsMenu), action: nil, keyEquivalent: "")
            item.submenu = submenu
            menu.addItem(.separator())
            menu.addItem(item)
        }
        return menu
    }

    @objc private func columnToggled(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let key = ColumnKey(rawValue: raw) else { return }
        model.update { preferences in
            switch key {
            case .key: preferences.columns.key.toggle()
            case .type: preferences.columns.type.toggle()
            case .fields: preferences.columns.fields.toggle()
            case .updated: preferences.columns.updated.toggle()
            }
        }
    }

    @objc private func fieldsReset() {
        model.update { $0.columns.fieldIds = nil }
    }

    @objc private func fieldToggled(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID else { return }
        let fallback = model.vocabulary.defaultChipFields
        model.update { preferences in
            var chosen = preferences.columns.fieldIds ?? fallback
            if let index = chosen.firstIndex(of: id) {
                chosen.remove(at: index)
            } else {
                chosen.append(id)
            }
            preferences.columns.fieldIds = chosen
            preferences.columns.fields = true
        }
    }
}

private enum ColumnKey: String {
    case key
    case type
    case fields
    case updated
}

private final class StatusChoice: NSObject {
    let node: TaskNode
    let status: UUID

    init(node: TaskNode, status: UUID) {
        self.node = node
        self.status = status
    }
}
