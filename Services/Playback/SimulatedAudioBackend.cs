using System;
using System.Threading;

namespace Avance_Player.Services.Playback;

/// <summary>
/// Clock-only backend for the demo library. It advances from a <see cref="TimeProvider"/> so the
/// view model can keep the same queue, A–B and sleep-timer rules it already had without pretending
/// that a demo track has real audio.
/// </summary>
public sealed class SimulatedAudioBackend : IAudioBackend {
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private PlaybackSession? _session;
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private double _rate = 1.0;
    private bool _isPlaying;
    private bool _ended;
    private bool _disposed;

    public SimulatedAudioBackend(TimeProvider? timeProvider = null) {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _anchorTimestamp = _timeProvider.GetTimestamp();
        _timer = _timeProvider.CreateTimer(
            OnTimerTick,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public bool IsAvailable => true;
    public string? AvailabilityError => null;

    public event Action<PlaybackEnded>? Ended;

    // The simulated backend has no native failure path. Keep the interface contract without
    // raising a compiler warning for an event that is intentionally never used.
    public event Action<PlaybackFailure>? Failed {
        add { }
        remove { }
    }

    public AudioLoadResult Load(AudioSource source, TimeSpan startAt = default) {
        lock (_sync) {
            if (_disposed) return AudioLoadResult.Fail("The simulated audio backend has been disposed.");
            StopTimerLocked();
            _session = new PlaybackSession(PlaybackSessionIds.Next(), source);
            var duration = source.Duration;
            _anchorPosition = startAt < TimeSpan.Zero
                ? TimeSpan.Zero
                : startAt > duration ? duration : startAt;
            _anchorTimestamp = _timeProvider.GetTimestamp();
            _isPlaying = false;
            _ended = duration > TimeSpan.Zero && _anchorPosition >= duration;
            return AudioLoadResult.Ok(_session);
        }
    }

    public void Play() {
        lock (_sync) {
            if (_disposed || _session is null) return;

            if (_ended || CurrentPositionLocked() >= _session.Source.Duration) {
                _anchorPosition = TimeSpan.Zero;
                _anchorTimestamp = _timeProvider.GetTimestamp();
                _ended = false;
            }
            else {
                CapturePositionLocked();
            }

            _isPlaying = true;
            _timer.Change(TickInterval, TickInterval);
        }
    }

    public void Pause() {
        lock (_sync) {
            if (!_isPlaying) return;
            CapturePositionLocked();
            _isPlaying = false;
            StopTimerLocked();
        }
    }

    public void Stop() {
        lock (_sync) {
            _isPlaying = false;
            _ended = false;
            _anchorPosition = TimeSpan.Zero;
            _anchorTimestamp = _timeProvider.GetTimestamp();
            StopTimerLocked();
        }
    }

    public void Seek(TimeSpan position) {
        lock (_sync) {
            if (_session is null) return;
            var duration = _session.Source.Duration;
            var clamped = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > duration ? duration : position;
            _anchorPosition = clamped;
            _anchorTimestamp = _timeProvider.GetTimestamp();
            _ended = duration > TimeSpan.Zero && clamped >= duration;
        }
    }

    public void SetVolume(double volume) {
        // The demo backend has no output. Volume is still stored by PlaybackService so switching
        // from a demo track to a local track keeps the user's chosen level.
    }

    public void SetRate(double rate) {
        lock (_sync) {
            var next = Math.Clamp(rate, 0.25, 4.0);
            if (Math.Abs(_rate - next) < 0.001) return;
            CapturePositionLocked();
            _rate = next;
        }
    }

    public PlaybackSnapshot Snapshot {
        get {
            lock (_sync) {
                if (_session is null) {
                    return new PlaybackSnapshot(false, TimeSpan.Zero, TimeSpan.Zero, false);
                }

                var duration = _session.Source.Duration;
                var position = CurrentPositionLocked();
                if (duration > TimeSpan.Zero && position > duration) position = duration;
                var atEnd = duration > TimeSpan.Zero && position >= duration;
                return new PlaybackSnapshot(_isPlaying, position, duration, atEnd);
            }
        }
    }

    private void OnTimerTick(object? state) {
        PlaybackSession? endedSession = null;
        lock (_sync) {
            if (_disposed || !_isPlaying || _session is null) return;

            var duration = _session.Source.Duration;
            var position = CurrentPositionLocked();
            if (duration > TimeSpan.Zero && position >= duration) {
                _anchorPosition = duration;
                _anchorTimestamp = _timeProvider.GetTimestamp();
                _isPlaying = false;
                _ended = true;
                StopTimerLocked();
                endedSession = _session;
            }
        }

        if (endedSession is not null) {
            Ended?.Invoke(new PlaybackEnded(endedSession, PlaybackEndReason.Completed));
        }
    }

    private TimeSpan CurrentPositionLocked() {
        if (!_isPlaying) return _anchorPosition;
        var elapsed = _timeProvider.GetElapsedTime(_anchorTimestamp);
        return _anchorPosition + TimeSpan.FromTicks((long)(elapsed.Ticks * _rate));
    }

    private void CapturePositionLocked() {
        if (!_isPlaying) return;
        _anchorPosition = CurrentPositionLocked();
        _anchorTimestamp = _timeProvider.GetTimestamp();
    }

    private void StopTimerLocked() {
        if (_disposed) return;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() {
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            _isPlaying = false;
            _session = null;
            StopTimerLocked();
        }

        _timer.Dispose();
    }
}
