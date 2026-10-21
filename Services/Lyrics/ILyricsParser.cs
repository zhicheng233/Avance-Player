using System;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

public sealed class LyricsParseContext(string fileName, TimeSpan trackDuration) {
    public string FileName { get; } = fileName;
    public TimeSpan TrackDuration { get; } = trackDuration;
}

public sealed class LyricsParseException(string message) : Exception(message);

public interface ILyricsParser {
    LyricsFormat Format { get; }

    /// <summary>Sniffs content when the extension is missing or ambiguous.</summary>
    bool CanParse(string content);

    LyricsDocument Parse(string content, LyricsParseContext context);
}
