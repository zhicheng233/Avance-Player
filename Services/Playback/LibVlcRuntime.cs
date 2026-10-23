using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Avance_Player.Services.Playback;

/// <summary>
/// Owns the native LibVLC initialization. It is lazy so a missing native library does not prevent
/// the demo library from starting; local playback reports the initialization error instead.
/// </summary>
public sealed class LibVlcRuntime : IDisposable {
    private static readonly string[] DefaultOptions = ["--no-video", "--quiet"];
    private const string RuntimePathVariable = "AVANCE_LIBVLC_PATH";

    private readonly object _sync = new();
    private readonly string[] _options;
    private LibVLC? _libVlc;
    private string? _error;
    private bool _disposed;

    public LibVlcRuntime(string[]? options = null) {
        _options = options ?? DefaultOptions;
    }

    public LibVLC? TryGet(out string? error) {
        lock (_sync) {
            if (_disposed) {
                error = "The LibVLC runtime has been disposed.";
                return null;
            }

            if (_libVlc is not null) {
                error = null;
                return _libVlc;
            }

            if (_error is not null) {
                error = _error;
                return null;
            }

            try {
                _libVlc = CreateLibVlc();
                _error = null;
                error = null;
                return _libVlc;
            }
            catch (Exception exception) {
                _error = exception.Message;
                error = _error;
                return null;
            }
        }
    }

    private LibVLC CreateLibVlc() {
        // A bundled or custom runtime wins over the system lookup. This is useful for portable
        // builds and for users who keep libvlc outside the normal loader path.
        var customPath = Environment.GetEnvironmentVariable(RuntimePathVariable);
        if (!string.IsNullOrWhiteSpace(customPath) && TryInitialize(customPath)) {
            return new LibVLC(_options);
        }

        // Prefer an app-local runtime before the default system lookup, so a portable build is
        // reproducible even when a different libvlc version is installed globally.
        var baseDirectory = AppContext.BaseDirectory;
        var architecture = RuntimeInformation.ProcessArchitecture;
        foreach (var directory in GetAppLocalDirectories(baseDirectory, architecture)) {
            if (Directory.Exists(directory) && TryInitialize(directory)) return new LibVLC(_options);
        }

        Exception? lastError = null;
        try {
            Core.Initialize();
            return new LibVLC(_options);
        }
        catch (Exception exception) {
            lastError = exception;
        }

        if (OperatingSystem.IsLinux()) {
            foreach (var directory in LinuxRuntimeDirectories) {
                if (Directory.Exists(directory) && TryInitialize(directory)) return new LibVLC(_options);
            }
        }

        throw new InvalidOperationException(
            "LibVLC native runtime was not found. Install libvlc, place it in the app's libvlc folder, " +
            $"or set {RuntimePathVariable} to the directory containing the native libraries.",
            lastError);
    }

    private static IEnumerable<string> GetAppLocalDirectories(string baseDirectory, Architecture architecture) {
        yield return Path.Combine(baseDirectory, "libvlc");

        if (OperatingSystem.IsWindows()) {
            var windowsArchitecture = architecture switch {
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };
            yield return Path.Combine(baseDirectory, "libvlc", windowsArchitecture);
            yield break;
        }

        if (OperatingSystem.IsMacOS()) {
            yield return Path.Combine(baseDirectory, "libvlc", "osx");
            if (architecture == Architecture.Arm64) yield return Path.Combine(baseDirectory, "libvlc", "osx-arm64");
            yield break;
        }

        if (!OperatingSystem.IsLinux()) yield break;

        var linuxArchitecture = architecture switch {
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };
        yield return Path.Combine(baseDirectory, "libvlc", $"linux-{linuxArchitecture}");
    }

    private static readonly string[] LinuxRuntimeDirectories = [
        "/usr/lib",
        "/usr/lib64",
        "/usr/local/lib",
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/aarch64-linux-gnu",
        "/usr/lib/arm-linux-gnueabihf"
    ];

    private static bool TryInitialize(string directory) {
        try {
            Core.Initialize(directory);
            return true;
        }
        catch (Exception) {
            // The caller reports the final failure from CreateLibVlc.
            return false;
        }
    }

    public void Dispose() {
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            _libVlc?.Dispose();
            _libVlc = null;
        }
    }
}
