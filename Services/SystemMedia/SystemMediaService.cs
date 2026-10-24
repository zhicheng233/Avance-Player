using System;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// 平台媒体集成外面包一层。应用其他部分只认这一小块，
/// 桌面服务缺了（无头会话、不支持的系统）就当没事，不会崩。
/// </summary>
public sealed class SystemMediaService : IDisposable {
    private readonly ISystemMediaBackend _backend;
    private bool _disposed;

    public SystemMediaService(ISystemMediaBackend backend) {
        _backend = backend;
        _backend.CommandReceived += Backend_CommandReceived;
    }

    public bool IsAvailable => !_disposed && _backend.IsAvailable;
    public event Action<SystemMediaCommand>? CommandReceived;

    public void Update(SystemMediaSnapshot snapshot) {
        if (_disposed) return;
        try {
            _backend.Update(snapshot);
        }
        catch (Exception) {
            // OS 集成挂了也没事；桌面媒体服务没了或者当前会话用不了，播放照样得跑。
        }
    }

    public void Clear() {
        if (_disposed) return;
        try {
            _backend.Clear();
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }

    private void Backend_CommandReceived(SystemMediaCommand command) {
        if (_disposed) return;
        try {
            CommandReceived?.Invoke(command);
        }
        catch (Exception) {
            // 调用方有 bug 也不能把 OS 媒体线程带崩。
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _backend.CommandReceived -= Backend_CommandReceived;
        try {
            _backend.Dispose();
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }

    public static SystemMediaService CreateNull() => new(new NullSystemMediaBackend());

    public static SystemMediaService CreateDefault() {
        ISystemMediaBackend backend;
        if (OperatingSystem.IsLinux()) {
            backend = new LinuxMprisSystemMediaBackend();
        }
        else if (OperatingSystem.IsMacOS()) {
            backend = new MacNowPlayingSystemMediaBackend();
        }
#if WINDOWS
        else if (OperatingSystem.IsWindows()) {
            backend = new WindowsSmtcSystemMediaBackend();
        }
#endif
        else {
            backend = new NullSystemMediaBackend();
        }

        return new SystemMediaService(backend);
    }

    private sealed class NullSystemMediaBackend : ISystemMediaBackend {
        public bool IsAvailable => false;
        public event Action<SystemMediaCommand>? CommandReceived { add { } remove { } }
        public void Update(SystemMediaSnapshot snapshot) { }
        public void Clear() { }
        public void Dispose() { }
    }
}
