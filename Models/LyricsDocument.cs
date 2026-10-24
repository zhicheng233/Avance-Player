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
/// 解析后的歌词和文件里带的元信息。播放器其实只用 <see cref="Lines"/>；
/// 其余字段留着，UI 可以说明加载了什么，以后设置里也能拿标题/歌手/偏移。
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
