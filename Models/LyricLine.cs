using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.Models;

public sealed class LyricLine(
    TimeSpan timestamp,
    string text,
    string? translation = null,
    string? romanization = null,
    TimeSpan? end = null,
    IReadOnlyList<LyricWord>? words = null) : INotifyPropertyChanged {
    private bool _isCurrent;
    private int _currentWordIndex = -1;

    public TimeSpan Timestamp { get; } = timestamp;

    /// <summary>End of the line. Parsers fill this from the format, the next line, or track length.</summary>
    public TimeSpan End { get; set; } = end ?? timestamp;

    public string Text { get; } = text;
    public string? Translation { get; } = translation;
    public string? Romanization { get; } = romanization;
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
    public bool HasRomanization => !string.IsNullOrWhiteSpace(Romanization);

    /// <summary>Timed words/syllables. Empty for line-level formats.</summary>
    public IReadOnlyList<LyricWord> Words { get; } = words ?? [];
    public bool HasWordTiming => Words.Count > 0;

    public bool IsCurrent {
        get => _isCurrent;
        set {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Index of the word being sung, or -1 before the first word. Updated by the player; the lyric
    /// renderer listens for this property instead of rebuilding the visual tree on every tick.
    /// </summary>
    public int CurrentWordIndex {
        get => _currentWordIndex;
        set {
            if (_currentWordIndex == value) return;
            _currentWordIndex = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
