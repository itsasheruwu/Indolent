import AppKit
import IndolentKit
import Observation
import SwiftUI

@main
struct IndolentMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra("Indolent", systemImage: "bolt.circle.fill") {
            MenuBarContentView(controller: appDelegate.controller, delegate: appDelegate)
                .frame(width: 280)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsRootView(controller: appDelegate.controller, delegate: appDelegate)
                .frame(minWidth: 860, minHeight: 720)
                .task {
                    await appDelegate.controller.start()
                }
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, WidgetWindowing {
    private(set) var controller: AppController!
    private var widgetPanelController: WidgetPanelController?
    private var settingsWindowController: SettingsWindowController?

    override init() {
        super.init()
        let appState = AppState()
        let settingsStore = JSONSettingsStore()
        let runner = ShellCommandRunner()
        let permissions = MacPermissionService()
        let codexProvider = CodexProviderRuntime(runner: runner, workingDirectory: FileManager.default.currentDirectoryPath)
        let openCodeProvider = OpenCodeProviderRuntime(runner: runner, workingDirectory: FileManager.default.currentDirectoryPath)
        let registry = ProviderRuntimeRegistry(runtimes: [codexProvider, openCodeProvider])
        let clickService = QuartzMouseClickService()
        let agentClickService = AgentClickService(appState: appState, providerRegistry: registry, clickPerformer: clickService)
        let widgetCoordinator = WidgetAnswerCoordinator(
            appState: appState,
            settingsStore: settingsStore,
            providerRegistry: registry,
            screenCaptureService: ScreenCaptureService(),
            ocrService: VisionOCRService(),
            permissionService: permissions,
            agentClickService: agentClickService
        )

        controller = AppController(
            appState: appState,
            settingsStore: settingsStore,
            providerRegistry: registry,
            openCodeSetupService: OpenCodeSetupService(runner: runner),
            permissionService: permissions,
            widgetCoordinator: widgetCoordinator
        )
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        let panel = WidgetPanelController(controller: controller, delegate: self)
        widgetPanelController = panel
        controller.widgetCoordinator.attachWindowing(self)

        Task { @MainActor in
            await controller.start()
            if controller.appState.startWithWidget {
                showWidget()
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func showWidget() {
        widgetPanelController?.show()
    }

    func hideWidget() {
        widgetPanelController?.hide()
    }

    func bringToFront() {
        widgetPanelController?.bringToFront()
    }

    func openSettingsWindow() {
        NSApp.activate(ignoringOtherApps: true)
        if settingsWindowController == nil {
            settingsWindowController = SettingsWindowController(controller: controller, delegate: self)
        }
        settingsWindowController?.show()
    }
}
