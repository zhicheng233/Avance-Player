using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// TTML lyrics as used by Apple Music and the AMLL TTML database. Supports word-timed
/// &lt;span&gt; elements, line-level paragraphs, inline translation/romanization spans, Apple
/// Music translation tables, and background-vocal spans (appended to the line for now).
/// </summary>
public sealed class TtmlLyricsParser : ILyricsParser {
    private const string TranslationRole = "x-translation";
    private const string RomanizationRole = "x-roman";
    private const string BackgroundRole = "x-bg";

    public LyricsFormat Format => LyricsFormat.Ttml;

    public bool CanParse(string content) {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith('<') && content.Contains("<tt", StringComparison.OrdinalIgnoreCase);
    }

    public LyricsDocument Parse(string content, LyricsParseContext context) {
        XDocument document;
        try {
            var settings = new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var stringReader = new StringReader(content);
            using var reader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException exception) {
            throw new LyricsParseException($"Invalid TTML/XML: {exception.Message}");
        }

        var root = document.Root ?? throw new LyricsParseException("The TTML file has no root element.");
        var timeContext = TtmlTimeContext.FromRoot(root);
        var translations = ReadTextMap(root, "translations");
        MergeTextMap(translations, ReadTextMap(root, "translation"));
        var transliterations = ReadTextMap(root, "transliterations");
        MergeTextMap(transliterations, ReadTextMap(root, "transliteration"));

        var lines = new List<LyricLine>();
        foreach (var paragraph in root.Descendants().Where(element => element.Name.LocalName == "p")) {
            var line = ParseParagraph(paragraph, timeContext, translations, transliterations);
            if (line is not null) lines.Add(line);
        }

        if (lines.Count == 0)
            throw new LyricsParseException("No lyric paragraphs were found in the TTML file.");

        var finalized = LyricsNormalizer.Finalize(lines, context.TrackDuration);
        return new LyricsDocument {
            Format = Format,
            Lines = LyricsNormalizer.FillWordEnds(finalized),
            Title = ReadMeta(root, "musicName"),
            Artist = ReadMeta(root, "artists"),
            Album = ReadMeta(root, "album")
        };
    }

    private static LyricLine? ParseParagraph(
        XElement paragraph,
        TtmlTimeContext timeContext,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyDictionary<string, string> transliterations) {
        var begin = ResolveBegin(paragraph, timeContext, TimeSpan.Zero);
        var end = ResolveEnd(paragraph, timeContext, begin);
        var key = Attribute(paragraph, "key");

        var builder = new LineBuilder();
        CollectChildren(paragraph, timeContext, begin, builder);

        if (builder.Background.Length > 0) {
            var background = " (" + builder.Background.ToString().Trim() + ")";
            if (builder.Words.Count > 0) {
                var last = builder.Words[^1];
                builder.Words[^1] = new LyricWord(last.Text + background, last.Start, last.End);
            }
            else {
                builder.Text.Append(background);
            }
        }

        var text = builder.Words.Count > 0
            ? LyricsNormalizer.TextFromWords(builder.Words)
            : builder.Text.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        var translation = builder.Translation.Length > 0
            ? builder.Translation.ToString().Trim()
            : key is not null ? translations.GetValueOrDefault(key) : null;
        var romanization = builder.Romanization.Length > 0
            ? builder.Romanization.ToString().Trim()
            : key is not null ? transliterations.GetValueOrDefault(key) : null;

        return new LyricLine(
            begin,
            text,
            string.IsNullOrWhiteSpace(translation) ? null : translation,
            string.IsNullOrWhiteSpace(romanization) ? null : romanization,
            end > begin ? end : null,
            builder.Words.Count > 0 ? builder.Words : null);
    }

    private static void CollectChildren(
        XElement parent,
        TtmlTimeContext timeContext,
        TimeSpan parentBegin,
        LineBuilder builder) {
        foreach (var node in parent.Nodes()) {
            switch (node) {
                case XText text:
                    AppendText(text.Value, builder);
                    break;
                case XElement element:
                    CollectElement(element, timeContext, parentBegin, builder);
                    break;
            }
        }
    }

    private static void CollectElement(
        XElement element,
        TtmlTimeContext timeContext,
        TimeSpan parentBegin,
        LineBuilder builder) {
        var role = Attribute(element, "role");
        switch (role) {
            case TranslationRole:
                builder.Translation.Append(CollectMainText(element));
                return;
            case RomanizationRole:
                builder.Romanization.Append(CollectMainText(element));
                return;
            case BackgroundRole:
                builder.Background.Append(CollectMainText(element));
                return;
        }

        if (element.Name.LocalName == "br") {
            AppendText(" ", builder);
            return;
        }

        if (HasTiming(element)) {
            if (HasTimedDescendant(element)) {
                CollectChildren(element, timeContext, parentBegin, builder);
                return;
            }

            var wordText = CollectMainText(element);
            if (wordText.Length == 0) return;

            CollectRoles(element, builder);
            var begin = ResolveBegin(element, timeContext, parentBegin);
            var end = ResolveEnd(element, timeContext, begin);
            var text = builder.Pending + wordText;
            builder.Pending = string.Empty;
            builder.Words.Add(new LyricWord(text, begin, end));
            builder.Text.Append(text);
            return;
        }

        CollectChildren(element, timeContext, parentBegin, builder);
    }

    private static void AppendText(string text, LineBuilder builder) {
        if (text.Length == 0) return;
        // Pretty-printed XML inserts newline+indent nodes between spans; those are formatting,
        // not lyric whitespace. A single space between spans is meaningful and kept.
        if (string.IsNullOrWhiteSpace(text) && text.Contains('\n')) return;

        builder.Text.Append(text);
        if (builder.Words.Count > 0) {
            var last = builder.Words[^1];
            builder.Words[^1] = new LyricWord(last.Text + text, last.Start, last.End);
        }
        else {
            builder.Pending += text;
        }
    }

    private static void CollectRoles(XElement element, LineBuilder builder) {
        foreach (var descendant in element.Descendants()) {
            switch (Attribute(descendant, "role")) {
                case TranslationRole:
                    builder.Translation.Append(CollectMainText(descendant));
                    break;
                case RomanizationRole:
                    builder.Romanization.Append(CollectMainText(descendant));
                    break;
                case BackgroundRole:
                    builder.Background.Append(CollectMainText(descendant));
                    break;
            }
        }
    }

    private static string CollectMainText(XElement element) {
        var builder = new StringBuilder();
        foreach (var node in element.Nodes()) {
            switch (node) {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child:
                    var role = Attribute(child, "role");
                    if (role is TranslationRole or RomanizationRole or BackgroundRole) continue;
                    builder.Append(CollectMainText(child));
                    break;
            }
        }
        return builder.ToString();
    }

    private static bool HasTiming(XElement element) =>
        Attribute(element, "begin") is not null
        || Attribute(element, "end") is not null
        || Attribute(element, "dur") is not null;

    private static bool HasTimedDescendant(XElement element) =>
        element.Descendants().Any(HasTiming);

    private static TimeSpan ResolveBegin(XElement element, TtmlTimeContext timeContext, TimeSpan fallback) {
        var begin = Attribute(element, "begin");
        if (begin is not null) return LyricsTime.ParseTtml(begin, timeContext, fallback);

        var current = element.Parent;
        while (current is not null) {
            var parentBegin = Attribute(current, "begin");
            if (parentBegin is not null) return LyricsTime.ParseTtml(parentBegin, timeContext, fallback);
            current = current.Parent;
        }
        return fallback;
    }

    private static TimeSpan ResolveEnd(XElement element, TtmlTimeContext timeContext, TimeSpan begin) {
        var end = Attribute(element, "end");
        if (end is not null) return LyricsTime.ParseTtml(end, timeContext, begin);

        var duration = Attribute(element, "dur");
        if (duration is not null) return begin + LyricsTime.ParseTtml(duration, timeContext, TimeSpan.Zero);
        return begin;
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static string? ReadMeta(XElement root, string key) {
        foreach (var meta in root.Descendants().Where(element => element.Name.LocalName == "meta")) {
            if (string.Equals(Attribute(meta, "key"), key, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(Attribute(meta, "value"))) {
                return Attribute(meta, "value");
            }
        }
        return null;
    }

    private static Dictionary<string, string> ReadTextMap(XElement root, string containerName) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var container in root.Descendants().Where(element => element.Name.LocalName == containerName)) {
            foreach (var text in container.Descendants().Where(element => element.Name.LocalName == "text")) {
                var key = Attribute(text, "for");
                if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key)) {
                    result[key] = text.Value.Trim();
                }
            }
        }
        return result;
    }

    private static void MergeTextMap(Dictionary<string, string> target, Dictionary<string, string> source) {
        foreach (var pair in source) {
            if (!target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value;
        }
    }

    private sealed class LineBuilder {
        public StringBuilder Text { get; } = new();
        public StringBuilder Translation { get; } = new();
        public StringBuilder Romanization { get; } = new();
        public StringBuilder Background { get; } = new();
        public List<LyricWord> Words { get; } = [];
        public string Pending { get; set; } = string.Empty;
    }
}
