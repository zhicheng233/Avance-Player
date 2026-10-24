using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Native;
using SharpHook.Providers;

namespace Avance_Player.Services.Input;

public enum GlobalHotkeyAction {
    TogglePlayback,
    PreviousTrack,
    NextTrack,
    VolumeUp,
    VolumeDown
}

/// <summary>
/// 全局快捷键，能挂就挂（Ctrl+Alt+P / 方向键）。SharpHook 包着各平台的原生钩子；
/// 会话不让挂（比如受限的 Wayland 合成器）时服务就闲着，窗口里的快捷键照样能用。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable {
    private readonly Action<GlobalHotkeyAction> _dispatch;
    private readonly SimpleGlobalHook? _hook;
    private readonly HashSet<KeyCode> _pressed = [];
    private bool _disposed;

    /// <summary>主窗口激活时更新；应用在前台时跳过全局处理。</summary>
    public volatile bool AppIsActive;

    public GlobalHotkeyService(Action<GlobalHotkeyAction> dispatch) {
        _dispatch = dispatch;
        try {
            _hook = new SimpleGlobalHook(GlobalHookType.Keyboard, UioHookProvider.Instance, true);
            _hook.KeyPressed += Hook_KeyPressed;
            _hook.KeyReleased += Hook_KeyReleased;
        }
        catch (Exception) {
            _hook = null;
        }
    }

    public bool IsRunning => !_disposed && _hook?.IsRunning == true;

    public void Start() {
        if (_disposed || _hook is null) return;
        _ = RunAsync();
    }

    private async Task RunAsync() {
        try {
            await _hook!.RunAsync().ConfigureAwait(false);
        }
        catch (Exception) {
            // 原生钩子不支持或者显示服务器不可用。窗口里的快捷键还是 MainWindow 管。
        }
    }

    private void Hook_KeyPressed(object? sender, KeyboardHookEventArgs e) {
        if (_disposed || AppIsActive) return;

        var mask = e.RawEvent.Mask;
        if (!mask.HasCtrl() || !mask.HasAlt()) return;

        GlobalHotkeyAction? action = e.Data.KeyCode switch {
            KeyCode.VcP => GlobalHotkeyAction.TogglePlayback,
            KeyCode.VcLeft => GlobalHotkeyAction.PreviousTrack,
            KeyCode.VcRight => GlobalHotkeyAction.NextTrack,
            KeyCode.VcUp => GlobalHotkeyAction.VolumeUp,
            KeyCode.VcDown => GlobalHotkeyAction.VolumeDown,
            _ => null
        };
        if (action is null) return;

        // Windows/macOS 支持吞键；Linux X11/Wayland 可能还是把键送给当前应用。
        // 上面的前台判断能避免重复处理。
        e.SuppressEvent = true;
        if (!_pressed.Add(e.Data.KeyCode)) return;

        try {
            _dispatch(action.Value);
        }
        catch (Exception) {
            // 播放命令再怎么样也不能把钩子线程搞崩。
        }
    }

    private void Hook_KeyReleased(object? sender, KeyboardHookEventArgs e) {
        _pressed.Remove(e.Data.KeyCode);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try {
            if (_hook is not null) {
                _hook.KeyPressed -= Hook_KeyPressed;
                _hook.KeyReleased -= Hook_KeyReleased;
                _hook.Dispose();
            }
        }
        catch (Exception) {
            // 原生关闭时的竞态，忽略。
        }
    }
}
