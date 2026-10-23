using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace Avance_Player.Services.Playback;

/// <summary>
/// LibVLC-backed audio output for imported local files. The class composes a
/// <see cref="LibVlcRuntime"/> and a <see cref="MediaPlayer"/> instead of deriving from a backend
/// base class.
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

        MediaPlayer? player;
        LibVLC? libVlc;
        string? runtimeError;
        lock (_sync) {
            if (_disposed) return AudioLoadResult.Fail("The local audio backend has been disposed.");

            libVlc = _runtime.TryGet(out runtimeError);
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

            // Suppress a delayed end event from the track being replaced before Stop runs.
            _hasStarted = false;
        }

        try {
            // Stop is synchronous. Keep it outside _sync so a callback arriving from a VLC thread
            // can still take the lock instead of deadlocking with Stop.
            player.Stop();
        }
        catch (Exception) {
            // A stopped or not-yet-started player may throw here; loading the new media is still safe.
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
            // Pausing a stopped player is harmless.
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
            // Stopping an already stopped player is harmless.
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
            // Some formats do not support seeking; keep the current position.
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
            // The player may not have a media loaded yet.
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
                // IsPlaying can briefly lag the native state while a newly loaded track starts.
                // Treat the Playing state as authoritative too.
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
            // Rate changes are best-effort for formats that do not support them.
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
            // The native runtime may be older than the equalizer API; playback still works.
        }
    }

    /// <summary>
    /// Maps the five normalized UI bands onto LibVLC's native ten bands. The UI frequencies are
    /// interpolated on a log scale so the displayed shape is preserved instead of snapping each
    /// native band to one of the five controls.
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

            var maxBoost = 0.0;
            for (uint band = 0; band < bandCount; band++) {
                var frequency = equalizer.BandFrequency(band);
                var level = LevelAt(levels, frequency);
                var amp = Math.Clamp((level - 0.5) * 24.0, -20.0, 20.0);
                if (amp > maxBoost) maxBoost = amp;
                equalizer.SetAmp((float)amp, band);
            }

            // Keep a little headroom when a preset boosts any band, so the EQ does not clip.
            equalizer.SetPreamp((float)Math.Clamp(-maxBoost, -20.0, 20.0));
            return equalizer;
        }
        catch {
            equalizer.Dispose();
            throw;
        }
    }

    private static double LevelAt(IReadOnlyList<double> levels, double frequency) {
        // Matches the five labels shown in the settings panel.
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
        // Playing is the fast path; TimeChanged is a fallback for outputs that do not raise it.
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

        // Never seek from inside a LibVLC event callback: some outputs can deadlock or park the
        // clock. Let the callback return, then apply the position on the thread pool.
        _ = Task.Run(async () => {
            await Task.Delay(30).ConfigureAwait(false);
            lock (_sync) {
                if (_disposed || !_hasStarted || _session?.Id != sessionId || !ReferenceEquals(_mediaPlayer, player)) return;
            }

            try {
                var targetMs = Math.Max(0, position.TotalMilliseconds);
                var lengthMs = player.Length;
                if (lengthMs > 0 && targetMs >= lengthMs) {
                    // TagLib's duration can be longer than LibVLC's; do not seek past the real end.
                    targetMs = Math.Max(0, lengthMs - 500);
                }
                player.Time = (long)targetMs;
            }
            catch (Exception) {
                // Some formats do not support seeking; the track will start from zero.
            }
        });
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e) {
        PlaybackSession? session = null;
        lock (_sync) {
            if (_disposed || !_hasStarted || _session is null || _mediaPlayer is null) return;

            // A delayed EndReached from the previous media can arrive after a new track starts.
            // Requiring the Ended state (or a position at the very end) avoids treating that stale
            // event as the new track's end.
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
            // Ignore shutdown races.
        }

        media?.Dispose();
        player?.Dispose();
        _runtime.Dispose();
    }
}
