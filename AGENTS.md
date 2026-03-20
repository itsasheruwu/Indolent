# Indolent Agent Notes

## Project
- WinUI 3 desktop app targeting `.NET 10` on Windows.
- Purpose: wrap a local Codex CLI install with a small widget UI and main settings window.
- Packaging model: unpackaged (`<WindowsPackageType>None</WindowsPackageType>`).

## macOS
- If you are working on the macOS version under `macOS/`, also read [`macOS/AGENTS.md`](macOS/AGENTS.md) before making changes there.

## Important Paths
- `App.xaml` / `App.xaml.cs`: app startup and service wiring.
- `MainWindow.xaml` / `WidgetWindow.xaml`: main app surfaces.
- `ViewModels/`: presentation logic for both windows.
- `Services/`: Codex CLI integration, OCR, capture, tray, settings, and model catalog logic.
- `Models/`: request/response/settings types.
- `Styles/`: shared WinUI resources and widget styling.
- `Assets/`: app icons, logos, and cursors.

## Working Rules
- Preserve the current WinUI + MVVM-ish structure; keep UI logic out of XAML code-behind unless it is window-specific behavior.
- Prefer extending existing services and view models over adding new top-level patterns or dependencies.
- Do not edit `bin/`, `obj/`, or the `*verify*` generated artifacts.

## Verify
```powershell
dotnet build .\Indolent.csproj
dotnet run --project .\Indolent.csproj
```
