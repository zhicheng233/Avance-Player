using System;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// macOS Now Playing 集成。Objective-C 桥挂了大不了没有系统集成：MediaPlayer 框架或者远程命令 selector
/// 不可用时，播放照常，只是没有系统集成。
/// </summary>
public sealed class MacNowPlayingSystemMediaBackend : ISystemMediaBackend {
    private readonly MacNowPlayingNative? _native;

    public MacNowPlayingSystemMediaBackend() {
        try {
            _native = OperatingSystem.IsMacOS() ? new MacNowPlayingNative() : null;
        }
        catch (Exception) {
            _native = null;
        }
    }

    public bool IsAvailable => _native?.IsAvailable == true;
    public event Action<SystemMediaCommand>? CommandReceived {
        add {
            if (_native is not null) _native.CommandReceived += value;
        }
        remove {
            if (_native is not null) _native.CommandReceived -= value;
        }
    }

    public void Update(SystemMediaSnapshot snapshot) {
        try {
            _native?.Update(snapshot);
        }
        catch (Exception) {
            // OS 集成，挂了也不影响播放。
        }
    }

    public void Clear() {
        try {
            _native?.Clear();
        }
        catch (Exception) {
            // OS 集成，挂了也不影响播放。
        }
    }

    public void Dispose() {
        try {
            _native?.Dispose();
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }
}
