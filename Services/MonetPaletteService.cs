using System;
using System.Collections.Generic;
using System.Globalization;

namespace Avance_Player.Services;

public sealed record MonetPalette(
    string WindowBackground,
    string SidebarSurface,
    string IslandSurface,
    string Accent,
    string AccentSecondary,
    string Foreground,
    string MutedForeground);

/// <summary>
/// 从封面主色的种子生成一套像图里来的配色。以后接了真实封面采样，就把种子换掉；
/// 这个类本身不看像素。变换刻意让界面暖、稳，还会把紫色往琥珀色推，
/// 免得一张紫色封面把整个界面染紫。
/// </summary>
public static class MonetPaletteService {
    private static readonly Dictionary<string, MonetPalette> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static MonetPalette FromColorSeed(string? seed) {
        var cacheKey = string.IsNullOrWhiteSpace(seed) ? "#A58856" : seed;
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        var source = ParseHex(cacheKey, out var color) ? color : (R: 165, G: 136, B: 86);
        var (hue, saturation, _) = ToHsl(source.R, source.G, source.B);
        if (hue is >= 245 and <= 325) hue = 38;
        if (hue is >= 326 or <= 8) hue = 22;

        var accent = FromHsl(hue, Clamp(saturation * 0.62, 0.24, 0.44), 0.71);
        var secondary = FromHsl((hue + 58) % 360, Clamp(saturation * 0.46, 0.20, 0.36), 0.62);
        var background = Mix((R: 19, G: 19, B: 18), FromHsl(hue, 0.14, 0.19), 0.30);
        var sidebar = Mix((R: 27, G: 27, B: 25), FromHsl(hue, 0.12, 0.24), 0.26);
        var surface = Mix((R: 39, G: 38, B: 35), FromHsl(hue, 0.12, 0.28), 0.30);

        var palette = new MonetPalette(
            ToHex(background),
            ToHex(sidebar),
            ToHex(surface),
            ToHex(accent),
            ToHex(secondary),
            "#F3EFE6",
            "#A8A49B");
        Cache[cacheKey] = palette;
        return palette;
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Clamp(value, minimum, maximum);

    private static bool ParseHex(string value, out (int R, int G, int B) color) {
        color = default;
        var hex = value.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(int red, int green, int blue) {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var delta = maximum - minimum;
        var lightness = (maximum + minimum) / 2;
        if (delta == 0) return (0, 0, lightness);

        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = maximum == r ? 60 * (((g - b) / delta) % 6)
            : maximum == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, saturation, lightness);
    }

    private static (int R, int G, int B) FromHsl(double hue, double saturation, double lightness) {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = lightness - chroma / 2;
        var rgb = hue switch {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return (ToByte((rgb.Item1 + m) * 255), ToByte((rgb.Item2 + m) * 255), ToByte((rgb.Item3 + m) * 255));
    }

    private static (int R, int G, int B) Mix((int R, int G, int B) first, (int R, int G, int B) second, double amount) => (
        ToByte(first.R + (second.R - first.R) * amount),
        ToByte(first.G + (second.G - first.G) * amount),
        ToByte(first.B + (second.B - first.B) * amount));

    private static int ToByte(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

    private static string ToHex((int R, int G, int B) color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
