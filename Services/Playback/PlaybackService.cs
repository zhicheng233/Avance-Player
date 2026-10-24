using System;

namespace Avance_Player.Services.Playback;

/// <summary>
/// 管当前播放会话，组合底层音频后端。队列、循环、随机、A–B、定时关闭的规则都在 view model；
/// 这个服务只管会话边界，把当前会话的后端事件转出去。
/// </summary>
public sealed class PlaybackService : IDisposable {
    private readonly IAudioBackend _backend;
    private PlaybackSession? _session;
    private double _volume = 0.72;
    private double _rate = 1.0;
    private double _pitchSemitones;
    private bool _preservePitch = true;
    private EqualizerSettings _equalizer = EqualizerSettings.Flat;
    private bool _disposed;

    public PlaybackService(IAudioBackend backend) {
        _backend = backend;
        _backend.Ended += Backend_Ended;
        _backend.Failed += Backend_Failed;
    }

    public PlaybackSession? CurrentSession => _session;
    public bool IsLoaded => _session is not null;
    public bool SupportsPitchShift => _backend.SupportsPitchShift;
    public PlaybackSnapshot Snapshot => _session is null
        ? new PlaybackSnapshot(false, TimeSpan.Zero, TimeSpan.Zero, false)
        : _backend.Snapshot;

    public event Action<PlaybackEnded>? Ended;
    public event Action<PlaybackFailure>? Failed;

    public AudioLoadResult Start(AudioSource source, TimeSpan startAt, bool autoplay) {
        if (_disposed) return AudioLoadResult.Fail("The playback service has been disposed.");

        // 音调/时间伸缩会改原生 runtime 选项，必须在 media 加载前应用。
        // 后端这里只是先存着。
        _backend.SetPitch(_pitchSemitones, _preservePitch);
        var result = _backend.Load(source, startAt);
        if (!result.Success || result.Session is null) {
            _backend.Stop();
            _session = null;
            return result;
        }

        _session = result.Session;
        _backend.SetVolume(_volume);
        _backend.SetRate(_rate);
        _backend.SetPitch(_pitchSemitones, _preservePitch);
        _backend.SetEqualizer(_equalizer);
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

    /// <summary>
    /// 应用音调同步和移调偏好。时间伸缩是原生 runtime 选项，开关时会在同一位置重载当前本地 media。
    /// 手动移调只有后端真的支持时才重载。
    /// </summary>
    public void SetPitch(double semitones, bool preservePitch) {
        var nextSemitones = Math.Clamp(semitones, -12, 12);
        var previousSemitones = _pitchSemitones;
        var syncChanged = _preservePitch != preservePitch;
        var pitchChanged = Math.Abs(previousSemitones - nextSemitones) > 0.001;
        _pitchSemitones = nextSemitones;
        _preservePitch = preservePitch;
        if (_disposed) return;

        _backend.SetPitch(_pitchSemitones, _preservePitch);
        if (_session is not { Source.IsLocalFile: true } session
            || (!syncChanged && !(pitchChanged && _backend.SupportsPitchShift))) {
            return;
        }

        var snapshot = _backend.Snapshot;
        Start(session.Source, snapshot.Position, snapshot.IsPlaying);
    }

    public void SetEqualizer(EqualizerSettings settings) {
        _equalizer = settings;
        if (_disposed) return;
        _backend.SetEqualizer(_equalizer);
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
