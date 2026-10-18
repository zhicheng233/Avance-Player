using System;
using System.Collections.Generic;

namespace Avance_Player.Models;

public sealed class Music {
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string Genre { get; init; }
    public required string ArtworkColor { get; init; }
    public required TimeSpan Duration { get; init; }
    public string DurationLabel => Duration.ToString(@"m\:ss");
    public string FilePath { get; init; } = string.Empty;
    public IReadOnlyList<LyricLine> Lyrics { get; init; } = [];
}
