using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Avance_Player.Services.Playback;

/// <summary>
/// 管原生 LibVLC 的初始化。懒加载，原生库没装也不耽误应用起来；
/// 本地播放失败时再报初始化错误。
/// </summary>
public sealed class LibVlcRuntime : IDisposable {
    private static readonly string[] DefaultOptions = ["--no-video", "--quiet"];
    private const string RuntimePathVariable = "AVANCE_LIBVLC_PATH";

    private readonly object _sync = new();
    private readonly string[] _options;
    private string[] _activeOptions;
    private LibVLC? _libVlc;
    private string? _error;
    private bool _disposed;

    public LibVlcRuntime(string[]? options = null) {
        _options = options ?? DefaultOptions;
        _activeOptions = _options;
    }

    public LibVLC? TryGet(out string? error) => TryGet(_activeOptions, out error);

    /// <summary>
    /// 返回一个带 <paramref name="options"/> 的 runtime。LibVLC 的选项（比如时间伸缩）
    /// 是整个实例的，改了就重建原生实例。调用前得把还引用旧实例的 media player dispose 掉。
    /// </summary>
    public LibVLC? TryGet(string[] options, out string? error) {
        lock (_sync) {
            if (_disposed) {
                error = "The LibVLC runtime has been disposed.";
                return null;
            }

            if (_libVlc is not null && OptionsEqual(_activeOptions, options)) {
                error = null;
                return _libVlc;
            }

            if (!OptionsEqual(_activeOptions, options)) {
                _libVlc?.Dispose();
                _libVlc = null;
                _activeOptions = options.ToArray();
                _error = null;
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
                _libVlc = CreateLibVlc(_activeOptions);
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

    private LibVLC CreateLibVlc(string[] options) {
        // 自带或者自定义的 runtime 优先于系统查找。便携版用得上，
        // 也方便把 libvlc 放在常规 loader 路径之外的人。
        var customPath = Environment.GetEnvironmentVariable(RuntimePathVariable);
        if (!string.IsNullOrWhiteSpace(customPath) && TryInitialize(customPath)) {
            return new LibVLC(options);
        }

        // 先找应用目录里的 runtime，再走系统默认，这样就算全局装了别的 libvlc 版本，
        // 便携版也稳定。
        var baseDirectory = AppContext.BaseDirectory;
        var architecture = RuntimeInformation.ProcessArchitecture;
        foreach (var directory in GetAppLocalDirectories(baseDirectory, architecture)) {
            if (Directory.Exists(directory) && TryInitialize(directory)) return new LibVLC(options);
        }

        Exception? lastError = null;
        try {
            Core.Initialize();
            return new LibVLC(options);
        }
        catch (Exception exception) {
            lastError = exception;
        }

        if (OperatingSystem.IsLinux()) {
            foreach (var directory in LinuxRuntimeDirectories) {
                if (Directory.Exists(directory) && TryInitialize(directory)) return new LibVLC(options);
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
            // 最终失败由调用方从 CreateLibVlc 报出去。
            return false;
        }
    }

    private static bool OptionsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++) {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        }
        return true;
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
