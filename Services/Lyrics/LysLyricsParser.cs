using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// LYS karaoke lyrics. Each line starts with a numeric property in brackets, followed by
/// word(start,duration) pairs where times are absolute milliseconds.
/// </summary>
public sealed class LysLyricsParser : ILyricsParser {
    private static readonly Regex MetadataPattern = new(
        @"^\s*\[(?<key>[a-zA-Z#]+):(?<value>.*)\]\s*$", RegexOptions.Compiled);

    private static readonly Regex LinePattern = new(
        @"^\s*\[(?<property>\d+)\](?<body>.*)$", RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(
        @"(?<text>.*?)\((?<start>\d+),(?<duration>\d+)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public LyricsFormat Format => LyricsFormat.Lys;

    public bool CanParse(string content) =>
        LinePattern.IsMatch(content) && WordPattern.IsMatch(content);

    public LyricsDocument Parse(string content, LyricsParseContext context) {
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
            var body = match.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body)) continue;

            var words = new List<LyricWord>();
            var position = 0;
            foreach (Match word in WordPattern.Matches(body)) {
                var text = word.Groups["text"].Value;
                if (text.Length > 0) {
                    var start = TimeSpan.FromMilliseconds(ParseLong(word.Groups["start"].Value));
                    var duration = TimeSpan.FromMilliseconds(ParseLong(word.Groups["duration"].Value));
                    start -= TimeSpan.FromMilliseconds(offset);
                    if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                    words.Add(new LyricWord(text, start, start + duration));
                }
                position = word.Index + word.Length;
            }

            var trailing = body[position..];
            if (trailing.Length > 0) {
                if (words.Count > 0) {
                    var last = words[^1];
                    words[^1] = new LyricWord(last.Text + trailing, last.Start, last.End);
                }
                else {
                    continue;
                }
            }

            var normalized = LyricsNormalizer.NormalizeWords(words).ToList();
            if (normalized.Count == 0) continue;

            var lineStart = normalized[0].Start;
            var lineEnd = normalized.Max(word => word.End);
            lines.Add(new LyricLine(lineStart, LyricsNormalizer.TextFromWords(normalized), end: lineEnd, words: normalized));
        }

        if (lines.Count == 0)
            throw new LyricsParseException("No word-timed lyric lines were found in the LYS file.");

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

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
