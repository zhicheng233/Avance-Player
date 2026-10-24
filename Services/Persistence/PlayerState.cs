using System.Collections.Generic;

namespace Avance_Player.Services.Persistence;

/// <summary>
/// 持久化的用户状态。设置用嵌套对象组合，不搞继承的 config 基类，
/// 跟播放层那些小服务一个路子。
/// </summary>
public sealed class PlayerState {
    // 版本 2 加了 LibrarySettings.LyricPaths。版本 3 加了连续速度和音调偏好。
    // 老文件也能读，新属性都有默认值。
    public const int CurrentVersion = 3;

    public int Version { get; init; } = CurrentVersion;
    public PlaybackSettings Playback { get; init; } = new();
    public LibrarySettings Library { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
}

public sealed class PlaybackSettings {
    public double Volume { get; init; } = 0.72;
    public double LastNonZeroVolume { get; init; } = 0.72;
    public bool Shuffle { get; init; }
    public string RepeatMode { get; init; } = "Off";

    /// <summary>老的离散速度索引，只在没有 <see cref="Speed"/> 时用。</summary>
    public int SpeedIndex { get; init; } = 1;

    public double? Speed { get; init; }
    public bool PitchSync { get; init; } = true;
    public double PitchSemitones { get; init; }
    public int EqualizerPresetIndex { get; init; }
    public string? LastTrackPath { get; init; }
    public double LastPositionSeconds { get; init; }
}

public sealed class LibrarySettings {
    public List<string> TrackPaths { get; init; } = [];
    public List<PlaylistState> Playlists { get; init; } = [];

    /// <summary>
    /// 手动导入的歌词文件，按音频路径存。sidecar 歌词扫描器会重新找；
    /// 从任意位置选的歌词文件得记在这里。
    /// </summary>
    public Dictionary<string, string> LyricPaths { get; init; } = [];
}

public sealed class PlaylistState {
    public string Name { get; init; } = string.Empty;
    public List<string> TrackPaths { get; init; } = [];
}

public sealed class UiSettings {
    public string Language { get; init; } = "en-US";
    public bool FloatingLyricsVisible { get; init; }
}
