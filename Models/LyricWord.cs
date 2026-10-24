using System;

namespace Avance_Player.Models;

/// <summary>
/// 歌词行里的一个带时间的词或音节。只有整行时间的格式，所属 <see cref="LyricLine.Words"/> 会是空的；
/// TTML、LYS、QRC、KRC、YRC、增强 LRC、ASS 卡拉 OK 这些会填上，UI 才能逐词高亮。
/// </summary>
public sealed class LyricWord(string text, TimeSpan start, TimeSpan end) {
    public string Text { get; } = text;
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public TimeSpan Duration => End - Start;
}
