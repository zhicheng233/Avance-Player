using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace Avance_Player.Services.Playback;

/// <summary>
/// 本地文件的 LibVLC 音频输出。这个类组合一个 <see cref="LibVlcRuntime"/> 和一个
/// <see cref="MediaPlayer"/>，没有从后端基类继承。
/// </summary>
public sealed class LibVlcAudioBackend : IAudioBackend {
    private readonly object _sync = new();
    private readonly LibVlcRuntime _runtime;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private PlaybackSession? _session;
    private string? _availabilityError;
    private double _volume = 0.72;
    private double _rate = 1.0;
    private double _pitchSemitones;
    private bool _preservePitch = true;
    private string[]? _appliedRuntimeOptions;
    private EqualizerSettings _equalizer = EqualizerSettings.Flat;
    private TimeSpan? _pendingStart;
    private bool _hasStarted;
    private bool _disposed;

    public LibVlcAudioBackend(LibVlcRuntime? runtime = null) {
        _runtime = runtime ?? new LibVlcRuntime();
    }

    public bool IsAvailable {
        get {
            var libVlc = _runtime.TryGet(out var error);
            if (libVlc is not null) return true;
            _availabilityError = error;
            return false;
        }
    }

    public string? AvailabilityError {
        get {
            if (_availabilityError is not null) return _availabilityError;
            _runtime.TryGet(out var error);
            return error;
        }
    }

    // libvlc 3 的公开 API 里没有音频滤镜链，所以本地文件没法手动移调。
    // 时间伸缩（音调同步）是支持的。
    public bool SupportsPitchShift => false;

    public event Action<PlaybackEnded>? Ended;
    public event Action<PlaybackFailure>? Failed;

    public AudioLoadResult Load(AudioSource source, TimeSpan startAt = default) {
        if (!source.IsLocalFile) {
            return AudioLoadResult.Fail("The local audio backend only accepts absolute file paths.");
        }

        string fullPath;
        try {
            fullPath = Path.GetFullPath(source.FilePath);
        }
        catch (Exception exception) {
            return AudioLoadResult.Fail(exception.Message);
        }

        if (!File.Exists(fullPath)) {
            return AudioLoadResult.Fail($"File not found: {fullPath}");
        }

        string[] runtimeOptions;
        MediaPlayer? oldPlayer = null;
        Media? oldMediaToDispose = null;
        lock (_sync) {
            if (_disposed) return AudioLoadResult.Fail("The local audio backend has been disposed.");

            runtimeOptions = BuildRuntimeOptions();
            if (_mediaPlayer is not null && !RuntimeOptionsEqual(_appliedRuntimeOptions, runtimeOptions)) {
                // 时间伸缩变了就得重建原生 LibVLC 实例。先把旧 player 摘掉，
                // runtime 才能安全地 dispose 掉旧的 LibVLC。
                oldPlayer = _mediaPlayer;
                oldMediaToDispose = _media;
                _mediaPlayer = null;
                _media = null;
                _hasStarted = false;
                _pendingStart = null;
            }
        }

        if (oldPlayer is not null) {
            try {
                oldPlayer.Stop();
            }
            catch (Exception) {
                // 已经停了或者还没开始的 player 这里可能抛异常。
            }

            oldPlayer.Dispose();
        }

        oldMediaToDispose?.Dispose();

        MediaPlayer? player;
        LibVLC? libVlc;
        string? runtimeError;
        lock (_sync) {
            if (_disposed) return AudioLoadResult.Fail("The local audio backend has been disposed.");

            libVlc = _runtime.TryGet(runtimeOptions, out runtimeError);
            if (libVlc is null) {
                _availabilityError = runtimeError;
                return AudioLoadResult.Fail(runtimeError ?? "LibVLC is unavailable.");
            }

            try {
                player = EnsurePlayerLocked(libVlc);
            }
            catch (Exception exception) {
                _availabilityError = exception.Message;
                return AudioLoadResult.Fail(exception.Message);
            }

            _appliedRuntimeOptions = runtimeOptions;
            // 压掉被换掉的那首歌延迟发来的结束事件，等 Stop 跑完再说。
            _hasStarted = false;
        }

        try {
            // Stop 是同步的。放在 _sync 外面，VLC 线程回调过来时还能拿锁，
            // 不会跟 Stop 死锁。
            player.Stop();
        }
        catch (Exception) {
            // 已经停了或者还没开始的 player 这里可能抛；新 media 还是照样能加载。
        }

        Media media;
        try {
            media = new Media(libVlc, fullPath, FromType.FromPath);
            media.AddOption(":no-video");
        }
        catch (Exception exception) {
            _availabilityError = exception.Message;
            return AudioLoadResult.Fail(exception.Message);
        }

        try {
            player.Media = media;
        }
        catch (Exception exception) {
            media.Dispose();
            _availabilityError = exception.Message;
            return AudioLoadResult.Fail(exception.Message);
        }

        PlaybackSession session;
        Media? oldMedia;
        lock (_sync) {
            if (_disposed) {
                media.Dispose();
                return AudioLoadResult.Fail("The local audio backend has been disposed.");
            }

            oldMedia = _media;
            _media = media;
            session = new PlaybackSession(PlaybackSessionIds.Next(), source);
            _session = session;
            _hasStarted = false;
            _pendingStart = startAt > TimeSpan.Zero
                ? (startAt >= source.Duration ? source.Duration : startAt)
                : null;
            player.Volume = (int)Math.Round(Math.Clamp(_volume, 0, 1) * 100);
            TrySetRate(player, (float)_rate);
            ApplyEqualizerLocked(player);
        }

        oldMedia?.Dispose();
        return AudioLoadResult.Ok(session);
    }

    public void Play() {
        MediaPlayer? player;
        Media? media;
        PlaybackSession? session;
        VLCState state;
        lock (_sync) {
            if (_disposed || _mediaPlayer is null || _media is null || _session is null) return;
            player = _mediaPlayer;
            media = _media;
            session = _session;
            state = player.State;
            _hasStarted = true;
        }

        try {
            if (state == VLCState.Paused) {
                player.SetPause(false);
            }
            else if (state is VLCState.Ended or VLCState.Stopped or VLCState.Error) {
                player.Play(media);
            }
            else {
                player.Play();
            }
        }
        catch (Exception exception) {
            lock (_sync) {
                _hasStarted = false;
                session = _session;
            }
            if (session is not null) {
                Failed?.Invoke(new PlaybackFailure(session, exception.Message));
            }
        }
    }

    public void Pause() {
        MediaPlayer? player;
        lock (_sync) {
            if (_disposed || _mediaPlayer is null) return;
            player = _mediaPlayer;
        }

        try {
            player.SetPause(true);
        }
        catch (Exception) {
            // 暂停一个已经停了的 player 没事。
        }
    }

    public void Stop() {
        MediaPlayer? player;
        lock (_sync) {
            if (_disposed || _mediaPlayer is null) return;
            _hasStarted = false;
            _pendingStart = null;
            player = _mediaPlayer;
        }

        try {
            player.Stop();
        }
        catch (Exception) {
            // 停一个已经停了的 player 也没事。
        }
    }

    public void Seek(TimeSpan position) {
        MediaPlayer? player;
        TimeSpan duration;
        lock (_sync) {
            if (_disposed || _mediaPlayer is null || _session is null) return;
            player = _mediaPlayer;
            duration = _session.Source.Duration;
            _pendingStart = null;
        }

        var clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > duration ? duration : position;

        try {
            player.Time = (long)clamped.TotalMilliseconds;
        }
        catch (Exception) {
            // 有些格式不支持 seek，那就留在当前位置。
        }
    }

    public void SetVolume(double volume) {
        _volume = Math.Clamp(volume, 0, 1);
        MediaPlayer? player;
        lock (_sync) {
            if (_disposed || _mediaPlayer is null) return;
            player = _mediaPlayer;
        }

        try {
            player.Volume = (int)Math.Round(_volume * 100);
        }
        catch (Exception) {
            // player 可能还没挂 media。
        }
    }

    public void SetRate(double rate) {
        _rate = Math.Clamp(rate, 0.25, 4.0);
        MediaPlayer? player;
        lock (_sync) {
            if (_disposed || _mediaPlayer is null) return;
            player = _mediaPlayer;
        }

        TrySetRate(player, (float)_rate);
    }

    public void SetPitch(double semitones, bool preservePitch) {
        lock (_sync) {
            // 留着，万一以后的原生运行时能移调，还用同一个接口。
            _pitchSemitones = Math.Clamp(semitones, -12, 12);
            _preservePitch = preservePitch;
        }
    }

    private string[] BuildRuntimeOptions() {
        var options = new List<string> { "--no-video", "--quiet" };
        if (!_preservePitch) {
            // LibVLC 默认对变速做时间伸缩。关掉之后变速会跟着变调，就是唱机那种调速手感。
            options.Add("--no-audio-time-stretch");
        }

        // libvlc 3 没有音频滤镜/移调 API，所以半音偏移只是存着，等以后有后端能应用；
        // 不会当成 media 选项传进去（传了也会被静默忽略）。
        return options.ToArray();
    }

    private static bool RuntimeOptionsEqual(IReadOnlyList<string>? left, IReadOnlyList<string> right) {
        if (left is null || left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++) {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public void SetEqualizer(EqualizerSettings settings) {
        lock (_sync) {
            _equalizer = settings;
            if (_disposed || _mediaPlayer is null) return;
            ApplyEqualizerLocked(_mediaPlayer);
        }
    }

    public PlaybackSnapshot Snapshot {
        get {
            MediaPlayer? player;
            PlaybackSession? session;
            lock (_sync) {
                if (_disposed || _mediaPlayer is null || _session is null) {
                    return new PlaybackSnapshot(false, TimeSpan.Zero, TimeSpan.Zero, false);
                }
                player = _mediaPlayer;
                session = _session;
            }

            try {
                var positionMs = Math.Max(0, player.Time);
                var lengthMs = player.Length;
                var duration = lengthMs > 0
                    ? TimeSpan.FromMilliseconds(lengthMs)
                    : session.Source.Duration;
                var position = TimeSpan.FromMilliseconds(positionMs);
                if (duration > TimeSpan.Zero && position > duration) position = duration;
                var state = player.State;
                var atEnd = state == VLCState.Ended
                            || (duration > TimeSpan.Zero && position >= duration - TimeSpan.FromMilliseconds(50));
                // 刚换歌开始播的时候 IsPlaying 可能比原生状态慢半拍。Playing 状态也算数。
                var isPlaying = player.IsPlaying || state == VLCState.Playing;
                return new PlaybackSnapshot(isPlaying, position, duration, atEnd);
            }
            catch (Exception) {
                return new PlaybackSnapshot(false, TimeSpan.Zero, session.Source.Duration, false);
            }
        }
    }

    private MediaPlayer EnsurePlayerLocked(LibVLC libVlc) {
        if (_mediaPlayer is not null) return _mediaPlayer;

        var player = new MediaPlayer(libVlc);
        player.Playing += MediaPlayer_Playing;
        player.TimeChanged += MediaPlayer_TimeChanged;
        player.EndReached += MediaPlayer_EndReached;
        player.EncounteredError += MediaPlayer_EncounteredError;
        _mediaPlayer = player;
        return player;
    }

    private static void TrySetRate(MediaPlayer player, float rate) {
        try {
            player.SetRate(rate);
        }
        catch (Exception) {
            // 不支持变速的格式，能变多少算多少。
        }
    }

    private void ApplyEqualizerLocked(MediaPlayer player) {
        try {
            if (BuildEqualizer(_equalizer) is not { } equalizer) {
                player.UnsetEqualizer();
                return;
            }

            using (equalizer) {
                player.SetEqualizer(equalizer);
            }
        }
        catch (Exception) {
            // 原生运行时可能比均衡器 API 老；播放还是能用的。
        }
    }

    /// <summary>
    /// 把 UI 上五个归一化频段映射到 LibVLC 原生十段。UI 频率按对数插值，
    /// 显示出来的形状能保住，不会把每个原生段硬塞到五个控制里的某一个。
    /// </summary>
    private static Equalizer? BuildEqualizer(EqualizerSettings settings) {
        var levels = settings.BandLevels;
        if (levels.Count == 0) return null;

        var changed = false;
        for (var index = 0; index < levels.Count; index++) {
            if (Math.Abs(levels[index] - 0.5) > 0.001) {
                changed = true;
                break;
            }
        }
        if (!changed) return null;

        var equalizer = new Equalizer();
        try {
            var bandCount = (int)equalizer.BandCount;
            if (bandCount <= 0) {
                equalizer.Dispose();
                return null;
            }

            // 曲线以 0 dB 为中心，不再给最响的频段压一个负 preamp。
            // 之前一加负 preamp，非 Flat 预设就明显比 Flat 小声。
            var amps = new double[bandCount];
            var sum = 0.0;
            for (uint band = 0; band < bandCount; band++) {
                var frequency = equalizer.BandFrequency(band);
                var level = LevelAt(levels, frequency);
                var amp = Math.Clamp((level - 0.5) * 20.0, -12.0, 6.0);
                amps[band] = amp;
                sum += amp;
            }

            var average = sum / bandCount;
            for (uint band = 0; band < bandCount; band++) {
                var compensated = Math.Clamp(amps[band] - average, -12.0, 6.0);
                equalizer.SetAmp((float)compensated, band);
            }

            equalizer.SetPreamp(0);
            return equalizer;
        }
        catch {
            equalizer.Dispose();
            throw;
        }
    }

    private static double LevelAt(IReadOnlyList<double> levels, double frequency) {
        // 跟设置面板里五个标签对上。
        ReadOnlySpan<double> frequencies = [31.0, 125.0, 500.0, 2000.0, 8000.0];
        if (frequency <= frequencies[0]) return levels[0];
        if (frequency >= frequencies[^1]) return levels[^1];

        for (var index = 1; index < frequencies.Length; index++) {
            if (frequency > frequencies[index]) continue;
            var lower = frequencies[index - 1];
            var upper = frequencies[index];
            var t = (Math.Log(frequency) - Math.Log(lower)) / (Math.Log(upper) - Math.Log(lower));
            return levels[index - 1] + (levels[index] - levels[index - 1]) * t;
        }

        return levels[^1];
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e) => SchedulePendingStart();

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) {
        // Playing 是快路径；TimeChanged 是给不抛 Playing 的输出的兜底。
        if (e.Time > 0) SchedulePendingStart();
    }

    private void SchedulePendingStart() {
        TimeSpan? pending;
        MediaPlayer? player;
        long sessionId;
        lock (_sync) {
            if (_disposed || !_hasStarted || _pendingStart is null || _mediaPlayer is null || _session is null) return;
            pending = _pendingStart;
            _pendingStart = null;
            player = _mediaPlayer;
            sessionId = _session.Id;
        }

        if (pending is not { } position || player is null) return;

        // 别在 LibVLC 事件回调里 seek：有些输出会死锁或者把时钟卡住。
        // 让回调先返回，再到线程池里应用位置。
        _ = Task.Run(async () => {
            await Task.Delay(30).ConfigureAwait(false);
            lock (_sync) {
                if (_disposed || !_hasStarted || _session?.Id != sessionId || !ReferenceEquals(_mediaPlayer, player)) return;
            }

            try {
                var targetMs = Math.Max(0, position.TotalMilliseconds);
                var lengthMs = player.Length;
                if (lengthMs > 0 && targetMs >= lengthMs) {
                    // TagLib 的时长可能比 LibVLC 的长；别 seek 过真正的结尾。
                    targetMs = Math.Max(0, lengthMs - 500);
                }
                player.Time = (long)targetMs;
            }
            catch (Exception) {
                // 有些格式不支持 seek，那首歌就从 0 开始。
            }
        });
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e) {
        PlaybackSession? session = null;
        lock (_sync) {
            if (_disposed || !_hasStarted || _session is null || _mediaPlayer is null) return;

            // 上一首延迟的 EndReached 可能在新歌开始之后才到。
            // 要求状态是 Ended（或者位置确实在最后），免得把旧事件当成新歌结束。
            var state = _mediaPlayer.State;
            if (state != VLCState.Ended) {
                var length = _mediaPlayer.Length;
                var position = _mediaPlayer.Time;
                if (length <= 0 || position < length - 500) return;
            }

            _hasStarted = false;
            session = _session;
        }

        if (session is not null) {
            Ended?.Invoke(new PlaybackEnded(session, PlaybackEndReason.Completed));
        }
    }

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) {
        PlaybackSession? session = null;
        lock (_sync) {
            if (_disposed || !_hasStarted || _session is null) return;
            _hasStarted = false;
            session = _session;
        }

        if (session is not null) {
            Failed?.Invoke(new PlaybackFailure(session, "LibVLC reported a playback error."));
        }
    }

    public void Dispose() {
        MediaPlayer? player;
        Media? media;
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            _hasStarted = false;
            player = _mediaPlayer;
            media = _media;
            _mediaPlayer = null;
            _media = null;
            _session = null;

            if (player is not null) {
                player.Playing -= MediaPlayer_Playing;
                player.TimeChanged -= MediaPlayer_TimeChanged;
                player.EndReached -= MediaPlayer_EndReached;
                player.EncounteredError -= MediaPlayer_EncounteredError;
            }
        }

        try {
            player?.Stop();
        }
        catch (Exception) {
            // 关停时的竞态，忽略。
        }

        media?.Dispose();
        player?.Dispose();
        _runtime.Dispose();
    }
}
