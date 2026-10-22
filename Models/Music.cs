using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace Avance_Player.Models;

public sealed class Music : INotifyPropertyChanged {
    private bool _isCurrent;
    private IReadOnlyList<LyricLine> _lyrics = [];

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string Genre { get; init; }
    public required string ArtworkColor { get; init; }
    public required TimeSpan Duration { get; init; }
    public string DurationLabel => Duration.ToString(@"m\:ss");
    public string FilePath { get; init; } = string.Empty;

    /// <summary>True for imported absolute paths; demo tracks keep relative paths.</summary>
    public bool IsLocalFile {
        get {
            if (string.IsNullOrWhiteSpace(FilePath)) return false;
            try {
                return Path.IsPathFullyQualified(FilePath);
            }
            catch (Exception) {
                return false;
            }
        }
    }

    /// <summary>True when this is an imported file that still exists on disk.</summary>
    public bool HasPlayableFile => IsLocalFile && File.Exists(FilePath);

    /// <summary>
    /// Cover loaded from a sidecar image or the audio file's embedded picture. Demo tracks leave
    /// this null and fall back to <see cref="ArtworkColor"/>.
    /// </summary>
    public Bitmap? Artwork { get; init; }

    /// <summary>Source path of the sidecar cover, when one was found.</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>Bitrate of the local file in kbps, used to derive the quality tier label.</summary>
    public int BitrateKbps { get; init; } = 1411;
    public string BitrateLabel => $"{BitrateKbps} kbps";

    /// <summary>
    /// Lossless tiers are named after the carrier they are commonly associated with, matching how
    /// local players label a file instead of offering a streaming quality switch.
    /// </summary>
    public string QualityTier => BitrateKbps switch {
        >= 2300 => "Hi-Res",
        >= 1450 => "DVD",
        >= 1000 => "CD",
        >= 320 => "HQ",
        _ => "Standard"
    };

    public string LocalQualityLabel => $"{BitrateLabel} · {QualityTier}";

    /// <summary>
    /// Lyrics are replaceable because a real library will load sidecar files after the track is
    /// constructed. The setter notifies bindings such as <c>CurrentTrack.Lyrics</c>.
    /// </summary>
    public IReadOnlyList<LyricLine> Lyrics {
        get => _lyrics;
        set {
            if (ReferenceEquals(_lyrics, value)) return;
            _lyrics = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Format of the currently attached lyrics, if they came from a parsed file.</summary>
    public LyricsFormat? LyricsFormat { get; set; }

    /// <summary>Original file name/path, used for the load status message.</summary>
    public string? LyricsSourcePath { get; set; }

    /// <summary>Set by the player so queue rows can highlight the current track.</summary>
    public bool IsCurrent {
        get => _isCurrent;
        set {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
