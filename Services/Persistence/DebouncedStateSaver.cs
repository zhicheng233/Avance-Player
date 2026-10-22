using System;
using System.Threading;

namespace Avance_Player.Services.Persistence;

/// <summary>
/// Coalesces frequent state snapshots into one write. The store itself stays synchronous and
/// easy to test; this class only adds scheduling and a final flush for shutdown.
/// </summary>
public sealed class DebouncedStateSaver : IDisposable {
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(750);

    private readonly object _sync = new();
    private readonly IPlayerStateStore _store;
    private readonly Timer _timer;
    private PlayerState? _pending;
    private bool _disposed;

    public DebouncedStateSaver(IPlayerStateStore store) {
        _store = store;
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<string>? SaveFailed;

    public void Schedule(PlayerState state) {
        lock (_sync) {
            if (_disposed) return;
            _pending = state;
            _timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush(PlayerState? state = null) {
        string? error;
        lock (_sync) {
            if (state is not null) _pending = state;
            var pending = _pending;
            _pending = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // Save while holding the scheduling lock so a timer callback cannot write an older
            // snapshot after this newer flush.
            error = pending is null ? null : SaveLocked(pending);
        }

        if (error is not null) SaveFailed?.Invoke(error);
    }

    private void OnTimer(object? state) {
        string? error;
        lock (_sync) {
            if (_disposed) return;
            var pending = _pending;
            _pending = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            error = pending is null ? null : SaveLocked(pending);
        }

        if (error is not null) SaveFailed?.Invoke(error);
    }

    private string? SaveLocked(PlayerState state) {
        try {
            return _store.Save(state) ? null : _store.LastError ?? "Unknown state save error.";
        }
        catch (Exception exception) {
            return exception.Message;
        }
    }

    public void Dispose() {
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Flush();
        _timer.Dispose();
    }
}
