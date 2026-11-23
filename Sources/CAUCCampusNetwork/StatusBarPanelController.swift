import AppKit
import Combine
import SwiftUI

@MainActor
final class StatusBarPanelController: NSObject, NSWindowDelegate {
    private static let cardWidth: CGFloat = 312
    private static let padTop: CGFloat = 6
    private static let padHorizontal: CGFloat = 20
    private static let padBottom: CGFloat = 26
    private static let panelWidth: CGFloat = cardWidth + padHorizontal * 2

    private let controller: AppController
    private let launchAtLogin: LaunchAtLoginManager
    private let statusItem: NSStatusItem
    private var panel: FloatingMenuPanel?
    private var hostingView: AutoSizingHostingView<MenuBarPopoverView>?
    private var cancellables: Set<AnyCancellable> = []
    private var lastClosedAt: Date = .distantPast
    nonisolated(unsafe) private var globalClickMonitor: Any?
    nonisolated(unsafe) private var localKeyMonitor: Any?

    init(controller: AppController, launchAtLogin: LaunchAtLoginManager) {
        self.controller = controller
        self.launchAtLogin = launchAtLogin
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()

        if let button = statusItem.button {
            button.target = self
            button.action = #selector(togglePanel(_:))
            button.sendAction(on: [.leftMouseUp])
            button.toolTip = "CAUC 校园网"
        }

        updateStatusIcon()

        Publishers.CombineLatest(controller.$connectivity, controller.$isBusy)
            .receive(on: RunLoop.main)
            .sink { [weak self] _, _ in
                Task { @MainActor [weak self] in
                    self?.updateStatusIcon()
                }
            }
            .store(in: &cancellables)
    }

    deinit {
        if let globalClickMonitor {
            NSEvent.removeMonitor(globalClickMonitor)
        }
        if let localKeyMonitor {
            NSEvent.removeMonitor(localKeyMonitor)
        }
    }

    @objc private func togglePanel(_ sender: AnyObject?) {
        if panel?.isVisible == true {
            closePanel()
            return
        }
        if Date().timeIntervalSince(lastClosedAt) < 0.25 {
            return
        }
        openPanel()
    }

    private func updateStatusIcon() {
        guard let button = statusItem.button else { return }
        let image = NSImage(
            systemSymbolName: controller.menuBarSymbolName,
            accessibilityDescription: "CAUC 校园网"
        )
        image?.isTemplate = true
        button.image = image
    }

    private func ensurePanel() -> FloatingMenuPanel {
        if let panel {
            return panel
        }

        let rootView = MenuBarPopoverView(
            controller: controller,
            launchAtLogin: launchAtLogin,
            onDismissPanel: { [weak self] in
                self?.closePanel()
            }
        )

        let hosting = AutoSizingHostingView(rootView: rootView)
        hosting.sizingOptions = [.intrinsicContentSize]
        hosting.wantsLayer = true
        hosting.layer?.backgroundColor = NSColor.clear.cgColor
        hosting.onSizeChange = { [weak self] size in
            Task { @MainActor [weak self] in
                self?.updatePanelSize(to: size)
            }
        }

        let created = FloatingMenuPanel(
            contentRect: NSRect(x: 0, y: 0, width: Self.panelWidth, height: 320),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        created.isOpaque = false
        created.backgroundColor = .clear
        created.hasShadow = false
        created.level = .popUpMenu
        created.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        created.isMovable = false
        created.hidesOnDeactivate = false
        created.delegate = self
        created.onEscape = { [weak self] in
            self?.closePanel()
        }

        let container = TransparentPanelContainerView()
        container.onOutsideClick = { [weak self] in
            self?.closePanel()
        }

        let cardContainer: NSView
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.style = .regular
            glass.cornerRadius = 26
            glass.contentView = hosting
            cardContainer = glass
        } else {
            let shadowWrapper = NSView()
            shadowWrapper.wantsLayer = true
            shadowWrapper.layer?.shadowColor = NSColor.black.cgColor
            shadowWrapper.layer?.shadowOpacity = 0.22
            shadowWrapper.layer?.shadowRadius = 14
            shadowWrapper.layer?.shadowOffset = CGSize(width: 0, height: -6)

            let visual = NSVisualEffectView()
            visual.material = .popover
            visual.blendingMode = .behindWindow
            visual.state = .active
            visual.wantsLayer = true
            visual.layer?.cornerRadius = 24
            visual.layer?.cornerCurve = .continuous
            visual.layer?.masksToBounds = true
            visual.layer?.borderWidth = 0.5
            visual.layer?.borderColor = NSColor.separatorColor.withAlphaComponent(0.35).cgColor

            pin(hosting, inside: visual)
            pin(visual, inside: shadowWrapper)
            cardContainer = shadowWrapper
        }

        cardContainer.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(cardContainer)
        NSLayoutConstraint.activate([
            cardContainer.topAnchor.constraint(equalTo: container.topAnchor, constant: Self.padTop),
            cardContainer.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: Self.padHorizontal),
            cardContainer.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -Self.padHorizontal),
            cardContainer.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -Self.padBottom),
        ])
        container.cardView = cardContainer
        created.contentView = container

        self.hostingView = hosting
        self.panel = created
        return created
    }

    private func pin(_ child: NSView, inside parent: NSView) {
        child.translatesAutoresizingMaskIntoConstraints = false
        parent.addSubview(child)
        NSLayoutConstraint.activate([
            child.topAnchor.constraint(equalTo: parent.topAnchor),
            child.bottomAnchor.constraint(equalTo: parent.bottomAnchor),
            child.leadingAnchor.constraint(equalTo: parent.leadingAnchor),
            child.trailingAnchor.constraint(equalTo: parent.trailingAnchor),
        ])
    }

    private func openPanel() {
        let panel = ensurePanel()
        let fitting = hostingView?.fittingSize ?? NSSize(width: Self.cardWidth, height: 280)
        let cardHeight = max(120, ceil(fitting.height))
        let targetSize = NSSize(
            width: Self.panelWidth,
            height: cardHeight + Self.padTop + Self.padBottom
        )
        panel.setContentSize(targetSize)

        if let button = statusItem.button, let buttonWindow = button.window {
            let buttonInWindow = button.convert(button.bounds, to: nil)
            let buttonScreen = buttonWindow.convertToScreen(buttonInWindow)
            let visibleFrame = (buttonWindow.screen ?? NSScreen.main)?.visibleFrame
                ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
            let rawCardX = buttonScreen.midX - Self.cardWidth / 2
            let clampedCardX = min(
                max(rawCardX, visibleFrame.minX + 8),
                visibleFrame.maxX - Self.cardWidth - 8
            )
            let cardTopY = buttonScreen.minY - 6
            let panelTopY = cardTopY + Self.padTop
            let panelOriginX = clampedCardX - Self.padHorizontal
            let panelOriginY = panelTopY - targetSize.height
            panel.setFrameOrigin(NSPoint(x: round(panelOriginX), y: round(panelOriginY)))
        }

        if #available(macOS 14.0, *) {
            NSApp.activate()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }

        panel.alphaValue = 0
        panel.makeKeyAndOrderFront(nil)
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.14
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = 1
        }

        installEventMonitors()
        controller.handlePopoverOpened(launchAtLogin: launchAtLogin)
    }

    func closePanel() {
        removeEventMonitors()
        guard let panel, panel.isVisible else { return }
        lastClosedAt = Date()
        panel.orderOut(nil)
    }

    private func updatePanelSize(to size: CGSize) {
        guard let panel, panel.isVisible else { return }
        let targetHeight = max(120, ceil(size.height)) + Self.padTop + Self.padBottom
        let current = panel.frame
        guard abs(current.width - Self.panelWidth) > 1 || abs(current.height - targetHeight) > 1 else {
            return
        }
        let topY = current.maxY
        let newFrame = NSRect(
            x: current.origin.x,
            y: round(topY - targetHeight),
            width: Self.panelWidth,
            height: targetHeight
        )
        panel.setFrame(newFrame, display: true, animate: false)
    }

    private func installEventMonitors() {
        removeEventMonitors()

        globalClickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if let buttonWindow = self.statusItem.button?.window,
                   buttonWindow.frame.contains(NSEvent.mouseLocation)
                {
                    return
                }
                self.closePanel()
            }
        }

        localKeyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 { // Escape
                self?.closePanel()
                return nil
            }
            return event
        }
    }

    private func removeEventMonitors() {
        if let globalClickMonitor {
            NSEvent.removeMonitor(globalClickMonitor)
            self.globalClickMonitor = nil
        }
        if let localKeyMonitor {
            NSEvent.removeMonitor(localKeyMonitor)
            self.localKeyMonitor = nil
        }
    }

    // MARK: - NSWindowDelegate

    func windowDidResignKey(_ notification: Notification) {
        closePanel()
    }
}

private final class TransparentPanelContainerView: NSView {
    weak var cardView: NSView?
    var onOutsideClick: (() -> Void)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = NSColor.clear.cgColor
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if let cardView, !cardView.frame.contains(point) {
            onOutsideClick?()
            return
        }
        super.mouseDown(with: event)
    }
}

private final class FloatingMenuPanel: NSPanel {
    var onEscape: (() -> Void)?

    override var canBecomeKey: Bool { true }

    override func cancelOperation(_ sender: Any?) {
        onEscape?()
    }
}

private final class AutoSizingHostingView<Content: View>: NSHostingView<Content> {
    var onSizeChange: ((CGSize) -> Void)?
    private var lastReportedSize: CGSize = .zero

    override func layout() {
        super.layout()
        let size = fittingSize
        if abs(size.width - lastReportedSize.width) > 0.5 || abs(size.height - lastReportedSize.height) > 0.5 {
            lastReportedSize = size
            onSizeChange?(size)
        }
    }
}
