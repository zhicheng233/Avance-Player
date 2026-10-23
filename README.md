# Avance Player

Avalonia desktop music player with local-folder import, real local playback through LibVLC,
synchronized word-by-word lyrics, a floating lyrics window, bilingual English/Chinese UI,
playback settings, and persistent state.

## Requirements

- .NET SDK 10.0
- Native LibVLC:
  - **Linux:** install the system `libvlc` package for your distribution.
  - **Windows / macOS:** the project references `VideoLAN.LibVLC.Windows` /
    `VideoLAN.LibVLC.Mac`, which bundle the native runtime.
- Optional: set `AVANCE_LIBVLC_PATH` to a directory containing a custom native LibVLC runtime.

## Build and run

```bash
dotnet build Avance-Player.slnx
dotnet run --project Avance-Player.csproj
```

If more than one .NET SDK is installed, use the matching executable explicitly, for example
`~/.dotnet/dotnet build Avance-Player.slnx`.

## Diagnostics

Run the console-only playback check without opening the UI:

```bash
dotnet run --project Avance-Player.csproj -- --diagnostics
```

It prints the LibVLC version, audio outputs and devices, and available audio filters. This is
useful on headless machines or when validating a native runtime installation.

## Publish

Publish profiles live in `Properties/PublishProfiles`:

| Profile | Runtime |
| --- | --- |
| `win-x64` | Windows x64 |
| `osx-x64` | macOS Intel |
| `osx-arm64` | macOS Apple Silicon |
| `linux-x64` | Linux x64 |
| `linux-arm64` | Linux ARM64 |

```bash
dotnet publish Avance-Player.csproj -p:PublishProfile=win-x64
dotnet publish Avance-Player.csproj -p:PublishProfile=osx-arm64
dotnet publish Avance-Player.csproj -p:PublishProfile=linux-x64
```

Output is written under `bin/Release/net10.0/<runtime>/publish/`.

The profiles publish a self-contained .NET runtime but keep single-file publishing and trimming
disabled, because Avalonia and LibVLC need their native assets alongside the managed assemblies.
Windows and macOS publish output includes the native LibVLC runtime; Linux publish output still
expects the system `libvlc` package. Installer packaging is not included yet.

If the target machine already has the .NET 10 runtime, a smaller framework-dependent publish is
also available:

```bash
dotnet publish Avance-Player.csproj -p:PublishProfile=linux-x64 -p:SelfContained=false
```

## Notes

- Built-in demo tracks use simulated playback and have no audio output.
- Imported local files use real LibVLC playback, seeking, volume, speed, and equalizer processing.
- The equalizer is a no-op for demo tracks.
- Player state is written to `<LocalApplicationData>/AvancePlayer/state.json`.
