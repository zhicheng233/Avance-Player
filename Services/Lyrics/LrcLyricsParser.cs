using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// Plain and enhanced LRC. Enhanced LRC embeds &lt;mm:ss.xx&gt; word timestamps; files without them
/// stay line-level. Repeated timestamps are treated as translation lines, matching the common
/// bilingual LRC convention.
/// </summary>
public sealed class LrcLyricsParser : ILyricsParser {
    private static readonly Regex MetadataPattern = new(
        @"^\s*\[(?<key>[a-zA-Z#]+):(?<value>.*)\]\s*$", RegexOptions.Compiled);

    private static readonly Regex LinePattern = new(
        @"^\s*(?<tags>(?:\[\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?\])+)(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex TimestampPattern = new(
        @"\[(?<time>\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)\]", RegexOptions.Compiled);

    private static readonly Regex WordTagPattern = new(
        @"<(?<time>\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)>", RegexOptions.Compiled);

    public LyricsFormat Format => LyricsFormat.Lrc;

    public bool CanParse(string content) =>
        content.Contains('[') && TimestampPattern.IsMatch(content) && !content.Contains("[Events]", StringComparison.OrdinalIgnoreCase);

    public LyricsDocument Parse(string content, LyricsParseContext context) {
        var rawLines = new List<LyricLine>();
        var title = default(string);
        var artist = default(string);
        var album = default(string);
        var offset = 0;

        foreach (var rawLine in content.Split('\n')) {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

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

            var timestamps = new List<TimeSpan>();
            foreach (Match timestamp in TimestampPattern.Matches(match.Groups["tags"].Value)) {
                if (LyricsTime.TryParseLrc(timestamp.Groups["time"].Value, out var time)) {
                    var shifted = time - TimeSpan.FromMilliseconds(offset);
                    timestamps.Add(shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted);
                }
            }
            if (timestamps.Count == 0) continue;

            var rawText = match.Groups["text"].Value.Trim();
            if (rawText.Length == 0) continue;

            var words = ParseWordTags(rawText, timestamps[0], TimeSpan.FromMilliseconds(offset), out var plainText);
            var text = words.Count > 0 ? LyricsNormalizer.TextFromWords(words) : plainText;
            if (string.IsNullOrWhiteSpace(text)) continue;

            for (var index = 0; index < timestamps.Count; index++) {
                var shift = timestamps[index] - timestamps[0];
                IReadOnlyList<LyricWord> shiftedWords = words.Count == 0
                    ? []
                    : words.Select(word => new LyricWord(word.Text, word.Start + shift, word.End + shift)).ToList();
                rawLines.Add(new LyricLine(timestamps[index], text, end: null, words: shiftedWords));
            }
        }

        if (rawLines.Count == 0)
            throw new LyricsParseException("No timestamped lyric lines were found in the LRC file.");

        var lines = MergeSameTimestampLines(rawLines);
        lines = LyricsNormalizer.Finalize(lines, context.TrackDuration).ToList();
        lines = LyricsNormalizer.FillWordEnds(lines).ToList();

        return new LyricsDocument {
            Format = Format,
            Lines = lines,
            Title = title,
            Artist = artist,
            Album = album,
            Offset = TimeSpan.FromMilliseconds(offset)
        };
    }

    private static IReadOnlyList<LyricWord> ParseWordTags(
        string text,
        TimeSpan lineStart,
        TimeSpan offset,
        out string plainText) {
        var matches = WordTagPattern.Matches(text);
        if (matches.Count == 0) {
            plainText = text;
            return [];
        }

        var words = new List<LyricWord>();
        var plain = new StringBuilder();
        var cursor = lineStart;
        var textStart = 0;

        foreach (Match match in matches) {
            if (!LyricsTime.TryParseLrc(match.Groups["time"].Value, out var tagTime)) continue;
            tagTime -= offset;
            if (tagTime < TimeSpan.Zero) tagTime = TimeSpan.Zero;

            var segment = text[textStart..match.Index];
            if (segment.Length > 0) {
                words.Add(new LyricWord(segment, cursor, tagTime > cursor ? tagTime : cursor));
                plain.Append(segment);
            }

            cursor = tagTime;
            textStart = match.Index + match.Length;
        }

        var tail = text[textStart..];
        if (tail.Length > 0) {
            words.Add(new LyricWord(tail, cursor, cursor));
            plain.Append(tail);
        }

        var normalized = LyricsNormalizer.NormalizeWords(words);
        plainText = normalized.Count > 0 ? LyricsNormalizer.TextFromWords(normalized) : plain.ToString();
        return normalized;
    }

    private static List<LyricLine> MergeSameTimestampLines(List<LyricLine> lines) {
        var result = new List<LyricLine>();
        foreach (var group in lines.GroupBy(line => line.Timestamp).OrderBy(group => group.Key)) {
            var items = group.ToList();
            if (items.Count == 1) {
                result.Add(items[0]);
                continue;
            }

            var main = items[0];
            var translation = main.Translation;
            var romanization = main.Romanization;
            for (var index = 1; index < items.Count; index++) {
                var extra = items[index].Text;
                if (string.IsNullOrWhiteSpace(extra)) continue;
                if (string.IsNullOrWhiteSpace(translation)) translation = extra;
                else if (string.IsNullOrWhiteSpace(romanization)) romanization = extra;
            }

            result.Add(new LyricLine(main.Timestamp, main.Text, translation, romanization, main.End, main.Words));
        }

        return result;
    }
}
