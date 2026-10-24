using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// SubRip 字幕。SRT 没有逐词时间，每个字幕块就是一行整行级歌词。
/// 标签会剥掉，多行块并成一行。
/// </summary>
public sealed class SrtLyricsParser : ILyricsParser {
    private static readonly Regex TimingPattern = new(
        @"^\s*(?<start>\d{1,2}:\d{1,2}(?::\d{1,2})?(?:[.,]\d{1,3})?)\s*-->\s*(?<end>\d{1,2}:\d{1,2}(?::\d{1,2})?(?:[.,]\d{1,3})?)",
        RegexOptions.Compiled);

    private static readonly Regex TagPattern = new(@"<[^>]+>|\{\\[^}]+\}", RegexOptions.Compiled);

    public LyricsFormat Format => LyricsFormat.Srt;

    public bool CanParse(string content) => TimingPattern.IsMatch(content);

    public LyricsDocument Parse(string content, LyricsParseContext context) {
        var lines = new List<LyricLine>();
        var rawLines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < rawLines.Length; index++) {
            var timing = TimingPattern.Match(rawLines[index]);
            if (!timing.Success) continue;
            if (!LyricsTime.TryParseAss(timing.Groups["start"].Value, out var start)) continue;
            if (!LyricsTime.TryParseAss(timing.Groups["end"].Value, out var end)) continue;

            var textLines = new List<string>();
            index++;
            while (index < rawLines.Length && !string.IsNullOrWhiteSpace(rawLines[index])) {
                var text = CleanText(rawLines[index]);
                if (text.Length > 0) textLines.Add(text);
                index++;
            }

            var lineText = string.Join(" ", textLines).Trim();
            if (lineText.Length == 0) continue;

            lines.Add(new LyricLine(start, lineText, end: end));
        }

        if (lines.Count == 0)
            throw new LyricsParseException("No subtitle blocks were found in the SRT file.");

        return new LyricsDocument {
            Format = Format,
            Lines = LyricsNormalizer.Finalize(lines, context.TrackDuration)
        };
    }

    private static string CleanText(string value) {
        var text = TagPattern.Replace(value, string.Empty)
            .Replace("\\N", " ", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\h", " ", StringComparison.Ordinal);
        return WebUtility.HtmlDecode(text).Trim();
    }
}
