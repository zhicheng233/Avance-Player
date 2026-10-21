using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Avance_Player.Services.Lyrics;

public readonly record struct TtmlTimeContext(double FrameRate = 30, double TickRate = 1) {
    public static TtmlTimeContext FromRoot(System.Xml.Linq.XElement root) {
        var frameRate = ReadDouble(root, "frameRate", 30);
        var tickRate = ReadDouble(root, "tickRate", 1);
        var multiplier = root.Attribute("frameRateMultiplier")
                         ?? root.Attributes().FirstOrDefault(a => a.Name.LocalName == "frameRateMultiplier");
        if (!string.IsNullOrWhiteSpace(multiplier?.Value)) {
            var parts = multiplier.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                && den != 0) {
                frameRate *= num / den;
            }
        }

        return new TtmlTimeContext(frameRate <= 0 ? 30 : frameRate, tickRate <= 0 ? 1 : tickRate);
    }

    private static double ReadDouble(System.Xml.Linq.XElement root, string localName, double fallback) {
        var attribute = root.Attribute(localName)
                        ?? root.Attributes().FirstOrDefault(a => a.Name.LocalName == localName);
        return attribute is not null
               && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}

public static class LyricsTime {
    private static readonly Regex LrcTimestamp = new(
        @"^\s*(\d{1,3}):(\d{1,2})(?:[.,](\d{1,3}))?\s*$", RegexOptions.Compiled);

    public static bool TryParseLrc(string value, out TimeSpan time) {
        time = default;
        var match = LrcTimestamp.Match(value);
        if (!match.Success) return false;

        var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var milliseconds = ParseFraction(match.Groups[3].Value);
        time = TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1_000 + milliseconds);
        return true;
    }

    public static bool TryParseAss(string value, out TimeSpan time) {
        time = default;
        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var second)) return false;

        int hours = 0;
        int minutes;
        int seconds;
        string fraction;
        if (parts.Length == 3) {
            hours = first;
            minutes = second;
            SplitSeconds(parts[2], out seconds, out fraction);
        }
        else {
            minutes = first;
            SplitSeconds(parts[1], out seconds, out fraction);
        }

        time = TimeSpan.FromMilliseconds(hours * 3_600_000 + minutes * 60_000 + seconds * 1_000 + ParseFraction(fraction));
        return true;
    }

    public static TimeSpan ParseTtml(string? value, TtmlTimeContext context, TimeSpan fallback = default) {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim();

        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMilliseconds(ParseDouble(text[..^2], fallback.TotalMilliseconds));
        if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(ParseDouble(text[..^1], fallback.TotalSeconds));
        if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(ParseDouble(text[..^1], fallback.TotalMinutes));
        if (text.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromHours(ParseDouble(text[..^1], fallback.TotalHours));
        if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(ParseDouble(text[..^1], 0) / context.FrameRate);
        if (text.EndsWith("t", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(ParseDouble(text[..^1], 0) / context.TickRate);

        var parts = text.Split(':');
        if (parts.Length == 2) {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return fallback;
            SplitSeconds(parts[1], out var seconds, out var fraction);
            return TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1_000 + ParseFraction(fraction));
        }

        if (parts.Length == 3) {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return fallback;
            SplitSeconds(parts[2], out var seconds, out var fraction);
            return TimeSpan.FromMilliseconds(hours * 3_600_000 + minutes * 60_000 + seconds * 1_000 + ParseFraction(fraction));
        }

        if (parts.Length == 4) {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames)) return fallback;
            return TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds + frames / context.FrameRate);
        }

        return TimeSpan.FromSeconds(ParseDouble(text, fallback.TotalSeconds));
    }

    public static int ParseOffsetMilliseconds(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : 0;
    }

    private static void SplitSeconds(string value, out int seconds, out string fraction) {
        var index = value.IndexOfAny(['.', ',']);
        if (index < 0) {
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
            fraction = string.Empty;
            return;
        }

        int.TryParse(value[..index], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
        fraction = value[(index + 1)..];
    }

    private static int ParseFraction(string value) {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var digits = value.Length > 3 ? value[..3] : value;
        var padded = digits.PadRight(3, '0');
        return int.TryParse(padded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            ? milliseconds
            : 0;
    }

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
