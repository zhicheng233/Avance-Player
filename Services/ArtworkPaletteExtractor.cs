using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avance_Player.Services;

/// <summary>
/// A small set of cover-derived colours. <see cref="DominantColor"/> is the average of the
/// strongest hue cluster and is suitable for the muted UI palette; the accent colours are
/// slightly more vivid so the immersive fluid field reads as light from the artwork.
/// </summary>
public sealed record ArtworkPalette(
    string DominantColor,
    string AccentColor,
    string SecondaryColor,
    string BackgroundColor);

/// <summary>
/// Extracts a stable colour palette from album artwork by sampling a downscaled copy. This is
/// deliberately a histogram of hue clusters rather than a single average: averaging a cover with
/// a large dark background and a small bright subject usually produces a muddy grey.
/// </summary>
public static class ArtworkPaletteExtractor {
    private const int SampleSize = 64;
    private const int HueBins = 24;
    private const double MinimumSaturation = 0.12;
    private const double MinimumLightness = 0.08;
    private const double MaximumLightness = 0.92;

    public static ArtworkPalette? Extract(Bitmap? artwork) {
        if (artwork is null) return null;

        Bitmap? scaled = null;
        try {
            var source = artwork.PixelSize;
            if (source.Width <= 0 || source.Height <= 0) return null;

            var scale = Math.Min(1.0, (double)SampleSize / Math.Max(source.Width, source.Height));
            var sample = artwork;
            if (scale < 0.999) {
                var target = new PixelSize(
                    Math.Max(1, (int)Math.Round(source.Width * scale)),
                    Math.Max(1, (int)Math.Round(source.Height * scale)));
                scaled = artwork.CreateScaledBitmap(target, BitmapInterpolationMode.MediumQuality);
                sample = scaled;
            }

            var bins = ReadHueBins(sample);
            return BuildPalette(bins);
        }
        catch (Exception) {
            // Artwork is optional. A failed sample should fall back to the path-derived swatch
            // instead of making an import fail.
            return null;
        }
        finally {
            scaled?.Dispose();
        }
    }

    private static HueBin[] ReadHueBins(Bitmap bitmap) {
        var size = bitmap.PixelSize;
        using var writable = new WriteableBitmap(size, new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = writable.Lock();
        bitmap.CopyPixels(framebuffer);

        var stride = framebuffer.RowBytes;
        var bytes = new byte[stride * framebuffer.Size.Height];
        Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);

        var bins = new HueBin[HueBins];
        for (var index = 0; index < bins.Length; index++) bins[index] = new HueBin(index);

        for (var y = 0; y < size.Height; y++) {
            var row = y * stride;
            for (var x = 0; x < size.Width; x++) {
                var offset = row + x * 4;
                var alpha = bytes[offset + 3];
                if (alpha < 32) continue;

                var blue = bytes[offset];
                var green = bytes[offset + 1];
                var red = bytes[offset + 2];
                var (hue, saturation, lightness) = ToHsl(red, green, blue);
                if (lightness < MinimumLightness || lightness > MaximumLightness) continue;
                if (saturation < MinimumSaturation) continue;

                var bin = (int)(hue / (360.0 / HueBins)) % HueBins;
                bins[bin].Add(red, green, blue, saturation, lightness);
            }
        }

        return bins;
    }

    private static ArtworkPalette? BuildPalette(HueBin[] bins) {
        var total = 0;
        for (var index = 0; index < bins.Length; index++) total += bins[index].Count;
        if (total < 8) return null;

        HueBin? strongest = null;
        for (var index = 0; index < bins.Length; index++) {
            var bin = bins[index];
            if (bin.Count == 0) continue;
            if (strongest is null || bin.Weight > strongest.Weight) strongest = bin;
        }

        if (strongest is null) return null;

        // Prefer a genuinely different hue for the secondary blob. If the cover is nearly
        // monochrome, derive a companion hue so the field still has depth.
        HueBin? companion = null;
        for (var index = 0; index < bins.Length; index++) {
            var bin = bins[index];
            if (bin.Count == 0 || ReferenceEquals(bin, strongest)) continue;
            if (HueDistance(bin.CenterHue, strongest.CenterHue) < 45) continue;
            if (companion is null || bin.Weight > companion.Weight) companion = bin;
        }

        var dominant = strongest.AverageColor;
        var accent = FromHsl(strongest.CenterHue,
            Clamp(strongest.AverageSaturation * 1.35, 0.52, 0.84),
            Clamp(strongest.AverageLightness, 0.42, 0.62));
        var secondary = companion is null
            ? FromHsl(WrapHue(strongest.CenterHue + 58),
                Clamp(strongest.AverageSaturation * 1.20, 0.46, 0.78),
                Clamp(strongest.AverageLightness, 0.40, 0.58))
            : FromHsl(companion.CenterHue,
                Clamp(companion.AverageSaturation * 1.30, 0.46, 0.80),
                Clamp(companion.AverageLightness, 0.40, 0.60));

        // A dark tint of the strongest cover hue anchors the whole fluid field. It stays dark
        // enough for the lyric column while removing the warm neutral wash that made the page
        // read as unrelated to the artwork.
        var background = FromHsl(strongest.CenterHue,
            Clamp(strongest.AverageSaturation * 0.34, 0.10, 0.26),
            0.10);

        return new ArtworkPalette(ToHex(dominant), ToHex(accent), ToHex(secondary), ToHex(background));
    }

    private static double HueDistance(double first, double second) {
        var delta = Math.Abs(first - second) % 360;
        return Math.Min(delta, 360 - delta);
    }

    private static double WrapHue(double hue) {
        hue %= 360;
        return hue < 0 ? hue + 360 : hue;
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Clamp(value, minimum, maximum);

    private static (double Hue, double Saturation, double Lightness) ToHsl(byte red, byte green, byte blue) {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var delta = maximum - minimum;
        var lightness = (maximum + minimum) / 2;
        if (delta <= 0) return (0, 0, lightness);

        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = maximum == r ? 60 * (((g - b) / delta) % 6)
            : maximum == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return (WrapHue(hue), saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness) {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - chroma / 2;
        var (r, g, b) = hue switch {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static byte ToChannelByte(double value) =>
        (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private sealed class HueBin(int index) {
        private double _sumRed;
        private double _sumGreen;
        private double _sumBlue;
        private double _sumSaturation;
        private double _sumLightness;

        public int Index { get; } = index;
        public int Count { get; private set; }
        public double CenterHue => (Index + 0.5) * (360.0 / HueBins);
        public double AverageSaturation => Count == 0 ? 0 : _sumSaturation / Count;
        public double AverageLightness => Count == 0 ? 0 : _sumLightness / Count;
        public Color AverageColor => Color.FromRgb(
            ToChannelByte(_sumRed / Count),
            ToChannelByte(_sumGreen / Count),
            ToChannelByte(_sumBlue / Count));

        // Area matters most; saturation only breaks ties between similarly sized clusters.
        public double Weight => Count * (0.35 + AverageSaturation * 0.65);

        public void Add(byte red, byte green, byte blue, double saturation, double lightness) {
            Count++;
            _sumRed += red;
            _sumGreen += green;
            _sumBlue += blue;
            _sumSaturation += saturation;
            _sumLightness += lightness;
        }
    }
}
