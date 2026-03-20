import IndolentKit
import Observation
import SwiftUI

struct MenuBarContentView: View {
    @Bindable var controller: AppController
    let delegate: AppDelegate

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Indolent")
                .font(.system(size: 16, weight: .semibold, design: .rounded))

            Text(controller.currentProvider.displayName)
                .font(.caption)
                .foregroundStyle(.secondary)

            Divider()

            Button("Open Settings") {
                delegate.openSettingsWindow()
            }
            Button("Show Widget") {
                delegate.showWidget()
            }
            Button("Refresh Status") {
                Task { await controller.refreshPreflight() }
            }

            Divider()

            Button("Quit") {
                NSApplication.shared.terminate(nil)
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct SettingsRootView: View {
    let controller: AppController
    let delegate: AppDelegate

    var body: some View {
        TabView {
            generalTab
                .tabItem {
                    Label("General", systemImage: "gearshape")
                }

            permissionsTab
                .tabItem {
                    Label("Permissions", systemImage: "lock.shield")
                }

            terminalTab
                .tabItem {
                    Label("Terminal", systemImage: "terminal")
                }
        }
        .padding(20)
    }

    private var generalTab: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Indolent")
                    .font(.title2)
                    .fontWeight(.semibold)
                Text("Manage providers, models, and widget behavior.")
                    .foregroundStyle(.secondary)
            }

            Form {
                Section("Status") {
                    LabeledContent("Provider") {
                        Text(controller.currentProvider.displayName)
                    }
                    LabeledContent("Version") {
                        Text(controller.appState.preflight.isInstalled ? controller.appState.preflight.version : "Not detected")
                    }
                    LabeledContent("Last Result") {
                        Text(controller.appState.lastAnswerSummary)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.trailing)
                    }

                    if !controller.appState.preflight.blockingMessage.isEmpty {
                        Text(controller.appState.preflight.blockingMessage)
                            .foregroundStyle(.red)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    HStack {
                        Button("Re-check") {
                            Task { await controller.refreshPreflight() }
                        }
                        if controller.currentProvider.providerID == .openCode {
                            Button("Guided Setup") {
                                Task { await controller.runGuidedSetup() }
                            }
                            .disabled(controller.isRunningGuidedSetup)
                        }
                        Button("Install Guide") {
                            controller.openInstallGuide()
                        }
                    }

                    if !controller.setupStatusText.isEmpty {
                        Text(controller.setupStatusText)
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                    }
                }

                Section("Provider") {
                    Picker("Provider", selection: Binding(
                        get: { controller.appState.selectedProviderID },
                        set: { newValue in
                            Task { await controller.setProvider(newValue) }
                        }
                    )) {
                        ForEach(controller.availableProviders) { provider in
                            Text(provider.displayName).tag(provider.id)
                        }
                    }

                    Picker("Model", selection: Binding(
                        get: { controller.appState.selectedModel },
                        set: { newValue in
                            Task { await controller.setModel(newValue) }
                        }
                    )) {
                        ForEach(controller.availableModels) { model in
                            Text(model.displayName).tag(model.slug)
                        }
                    }

                    if let selectedModel = controller.availableModels.first(where: { $0.slug == controller.appState.selectedModel }),
                       !selectedModel.description.isEmpty {
                        Text(selectedModel.description)
                            .foregroundStyle(.secondary)
                    }

                    if !controller.reasoningOptions.isEmpty {
                        Picker("Reasoning", selection: Binding(
                            get: { controller.appState.selectedReasoningEffort },
                            set: { newValue in
                                Task { await controller.setReasoning(newValue) }
                            }
                        )) {
                            ForEach(controller.reasoningOptions) { option in
                                Text(option.displayName).tag(option.effort)
                            }
                        }
                    }
                }

                Section("Widget") {
                    Toggle("Show widget on startup", isOn: Binding(
                        get: { controller.appState.startWithWidget },
                        set: { newValue in
                            controller.appState.startWithWidget = newValue
                            Task { await controller.persistSettings() }
                        }
                    ))
                    Toggle("Save current model on restart", isOn: Binding(
                        get: { controller.appState.saveCurrentModelOnRestart },
                        set: { newValue in
                            controller.appState.saveCurrentModelOnRestart = newValue
                            Task { await controller.persistSettings() }
                        }
                    ))
                    Toggle("Enable agent mode", isOn: Binding(
                        get: { controller.appState.agentModeEnabled },
                        set: { newValue in
                            controller.appState.agentModeEnabled = newValue
                            Task { await controller.persistSettings() }
                        }
                    ))
                    Toggle("Enable loop mode", isOn: Binding(
                        get: { controller.appState.agentLoopEnabled },
                        set: { newValue in
                            controller.appState.agentLoopEnabled = newValue
                            Task { await controller.persistSettings() }
                        }
                    ))
                }

                Section {
                    HStack {
                        Button("Show Widget") {
                            delegate.showWidget()
                        }
                        Button("Open Logs Folder") {
                            controller.openLogsDirectory()
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private var permissionsTab: some View {
        Form {
            Section("Privacy Access") {
                permissionRow(
                    title: "Screen Recording",
                    detail: "Required for capturing the display under the cursor.",
                    granted: controller.permissionService.hasScreenRecordingPermission(),
                    action: controller.requestScreenRecordingPermission
                )
                permissionRow(
                    title: "Accessibility",
                    detail: "Required for automated answer clicking in agent mode.",
                    granted: controller.permissionService.hasAccessibilityPermission(prompt: false),
                    action: controller.requestAccessibilityPermission
                )
            }
        }
        .formStyle(.grouped)
    }

    private var terminalTab: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Terminal")
                .font(.title3)
                .fontWeight(.semibold)

            HStack {
                TextField("Arguments", text: Binding(
                    get: { controller.terminalCommandText },
                    set: { controller.terminalCommandText = $0 }
                ))
                .textFieldStyle(.roundedBorder)

                Button("Run") {
                    Task { await controller.runTerminalCommand() }
                }
                .disabled(controller.isRunningTerminalCommand)

                Button("Clear") {
                    controller.clearTranscript()
                }
            }

            TextEditor(text: .constant(controller.terminalTranscript))
                .font(.system(.caption, design: .monospaced))
                .frame(minHeight: 280)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.secondary.opacity(0.2), lineWidth: 1))
                .clipShape(.rect(cornerRadius: 8))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func permissionRow(title: String, detail: String, granted: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(granted ? "Granted" : "Not granted")
                    .font(.caption)
                    .foregroundStyle(granted ? Color.secondary : Color.red)
            }
            Spacer()
            Button(granted ? "Re-check" : "Grant") {
                action()
            }
        }
    }
}

struct WidgetRootView: View {
    @Bindable var controller: AppController
    let delegate: AppDelegate

    var body: some View {
        let widget = controller.widgetCoordinator.viewModel

        ZStack(alignment: .bottom) {
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(Color(red: 0.10, green: 0.11, blue: 0.14).opacity(0.90))
                .background(
                    RoundedRectangle(cornerRadius: 18, style: .continuous)
                        .fill(.ultraThinMaterial.opacity(0.35))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 18, style: .continuous)
                        .stroke(widget.isHovered ? Color.white.opacity(0.20) : Color.white.opacity(0.10), lineWidth: 1)
                )

            HStack(spacing: 0) {
                ZStack {
                    Color.white.opacity(0.015)
                    VStack(spacing: 4) {
                        Capsule().fill(Color.white.opacity(0.14)).frame(width: 12, height: 2)
                        Capsule().fill(Color.white.opacity(0.14)).frame(width: 12, height: 2)
                        Capsule().fill(Color.white.opacity(0.14)).frame(width: 12, height: 2)
                    }
                }
                .frame(width: 38)

                Rectangle()
                    .fill(Color.white.opacity(0.08))
                    .frame(width: 1)
                    .padding(.vertical, 14)

                ZStack(alignment: .leading) {
                    content
                        .padding(.horizontal, 20)
                        .padding(.vertical, 10)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            }

            Rectangle()
                .fill(
                    LinearGradient(
                        colors: [
                            Color.blue.opacity(0.0),
                            Color(red: 0.23, green: 0.51, blue: 0.96),
                            Color.blue.opacity(0.0)
                        ],
                        startPoint: .leading,
                        endPoint: .trailing
                    )
                )
                .frame(height: 2)
                .opacity(widget.statusText.isEmpty ? 0 : 0.95)
        }
        .frame(width: 368, height: 72)
        .clipShape(.rect(cornerRadius: 18))
        .shadow(color: .black.opacity(0.28), radius: 24, x: 0, y: 14)
        .onHover { hovering in
            controller.widgetCoordinator.viewModel.isHovered = hovering
        }
        .contextMenu {
            Button("Open Settings") {
                delegate.openSettingsWindow()
            }
            Button("Force stop current answer", role: .destructive) {
                controller.widgetCoordinator.cancelCurrentAnswer()
            }
            .disabled(!controller.widgetCoordinator.isAnswerRunning)
            Button("Hide Widget") {
                delegate.hideWidget()
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        let widget = controller.widgetCoordinator.viewModel
        if !widget.messageText.isEmpty {
            Text(widget.messageText)
                .lineLimit(3)
                .font(.system(size: 14, weight: .regular, design: .default))
                .foregroundStyle(widget.isError ? Color(red: 1.0, green: 0.54, blue: 0.54) : Color.white.opacity(0.93))
        } else if !widget.statusText.isEmpty {
            HStack(spacing: 10) {
                ProgressView()
                    .controlSize(.small)
                    .tint(Color.white.opacity(0.78))
                Text(widget.statusText)
                    .font(.system(size: 15, weight: .light, design: .default))
                    .foregroundStyle(
                        widget.statusPhase == .extractingText || widget.statusPhase == .screenshotTaken
                            ? Color(red: 0.96, green: 0.85, blue: 0.43)
                            : Color.white.opacity(0.74)
                    )
            }
        } else if widget.showActionButton {
            Button("Answer") {
                Task { await controller.widgetCoordinator.triggerAnswer() }
            }
            .buttonStyle(.plain)
            .font(.system(size: 15, weight: .semibold, design: .default))
            .foregroundStyle(Color.white.opacity(0.92))
        } else {
            Text("Hover to answer")
                .font(.system(size: 14, weight: .regular, design: .default))
                .foregroundStyle(Color.white.opacity(0.46))
        }
    }
}
