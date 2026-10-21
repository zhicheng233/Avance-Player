using System;
using System.Collections.Generic;
using System.Linq;

namespace Avance_Player.Models;

public enum LyricsFormat {
    Lrc,
    Srt,
    Ass,
    Ttml,
    Lys,
    Qrc,
    Krc,
    Yrc
}

/// <summary>
/// Parsed lyrics plus the metadata that came with the file. The player only needs
/// <see cref="Lines"/>; the remaining fields are kept so the UI can describe what was loaded and
/// future settings can expose title/artist/offset information.
/// </summary>
public sealed class LyricsDocument {
    public required LyricsFormat Format { get; init; }
    public required IReadOnlyList<LyricLine> Lines { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public TimeSpan Offset { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasWordTiming => Lines.Any(line => line.HasWordTiming);
}
