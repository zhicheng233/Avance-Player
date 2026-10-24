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

    /// <summary>扩展名缺失或者拿不准时，靠内容嗅探。</summary>
    bool CanParse(string content);

    LyricsDocument Parse(string content, LyricsParseContext context);
}
