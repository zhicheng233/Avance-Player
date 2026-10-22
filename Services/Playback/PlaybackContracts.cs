using System;
using System.Threading;

namespace Avance_Player.Services.Playback;

/// <summary>
/// A playable source. Demo tracks use relative paths and are handled by the simulated backend;
/// imported files use absolute paths and are handled by LibVLC.
/// </summary>
public sealed record AudioSource(string FilePath, TimeSpan Duration, bool IsLocalFile);

public sealed record PlaybackSession(long Id, AudioSource Source);

internal static class PlaybackSessionIds {
    private static long _next;

    public static long Next() => Interlocked.Increment(ref _next);
}

public enum PlaybackEndReason {
    Completed,
    Stopped,
    Error
}

public sealed record PlaybackEnded(PlaybackSession Session, PlaybackEndReason Reason);

public sealed record PlaybackFailure(PlaybackSession Session, string Message);

public sealed record PlaybackSnapshot(
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsAtEnd);

public sealed record AudioLoadResult(PlaybackSession? Session, string? Error) {
    public bool Success => Session is not null;

    public static AudioLoadResult Ok(PlaybackSession session) => new(session, null);
    public static AudioLoadResult Fail(string error) => new(null, error);
}

/// <summary>
/// Low-level audio I/O. Implementations are composed by <see cref="PlaybackService"/>; this
/// interface is deliberately small so the demo clock and LibVLC can be swapped without a
/// backend class hierarchy.
/// </summary>
public interface IAudioBackend : IDisposable {
    bool IsAvailable { get; }
    string? AvailabilityError { get; }
    AudioLoadResult Load(AudioSource source, TimeSpan startAt = default);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(double volume);
    void SetRate(double rate);
    PlaybackSnapshot Snapshot { get; }
    event Action<PlaybackEnded>? Ended;
    event Action<PlaybackFailure>? Failed;
}
