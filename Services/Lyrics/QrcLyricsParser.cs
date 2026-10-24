using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// QQ 音乐 QRC（明文/解密后的）。行是 [start,duration]，每个音节后面跟
/// (start,duration)，绝对毫秒。
/// </summary>
public sealed class QrcLyricsParser : ILyricsParser {
    private static readonly Regex MetadataPattern = new(
        @"^\s*\[(?<key>[a-zA-Z#]+):(?<value>.*)\]\s*$", RegexOptions.Compiled);

    private static readonly Regex LinePattern = new(
        @"^\s*\[(?<start>\d+),(?<duration>\d+)\](?<body>.*)$", RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(
        @"(?<text>.*?)\((?<start>\d+),(?<duration>\d+)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public LyricsFormat Format => LyricsFormat.Qrc;

    public bool CanParse(string content) =>
        LinePattern.IsMatch(content) && WordPattern.IsMatch(content);

    public LyricsDocument Parse(string content, LyricsParseContext context) {
        if (LooksEncrypted(content)) {
            throw new LyricsParseException(
                "This QRC file is encrypted. Export or download a decrypted QRC/LRC/TTML file instead.");
        }

        var lines = new List<LyricLine>();
        var title = default(string);
        var artist = default(string);
        var album = default(string);
        var offset = 0;

        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var metadata = MetadataPattern.Match(line);
            if (metadata.Success) {
                var key = metadata.Groups["key"].Value.ToLowerInvariant();
                var value = metadata.Groups["value"].Value.Trim();
                switch (key) {
                    case "ti": title = value; break;
                    case "ar": artist = value; break;
                    case "al": album = value; break;
                    case "offset": offset = LyricsTime.ParseOffsetMilliseconds(value); break;
                }
                continue;
            }

            var match = LinePattern.Match(line);
            if (!match.Success) continue;

            var lineStart = TimeSpan.FromMilliseconds(ParseLong(match.Groups["start"].Value));
            var lineDuration = TimeSpan.FromMilliseconds(ParseLong(match.Groups["duration"].Value));
            lineStart -= TimeSpan.FromMilliseconds(offset);
            if (lineStart < TimeSpan.Zero) lineStart = TimeSpan.Zero;

            var body = match.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body)) continue;

            var words = new List<LyricWord>();
            var position = 0;
            foreach (Match word in WordPattern.Matches(body)) {
                var wordText = word.Groups["text"].Value;
                if (wordText.Length > 0) {
                    var start = TimeSpan.FromMilliseconds(ParseLong(word.Groups["start"].Value));
                    var duration = TimeSpan.FromMilliseconds(ParseLong(word.Groups["duration"].Value));
                    start -= TimeSpan.FromMilliseconds(offset);
                    if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                    words.Add(new LyricWord(wordText, start, start + duration));
                }
                position = word.Index + word.Length;
            }

            var trailing = body[position..];
            if (trailing.Length > 0 && words.Count > 0) {
                var last = words[^1];
                words[^1] = new LyricWord(last.Text + trailing, last.Start, last.End);
            }

            var normalized = LyricsNormalizer.NormalizeWords(words).ToList();
            var text = normalized.Count > 0
                ? LyricsNormalizer.TextFromWords(normalized)
                : body.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var end = lineDuration > TimeSpan.Zero ? lineStart + lineDuration : (TimeSpan?)null;
            lines.Add(new LyricLine(lineStart, text, end: end, words: normalized));
        }

        if (lines.Count == 0)
            throw new LyricsParseException("No timestamped lyric lines were found in the QRC file.");

        var finalized = LyricsNormalizer.Finalize(lines, context.TrackDuration);
        return new LyricsDocument {
            Format = Format,
            Lines = LyricsNormalizer.FillWordEnds(finalized),
            Title = title,
            Artist = artist,
            Album = album,
            Offset = TimeSpan.FromMilliseconds(offset)
        };
    }

    private static bool LooksEncrypted(string content) {
        var trimmed = content.Trim();
        if (trimmed.Length < 128) return false;
        return trimmed.All(character => Uri.IsHexDigit(character));
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
