using System.Collections.Generic;

namespace Avance_Player.Services.Persistence;

/// <summary>
/// Persisted user state. Settings are composed as nested objects rather than inherited config
/// bases, mirroring the small composition-oriented services used by the playback layer.
/// </summary>
public sealed class PlayerState {
    public const int CurrentVersion = 1;

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
    public int SpeedIndex { get; init; } = 1;
    public int EqualizerPresetIndex { get; init; }
    public string? LastTrackPath { get; init; }
    public double LastPositionSeconds { get; init; }
}

public sealed class LibrarySettings {
    public List<string> TrackPaths { get; init; } = [];
    public List<PlaylistState> Playlists { get; init; } = [];
}

public sealed class PlaylistState {
    public string Name { get; init; } = string.Empty;
    public List<string> TrackPaths { get; init; } = [];
}

public sealed class UiSettings {
    public string Language { get; init; } = "en-US";
    public bool FloatingLyricsVisible { get; init; }
}
