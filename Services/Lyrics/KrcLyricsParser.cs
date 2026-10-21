using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// Kugou KRC karaoke lyrics. KRC files are commonly a base64 or binary payload: four magic bytes
/// followed by an XOR-obfuscated zlib stream. Word offsets in the decoded text are relative to
/// the line start. The optional [language:...] JSON blob supplies translations/romanization.
/// </summary>
public sealed class KrcLyricsParser : ILyricsParser {
    private static readonly byte[] XorKey = [
        64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105
    ];

    private static readonly Regex MetadataPattern = new(
        @"^\s*\[(?<key>[a-zA-Z#]+):(?<value>.*)\]\s*$", RegexOptions.Compiled);

    private static readonly Regex LinePattern = new(
        @"^\s*\[(?<start>\d+),(?<duration>\d+)\](?<body>.*)$", RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(
        @"<(?<start>\d+),(?<duration>\d+)(?:,\d+)?>(?<text>[^<]*)", RegexOptions.Compiled);

    private static readonly Regex LanguagePattern = new(
        @"^\s*\[language:(?<value>.+)\]\s*$", RegexOptions.Compiled);

    public LyricsFormat Format => LyricsFormat.Krc;

    public bool CanParse(string content) =>
        LinePattern.IsMatch(content) && content.Contains('<');

    public LyricsDocument Parse(string content, LyricsParseContext context) =>
        ParseText(DecodeKrc(Encoding.UTF8.GetBytes(content)), context);

    /// <summary>Used by the registry so binary KRC files are decrypted before text decoding.</summary>
    public LyricsDocument ParseBytes(byte[] bytes, LyricsParseContext context) =>
        ParseText(DecodeKrc(bytes), context);

    private LyricsDocument ParseText(string content, LyricsParseContext context) {
        var lines = new List<LyricLine>();
        var title = default(string);
        var artist = default(string);
        var album = default(string);
        var offset = 0;
        var translations = new List<string>();
        var romanizations = new List<string>();

        foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var language = LanguagePattern.Match(line);
            if (language.Success) {
                ReadLanguageMetadata(language.Groups["value"].Value, translations, romanizations);
                continue;
            }

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
            foreach (Match word in WordPattern.Matches(body)) {
                var wordOffset = TimeSpan.FromMilliseconds(ParseLong(word.Groups["start"].Value));
                var wordDuration = TimeSpan.FromMilliseconds(ParseLong(word.Groups["duration"].Value));
                var wordText = word.Groups["text"].Value;
                if (wordText.Length == 0) continue;
                var start = lineStart + wordOffset;
                words.Add(new LyricWord(wordText, start, start + wordDuration));
            }

            var normalized = LyricsNormalizer.NormalizeWords(words).ToList();
            var text = normalized.Count > 0
                ? LyricsNormalizer.TextFromWords(normalized)
                : Regex.Replace(body, "<[^>]+>", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var end = lineDuration > TimeSpan.Zero ? lineStart + lineDuration : (TimeSpan?)null;
            lines.Add(new LyricLine(lineStart, text, end: end, words: normalized));
        }

        if (lines.Count == 0)
            throw new LyricsParseException("No timestamped lyric lines were found in the KRC file.");

        var finalized = LyricsNormalizer.Finalize(lines, context.TrackDuration).ToList();
        finalized = AttachEmbeddedText(finalized, translations, romanizations);
        return new LyricsDocument {
            Format = Format,
            Lines = LyricsNormalizer.FillWordEnds(finalized),
            Title = title,
            Artist = artist,
            Album = album,
            Offset = TimeSpan.FromMilliseconds(offset)
        };
    }

    private static List<LyricLine> AttachEmbeddedText(
        List<LyricLine> lines,
        IReadOnlyList<string> translations,
        IReadOnlyList<string> romanizations) {
        var result = new List<LyricLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++) {
            var line = lines[index];
            var translation = index < translations.Count && !string.IsNullOrWhiteSpace(translations[index])
                ? translations[index]
                : null;
            var romanization = index < romanizations.Count && !string.IsNullOrWhiteSpace(romanizations[index])
                ? romanizations[index]
                : null;
            result.Add(translation is null && romanization is null
                ? line
                : new LyricLine(line.Timestamp, line.Text, translation, romanization, line.End, line.Words));
        }
        return result;
    }

    private static void ReadLanguageMetadata(string encoded, List<string> translations, List<string> romanizations) {
        try {
            var clean = encoded.Trim();
            while (clean.Length % 4 != 0) clean += "=";
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(clean));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) return;

            foreach (var item in content.EnumerateArray()) {
                if (!item.TryGetProperty("type", out var typeElement)
                    || !typeElement.TryGetInt32(out var type)
                    || !item.TryGetProperty("lyricContent", out var lyricContent)
                    || lyricContent.ValueKind != JsonValueKind.Array) continue;

                var target = type == 1 ? translations : type == 0 ? romanizations : null;
                if (target is null) continue;

                foreach (var entry in lyricContent.EnumerateArray()) {
                    if (entry.ValueKind == JsonValueKind.Array) {
                        var pieces = entry.EnumerateArray()
                            .Where(piece => piece.ValueKind == JsonValueKind.String)
                            .Select(piece => piece.GetString())
                            .Where(piece => piece is not null);
                        target.Add(string.Concat(pieces));
                    }
                    else if (entry.ValueKind == JsonValueKind.String) {
                        target.Add(entry.GetString() ?? string.Empty);
                    }
                    else {
                        target.Add(string.Empty);
                    }
                }
            }
        }
        catch (Exception) {
            // Translation metadata is optional; keep the lyric lines if it is malformed.
        }
    }

    private static string DecodeKrc(byte[] bytes) {
        if (bytes.Length == 0) return string.Empty;

        byte[] payload = bytes;
        if (bytes.Length >= 4 && bytes[0] == (byte)'k' && bytes[1] == (byte)'r' && bytes[2] == (byte)'c' && bytes[3] == (byte)'1') {
            payload = bytes[4..];
        }
        else {
            // Hand-edited/exported KRC files are plain text, sometimes with a UTF-8 BOM.
            var decodedText = LyricsTextDecoder.Decode(bytes);
            if (decodedText.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('['))
                return decodedText;

            try {
                var text = decodedText.Trim();
                var decoded = Convert.FromBase64String(text);
                if (decoded.Length <= 4)
                    throw new LyricsParseException("The KRC payload is too short.");
                payload = decoded;
                if (decoded[0] == (byte)'k' && decoded[1] == (byte)'r' && decoded[2] == (byte)'c' && decoded[3] == (byte)'1')
                    payload = decoded[4..];
            }
            catch (FormatException) {
                throw new LyricsParseException("The KRC file is neither plain text nor a valid base64 payload.");
            }
        }

        try {
            var decrypted = new byte[payload.Length];
            for (var index = 0; index < payload.Length; index++)
                decrypted[index] = (byte)(payload[index] ^ XorKey[index % XorKey.Length]);

            using var input = new MemoryStream(decrypted);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException) {
            throw new LyricsParseException("The KRC file could not be decompressed. It may be corrupt or use an unsupported variant.");
        }
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
