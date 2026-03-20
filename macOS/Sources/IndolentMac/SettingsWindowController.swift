import AppKit
import IndolentKit
import SwiftUI

final class SettingsWindowController: NSWindowController {
    init(controller: AppController, delegate: AppDelegate) {
        let contentView = SettingsRootView(controller: controller, delegate: delegate)
        let hosting = NSHostingView(rootView: contentView)
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 860, height: 720),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Indolent Settings"
        window.contentView = hosting
        window.isReleasedWhenClosed = false
        window.center()

        super.init(window: window)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        nil
    }

    func show() {
        guard let window else { return }
        window.makeKeyAndOrderFront(nil)
    }
}
