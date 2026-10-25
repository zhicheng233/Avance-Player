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

    // 重载媒体（音调/均衡器）之后，后端要过一会儿才把位置和播放状态报回来。
    // 这段时间里再读快照只会拿到 0 秒 + 未播放，紧接着的第二次重载就会把播放掐掉。
    // 记下上一次重载请求的位置和状态，作为短时间内的兜底。
    private TimeSpan _reloadPosition;
    private bool _reloadPlaying;
    private DateTime _reloadGuardUntilUtc = DateTime.MinValue;

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
    /// 手动移调也是重载才生效：音调滤镜链写在 MediaPlayer 上，播放中的 aout 已经被 input 占着，
    /// 只重建输出不会换链。
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
        if (_session is not { Source.IsLocalFile: true } session) return;

        // 没有移调能力的后端（模拟后端、缺插件的运行时）连重载都不用，
        // 免得拖一下滑块就重启一次播放。
        var needsReload = syncChanged || (pitchChanged && _backend.SupportsPitchShift);
        if (!needsReload) return;

        ReloadLocalSession(session);
    }

    public void SetEqualizer(EqualizerSettings settings) {
        var changed = !EqualizerEqual(_equalizer, settings);
        _equalizer = settings;
        if (_disposed) return;
        _backend.SetEqualizer(_equalizer);

        if (!changed) return;

        // 均衡器是通过 audio-filter 变量塞进原生对象的，会整条覆盖滤镜链。
        // 音调正在生效的时候，只有重载媒体才能把 scaletempo_pitch 重新接上。
        if (!IsManualPitchActive) return;
        if (_session is not { Source.IsLocalFile: true } session) return;

        ReloadLocalSession(session);
    }

    /// <summary>
    /// 在当前位置重载当前本地媒体。音调/均衡器各改一次就会各重载一次，
    /// 后一次必须用前一次"准备放到哪"的位置，而不是后端还没追上来的 0 秒。
    /// </summary>
    private void ReloadLocalSession(PlaybackSession session) {
        var (position, playing) = ResolveReloadTarget();
        _reloadPosition = position;
        _reloadPlaying = playing;
        _reloadGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
        Start(session.Source, position, playing);
    }

    /// <summary>快照还没跟上刚发起的重载时，沿用上次请求的位置和播放状态。</summary>
    private (TimeSpan Position, bool Playing) ResolveReloadTarget() {
        var snapshot = _backend.Snapshot;
        var reloading = DateTime.UtcNow < _reloadGuardUntilUtc;
        if (reloading && !snapshot.IsPlaying) return (_reloadPosition, _reloadPlaying);
        return (snapshot.Position, snapshot.IsPlaying);
    }

    /// <summary>用户拖了音调，而且后端真能移调。</summary>
    private bool IsManualPitchActive =>
        _backend.SupportsPitchShift && Math.Abs(_pitchSemitones) > 0.001;

    private static bool EqualizerEqual(EqualizerSettings left, EqualizerSettings right) {
        if (ReferenceEquals(left, right)) return true;
        if (left.BandLevels.Count != right.BandLevels.Count) return false;
        for (var index = 0; index < left.BandLevels.Count; index++) {
            if (Math.Abs(left.BandLevels[index] - right.BandLevels[index]) > 0.001) return false;
        }

        return true;
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
