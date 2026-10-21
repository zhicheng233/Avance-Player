using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// Advanced SubStation Alpha karaoke. ASS timing is carried by \k / \kf / \ko tags in
/// centiseconds; files without karaoke tags remain line-level. Separate translation/romaji
/// dialogue lines are paired with the main line when their start times match.
/// </summary>
public sealed class AssLyricsParser : ILyricsParser {
    private static readonly Regex OverrideBlockPattern = new(@"\{(?<content>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex KaraokeTagPattern = new(
        @"\\[kK](?:[fot])?(?<duration>\d+)", RegexOptions.Compiled);

    private static readonly string[] DefaultFormat = [
        "Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"
    ];

    public LyricsFormat Format => LyricsFormat.Ass;

    public bool CanParse(string content) =>
        content.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
        || content.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase);

    public LyricsDocument Parse(string content, LyricsParseContext context) {
        var inEvents = false;
        var format = DefaultFormat;
        var parsed = new List<ParsedAssLine>();

        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[", StringComparison.Ordinal)) {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents) continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) {
                var fields = line["Format:".Length..]
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0) format = fields;
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            var payload = line["Dialogue:".Length..].TrimStart();
            var parts = payload.Split(',', format.Length);
            if (parts.Length < format.Length) continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < format.Length; index++)
                values[format[index]] = parts[index];

            if (!values.TryGetValue("Start", out var startText)
                || !values.TryGetValue("End", out var endText)
                || !values.TryGetValue("Text", out var rawText)) continue;

            if (!LyricsTime.TryParseAss(startText, out var start)
                || !LyricsTime.TryParseAss(endText, out var end)) continue;

            var (text, words) = ParseKaraoke(rawText, start);
            if (string.IsNullOrWhiteSpace(text)) continue;

            parsed.Add(new ParsedAssLine(
                new LyricLine(start, text, end: end, words: words),
                values.GetValueOrDefault("Style", string.Empty),
                values.GetValueOrDefault("Name", string.Empty)));
        }

        if (parsed.Count == 0)
            throw new LyricsParseException("No dialogue lines were found in the ASS file.");

        var lines = PairTranslationLines(parsed);
        return new LyricsDocument {
            Format = Format,
            Lines = LyricsNormalizer.Finalize(lines, context.TrackDuration)
        };
    }

    private static (string Text, IReadOnlyList<LyricWord> Words) ParseKaraoke(string rawText, TimeSpan lineStart) {
        var wordBuilders = new List<WordBuilder>();
        WordBuilder? current = null;
        var plain = new StringBuilder();
        var cursor = lineStart;
        var position = 0;

        foreach (Match block in OverrideBlockPattern.Matches(rawText)) {
            var segment = CleanAssText(rawText[position..block.Index]);
            if (segment.Length > 0) {
                if (current is not null) current.Text.Append(segment);
                else plain.Append(segment);
            }

            var karaokeTags = KaraokeTagPattern.Matches(block.Groups["content"].Value);
            foreach (Match tag in karaokeTags) {
                if (!int.TryParse(tag.Groups["duration"].Value, out var centiseconds)) continue;
                var duration = TimeSpan.FromMilliseconds(centiseconds * 10d);
                if (current is not null && current.Text.Length > 0) wordBuilders.Add(current);
                current = new WordBuilder(cursor, cursor + duration);
                cursor += duration;
            }

            position = block.Index + block.Length;
        }

        var tail = CleanAssText(rawText[position..]);
        if (tail.Length > 0) {
            if (current is not null) current.Text.Append(tail);
            else plain.Append(tail);
        }
        if (current is not null && current.Text.Length > 0) wordBuilders.Add(current);

        var words = wordBuilders
            .Select(word => new LyricWord(word.Text.ToString(), word.Start, word.End))
            .ToList();
        var normalized = LyricsNormalizer.NormalizeWords(words).ToList();

        if (normalized.Count > 0 && plain.Length > 0) {
            var first = normalized[0];
            normalized[0] = new LyricWord(plain + first.Text, first.Start, first.End);
        }

        var text = normalized.Count > 0
            ? LyricsNormalizer.TextFromWords(normalized)
            : plain.ToString();
        return (text, normalized);
    }

    private static string CleanAssText(string value) => value
        .Replace("\\N", " ", StringComparison.Ordinal)
        .Replace("\\n", " ", StringComparison.Ordinal)
        .Replace("\\h", " ", StringComparison.Ordinal);

    private static IReadOnlyList<LyricLine> PairTranslationLines(List<ParsedAssLine> parsed) {
        var result = new List<LyricLine>();
        foreach (var group in parsed.GroupBy(item => item.Line.Timestamp).OrderBy(group => group.Key)) {
            var items = group.ToList();
            var mains = items.Where(item => !IsTranslation(item) && !IsRomanization(item)).ToList();
            if (mains.Count == 0) {
                result.AddRange(items.Select(item => item.Line));
                continue;
            }

            // A common bilingual ASS uses two unlabeled styles, one Chinese and one Latin.
            if (mains.Count == 2) {
                var first = mains[0];
                var second = mains[1];
                var firstCjk = ContainsCjk(first.Line.Text);
                var secondCjk = ContainsCjk(second.Line.Text);
                if (firstCjk != secondCjk) {
                    var translation = firstCjk ? first.Line.Text : second.Line.Text;
                    var main = firstCjk ? second : first;
                    result.Add(new LyricLine(main.Line.Timestamp, main.Line.Text, translation,
                        romanization: null, main.Line.End, main.Line.Words));
                    continue;
                }
            }

            foreach (var main in mains) {
                var translation = items
                    .Where(item => !ReferenceEquals(item, main) && IsTranslation(item))
                    .Select(item => item.Line.Text)
                    .FirstOrDefault();
                var romanization = items
                    .Where(item => !ReferenceEquals(item, main) && IsRomanization(item))
                    .Select(item => item.Line.Text)
                    .FirstOrDefault();
                result.Add(new LyricLine(main.Line.Timestamp, main.Line.Text, translation, romanization,
                    main.Line.End, main.Line.Words));
            }
        }

        return result;
    }

    private static bool IsTranslation(ParsedAssLine item) =>
        ContainsAny(item.Style, "trans", "zh", "chi", "中文", "翻译")
        || ContainsAny(item.Name, "trans", "zh", "chi", "中文", "翻译");

    private static bool IsRomanization(ParsedAssLine item) =>
        ContainsAny(item.Style, "roman", "romaji", "pinyin")
        || ContainsAny(item.Name, "roman", "romaji", "pinyin");

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsCjk(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');

    private sealed record ParsedAssLine(LyricLine Line, string Style, string Name);

    private sealed class WordBuilder(TimeSpan start, TimeSpan end) {
        public StringBuilder Text { get; } = new();
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; } = end;
    }
}
