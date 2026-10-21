using System;

namespace Avance_Player.Models;

/// <summary>
/// A timed word or syllable inside a lyric line. Formats that only carry line timing leave the
/// owning <see cref="LyricLine.Words"/> collection empty; formats such as TTML, LYS, QRC, KRC,
/// YRC, enhanced LRC, and ASS karaoke fill it so the UI can highlight the line word by word.
/// </summary>
public sealed class LyricWord(string text, TimeSpan start, TimeSpan end) {
    public string Text { get; } = text;
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public TimeSpan Duration => End - Start;
}
