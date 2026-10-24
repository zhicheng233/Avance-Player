using System;

namespace Avance_Player.Services.Playback;

/// <summary>
/// 按源把播放分到真实或模拟后端。这是组合，不是后端继承：
/// 路由自己拿着两个实现，把很小的 <see cref="IAudioBackend"/> 接口转发出去。
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
    public bool SupportsPitchShift => _local.SupportsPitchShift;

    public event Action<PlaybackEnded>? Ended;
    public event Action<PlaybackFailure>? Failed;

    public AudioLoadResult Load(AudioSource source, TimeSpan startAt = default) {
        if (_disposed) return AudioLoadResult.Fail("The audio backend has been disposed.");

        if (source.IsLocalFile) {
            _simulated.Stop();
            // 这次加载失败了（文件没了、原生运行时不可用）也要把上一首本地歌停掉；
            // 不然旧音频还在响，会话却已经没了。
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

    public void SetPitch(double semitones, bool preservePitch) {
        // 两个后端都存一份偏好，切源的时候不会丢。
        _local.SetPitch(semitones, preservePitch);
        _simulated.SetPitch(semitones, preservePitch);
    }

    public void SetEqualizer(EqualizerSettings settings) => _active?.SetEqualizer(settings);

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
