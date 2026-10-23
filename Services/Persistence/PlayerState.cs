using System.Collections.Generic;

namespace Avance_Player.Services.Persistence;

/// <summary>
/// Persisted user state. Settings are composed as nested objects rather than inherited config
/// bases, mirroring the small composition-oriented services used by the playback layer.
/// </summary>
public sealed class PlayerState {
    // Version 2 adds LibrarySettings.LyricPaths. Older files remain readable because the new
    // collection defaults to empty when the property is absent.
    public const int CurrentVersion = 2;

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

    /// <summary>
    /// Manual lyric-file imports keyed by the audio track path. Sidecar lyrics are re-discovered
    /// by the scanner, but a file chosen from an arbitrary location has to be remembered here.
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
