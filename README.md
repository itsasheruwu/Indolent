# Indolent

Indolent is a WinUI 3 desktop app for Windows that wraps a local Codex CLI install with a widget-style interface. It checks whether Codex CLI is available, lets you choose a visible Codex model and reasoning level, and keeps a small desktop widget ready for quick answers.

## Stack

- .NET 10
- WinUI 3 / Windows App SDK
- WebView2
- H.NotifyIcon.WinUI

## Requirements

- Windows 10 version 19041 or later
- .NET 10 SDK
- A working Codex CLI install

## Run

```powershell
dotnet build .\Indolent.csproj
dotnet run --project .\Indolent.csproj
```

## Notes

- Build output folders like `bin/` and `obj/` are ignored in git.
- The `Widget concept art/` images are included in the repository as design references.
