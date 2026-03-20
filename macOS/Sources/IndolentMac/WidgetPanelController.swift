import AppKit
import IndolentKit
import SwiftUI

final class WidgetPanelController: NSWindowController {
    init(controller: AppController, delegate: AppDelegate) {
        let contentView = WidgetRootView(controller: controller, delegate: delegate)
        let hosting = NSHostingView(rootView: contentView)
        let panel = NSPanel(
            contentRect: controller.appState.widgetBounds.rect,
            styleMask: [.borderless, .nonactivatingPanel, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.level = .floating
        panel.isFloatingPanel = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isMovableByWindowBackground = true
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.contentView = hosting

        super.init(window: panel)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        nil
    }

    func show() {
        guard let window else { return }
        window.orderFrontRegardless()
    }

    func hide() {
        window?.orderOut(nil)
    }

    func bringToFront() {
        guard let window else { return }
        window.orderFrontRegardless()
    }
}
