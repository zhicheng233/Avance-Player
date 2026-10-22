using System;
using LibVLCSharp.Shared;

namespace Avance_Player.Services.Playback;

/// <summary>
/// Owns the native LibVLC initialization. It is lazy so a missing native library does not prevent
/// the demo library from starting; local playback reports the initialization error instead.
/// </summary>
public sealed class LibVlcRuntime : IDisposable {
    private static readonly string[] DefaultOptions = ["--no-video", "--quiet"];

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
        // Linux distributions usually place libvlc in the loader path. If the default lookup
        // fails, try the two common multiarch locations before giving up.
        try {
            Core.Initialize();
        }
        catch (Exception) when (OperatingSystem.IsLinux()) {
            TryInitialize("/usr/lib");
            TryInitialize("/usr/lib/x86_64-linux-gnu");
        }

        return new LibVLC(_options);
    }

    private static void TryInitialize(string directory) {
        try {
            Core.Initialize(directory);
        }
        catch (Exception) {
            // The caller reports the final failure from CreateLibVlc.
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
