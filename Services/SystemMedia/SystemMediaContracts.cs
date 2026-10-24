using System;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// 系统媒体控制显示用的播放器状态快照。后端留着最新快照，
/// MPRIS 的 <c>Position</c> 这种属性才能按需回答。
/// </summary>
public sealed record SystemMediaSnapshot(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string? ArtworkPath,
    string? SourcePath,
    bool IsPlaying,
    TimeSpan Position,
    double Volume,
    bool IsShuffle,
    SystemMediaRepeatMode RepeatMode,
    double Rate);

public enum SystemMediaRepeatMode {
    Off,
    Track,
    Playlist
}

public enum SystemMediaCommandKind {
    Play,
    Pause,
    TogglePlayPause,
    Stop,
    Next,
    Previous,
    Seek,
    Shuffle,
    Repeat,
    Volume,
    Raise,
    Quit
}

/// <summary>
/// 系统媒体控制发过来的命令。Payload 字段只对匹配的 <see cref="Kind"/> 有意义。
/// </summary>
public sealed record SystemMediaCommand(
    SystemMediaCommandKind Kind,
    TimeSpan Position = default,
    double Number = 0,
    bool Flag = false,
    SystemMediaRepeatMode Repeat = SystemMediaRepeatMode.Off);

/// <summary>
/// OS 媒体浮层/媒体键的平台实现。Update 里别往外抛，
/// 因为桌面服务不在的时候播放还得继续。
/// </summary>
public interface ISystemMediaBackend : IDisposable {
    bool IsAvailable { get; }
    event Action<SystemMediaCommand>? CommandReceived;
    void Update(SystemMediaSnapshot snapshot);
    void Clear();
}
