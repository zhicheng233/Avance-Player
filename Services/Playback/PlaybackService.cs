using System;

namespace Avance_Player.Services.Playback;

/// <summary>
/// Owns the current playback session and composes the low-level audio backend. Queue, repeat,
/// shuffle, A–B and sleep-timer rules stay in the view model; this service only guards the
/// session boundary and forwards backend events for the active session.
/// </summary>
public sealed class PlaybackService : IDisposable {
    private readonly IAudioBackend _backend;
    private PlaybackSession? _session;
    private double _volume = 0.72;
    private double _rate = 1.0;
    private bool _disposed;

    public PlaybackService(IAudioBackend backend) {
        _backend = backend;
        _backend.Ended += Backend_Ended;
        _backend.Failed += Backend_Failed;
    }

    public PlaybackSession? CurrentSession => _session;
    public bool IsLoaded => _session is not null;
    public PlaybackSnapshot Snapshot => _session is null
        ? new PlaybackSnapshot(false, TimeSpan.Zero, TimeSpan.Zero, false)
        : _backend.Snapshot;

    public event Action<PlaybackEnded>? Ended;
    public event Action<PlaybackFailure>? Failed;

    public AudioLoadResult Start(AudioSource source, TimeSpan startAt, bool autoplay) {
        if (_disposed) return AudioLoadResult.Fail("The playback service has been disposed.");

        var result = _backend.Load(source, startAt);
        if (!result.Success || result.Session is null) {
            _backend.Stop();
            _session = null;
            return result;
        }

        _session = result.Session;
        _backend.SetVolume(_volume);
        _backend.SetRate(_rate);
        if (autoplay) _backend.Play();
        return result;
    }

    public void Play() {
        if (_disposed || _session is null) return;
        _backend.Play();
    }

    public void Pause() {
        if (_disposed || _session is null) return;
        _backend.Pause();
    }

    public void Stop() {
        if (_disposed || _session is null) return;
        _backend.Stop();
        _session = null;
    }

    public void Seek(TimeSpan position) {
        if (_disposed || _session is null) return;
        _backend.Seek(position);
    }

    public void SetVolume(double volume) {
        _volume = Math.Clamp(volume, 0, 1);
        if (_disposed) return;
        _backend.SetVolume(_volume);
    }

    public void SetRate(double rate) {
        _rate = Math.Clamp(rate, 0.25, 4.0);
        if (_disposed) return;
        _backend.SetRate(_rate);
    }

    private void Backend_Ended(PlaybackEnded ended) {
        if (_disposed || _session is null || ended.Session.Id != _session.Id) return;
        Ended?.Invoke(ended);
    }

    private void Backend_Failed(PlaybackFailure failure) {
        if (_disposed || _session is null || failure.Session.Id != _session.Id) return;
        Failed?.Invoke(failure);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _backend.Ended -= Backend_Ended;
        _backend.Failed -= Backend_Failed;
        _session = null;
        _backend.Dispose();
    }
}
