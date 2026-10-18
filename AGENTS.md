# Avance Player

## Current state and entrypoints
- This is a single-project C# Avalonia desktop app. `Program.cs` configures the desktop lifetime; `App.axaml.cs` creates `MainWindow`; `MainWindow.axaml` hosts the navigation shell and typed page templates.
- The current UI is a simulated prototype: `ViewModels/MainWindowViewModel.cs` owns navigation and playback demo state; `Models/` contains sample music, playlists, and lyric lines. There is no real audio backend, file scanning, persistence, or test project yet.
- `Avance-Player.csproj` targets `net10.0` and references Avalonia 12.1.4 (Fluent theme, Inter font). Keep demo-only behavior clearly separated from future real library/playback services.

## Build and verification
- .NET SDK 10.0.402 is installed under `~/.dotnet`, but `/usr/bin/dotnet` resolves to a separate SDK 9 installation. Use `~/.dotnet/dotnet build Avance-Player.slnx` (or put `~/.dotnet` first on `PATH`); run with `~/.dotnet/dotnet run --project Avance-Player.csproj` in a desktop session.
- There are currently no tests, lint scripts, CI workflows, or pinned SDK in this repo. Do not lower `net10.0` to work around the system `dotnet` path selecting SDK 9.

## Product scope and remaining work
- The prototype demonstrates OOP music/playlist models, library browsing, playlist edits, playback controls/history, and timestamped lyrics. Replace in-memory demo tracks and the UI timer with cross-platform services before treating these as production features.
- Keep the polished Avalonia UI, fluid ambient background, and animated lyric highlighting; do not copy Apple Music assets or introduce Windows-only WPF APIs.

## C# style and design references
- Follow this repo's file-scoped namespaces and same-line opening braces for new code. The author's other projects do **not** share one brace convention (`WhereMyWinCursor` uses same-line; `PN532-Aime-Reader` uses next-line); favor local consistency over assuming a global style.
- For substantive work, consult the author's C# code at https://github.com/zhicheng233: `WhereMyWinCursor` separates typed config/persistence and XAML/ViewModel UI state; `PN532-Aime-Reader` isolates device I/O behind an interface with a replay implementation. Keep library/playback logic out of Avalonia window code-behind and isolate the eventual audio backend; do not import Windows-only WPF APIs.
