using System;

namespace Avance_Player.Services.Playback;

/// <summary>
/// Routes each source to the real or simulated backend. This is composition rather than a
/// backend inheritance hierarchy: the router owns both implementations and delegates the small
/// <see cref="IAudioBackend"/> surface.
/// </summary>
public sealed class SwitchableAudioBackend : IAudioBackend {
    private readonly LibVlcAudioBackend _local;
    private readonly SimulatedAudioBackend _simulated;
    private IAudioBackend? _active;
    private bool _disposed;

    public SwitchableAudioBackend(LibVlcAudioBackend local, SimulatedAudioBackend simulated) {
        _local = local;
        _simulated = simulated;
        _local.Ended += ForwardEnded;
        _local.Failed += ForwardFailed;
        _simulated.Ended += ForwardEnded;
        _simulated.Failed += ForwardFailed;
    }

    public bool IsAvailable => true;
    public string? AvailabilityError => _local.AvailabilityError;

    public event Action<PlaybackEnded>? Ended;
    public event Action<PlaybackFailure>? Failed;

    public AudioLoadResult Load(AudioSource source, TimeSpan startAt = default) {
        if (_disposed) return AudioLoadResult.Fail("The audio backend has been disposed.");

        if (source.IsLocalFile) {
            _simulated.Stop();
            // Stop the previous local track even if this load fails (missing file, native runtime
            // unavailable); otherwise the old audio would keep playing with no active session.
            _local.Stop();
            var result = _local.Load(source, startAt);
            _active = result.Success ? _local : null;
            return result;
        }

        _local.Stop();
        var simulatedResult = _simulated.Load(source, startAt);
        _active = simulatedResult.Success ? _simulated : null;
        return simulatedResult;
    }

    public void Play() => _active?.Play();
    public void Pause() => _active?.Pause();
    public void Stop() => _active?.Stop();
    public void Seek(TimeSpan position) => _active?.Seek(position);
    public void SetVolume(double volume) => _active?.SetVolume(volume);
    public void SetRate(double rate) => _active?.SetRate(rate);

    public PlaybackSnapshot Snapshot => _active?.Snapshot
        ?? new PlaybackSnapshot(false, TimeSpan.Zero, TimeSpan.Zero, false);

    private void ForwardEnded(PlaybackEnded ended) => Ended?.Invoke(ended);
    private void ForwardFailed(PlaybackFailure failure) => Failed?.Invoke(failure);

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        _local.Ended -= ForwardEnded;
        _local.Failed -= ForwardFailed;
        _simulated.Ended -= ForwardEnded;
        _simulated.Failed -= ForwardFailed;

        _active = null;
        _local.Dispose();
        _simulated.Dispose();
    }
}
