using System;
using System.Collections.Generic;
using System.Threading;

namespace Avance_Player.Services.Playback;

/// <summary>
/// 可播放源。老的 demo 源是相对路径，走模拟后端；导入的文件是绝对路径，走 LibVLC。
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

/// <summary>
/// 五个归一化的 UI 频段（0..1，0.5 是平直）。LibVLC 后端映射到原生十段；
/// 模拟后端故意不管。
/// </summary>
public sealed record EqualizerSettings(IReadOnlyList<double> BandLevels) {
    public static EqualizerSettings Flat { get; } =
        new(Array.AsReadOnly(new[] { 0.5, 0.5, 0.5, 0.5, 0.5 }));
}

public sealed record AudioLoadResult(PlaybackSession? Session, string? Error) {
    public bool Success => Session is not null;

    public static AudioLoadResult Ok(PlaybackSession session) => new(session, null);
    public static AudioLoadResult Fail(string error) => new(null, error);
}

/// <summary>
/// 底层音频 I/O。由 <see cref="PlaybackService"/> 组合；接口故意做小，
/// 模拟时钟和 LibVLC 能直接换，不用搞后端继承树。
/// </summary>
public interface IAudioBackend : IDisposable {
    bool IsAvailable { get; }
    string? AvailabilityError { get; }

    /// <summary>后端能不能在变速之外单独移调。</summary>
    bool SupportsPitchShift { get; }

    AudioLoadResult Load(AudioSource source, TimeSpan startAt = default);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(double volume);
    void SetRate(double rate);
    void SetPitch(double semitones, bool preservePitch);
    void SetEqualizer(EqualizerSettings settings);
    PlaybackSnapshot Snapshot { get; }
    event Action<PlaybackEnded>? Ended;
    event Action<PlaybackFailure>? Failed;
}
