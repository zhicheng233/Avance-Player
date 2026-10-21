using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Avance_Player.Controls;

/// <summary>
/// Draws the slow, blurred colour field behind the immersive lyrics view. The glow is built from
/// several very wide radial gradients anchored near the edges, which reads as soft blurred light
/// without requiring a shader. Blobs drift along independent sine paths so the field never loops
/// visibly; the centre is deliberately left calmer so the lyric column keeps its contrast.
/// </summary>
public sealed class FluidBackdrop : Control {
    public static readonly StyledProperty<IBrush?> BaseBrushProperty =
        AvaloniaProperty.Register<FluidBackdrop, IBrush?>(nameof(BaseBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<FluidBackdrop, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty =
        AvaloniaProperty.Register<FluidBackdrop, IBrush?>(nameof(SecondaryBrush));

    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<FluidBackdrop, double>(nameof(Phase));

    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<FluidBackdrop, double>(nameof(GlowOpacity), 1.0);

    private Color _accentColor = Color.Parse("#B9A47F");
    private Color _secondaryColor = Color.Parse("#87977F");
    private bool _hasColors;
    private DateTime _lastRenderUtc;

    static FluidBackdrop() {
        AffectsRender<FluidBackdrop>(BaseBrushProperty, AccentBrushProperty, SecondaryBrushProperty,
            PhaseProperty, GlowOpacityProperty);
    }

    public IBrush? BaseBrush {
        get => GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public IBrush? AccentBrush {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? SecondaryBrush {
        get => GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public double Phase {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public double GlowOpacity {
        get => GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        context.DrawRectangle(BaseBrush ?? new SolidColorBrush(Color.Parse("#121110")), null, bounds);
        using (context.PushClip(bounds)) {
            var phase = Phase;
            var w = bounds.Width;
            var h = bounds.Height;
            var opacity = Math.Clamp(GlowOpacity, 0, 1);

            // The UI palette is deliberately muted so chrome stays warm and neutral. The ambient
            // field instead re-derives vivid tones from the same two hues: keep the hue, raise
            // saturation and pin a mid lightness. Without that step the glow reads as grey.
            var accentTarget = GetColor(AccentBrush, Color.Parse("#B9A47F"));
            var secondaryTarget = GetColor(SecondaryBrush, Color.Parse("#87977F"));
            var now = DateTime.UtcNow;
            if (!_hasColors) {
                _accentColor = accentTarget;
                _secondaryColor = secondaryTarget;
                _hasColors = true;
                _lastRenderUtc = now;
            }
            else {
                // Ease toward the new track's palette on the ambient tick, so switching songs
                // washes the field across instead of snapping it.
                var delta = Math.Clamp((now - _lastRenderUtc).TotalSeconds, 0, 0.2);
                _lastRenderUtc = now;
                var amount = 1 - Math.Exp(-3.2 * delta);
                _accentColor = Blend(_accentColor, accentTarget, amount);
                _secondaryColor = Blend(_secondaryColor, secondaryTarget, amount);
            }

            var (accentHue, _) = ToHsl(_accentColor);
            var (secondaryHue, _) = ToHsl(_secondaryColor);

            var highlight = FromHsl(WrapHue(accentHue - 22), 0.74, 0.60);
            var accent = FromHsl(accentHue, 0.72, 0.50);
            var secondary = FromHsl(WrapHue(secondaryHue + 16), 0.70, 0.47);
            var depth = FromHsl(WrapHue(secondaryHue + 48), 0.66, 0.33);

            // Corner-anchored field: vibrant at the edges, calmer through the middle where the
            // lyric column sits. Radii are generous so the blobs merge into a single gradient mesh.
            Blob(context, highlight, opacity * 0.95,
                w * (-0.26 + Math.Sin(phase * 0.51) * 0.05),
                h * (-0.30 + Math.Cos(phase * 0.43) * 0.05), w * 0.92, h * 1.00);

            Blob(context, accent, opacity,
                w * (0.36 + Math.Cos(phase * 0.37) * 0.06),
                h * (-0.22 + Math.Sin(phase * 0.61) * 0.07), w * 1.00, h * 1.08);

            Blob(context, secondary, opacity * 0.98,
                w * (-0.28 + Math.Cos(phase * 0.45 + 1.1) * 0.06),
                h * (0.46 + Math.Sin(phase * 0.53 + 0.7) * 0.06), w * 1.06, h * 1.06);

            Blob(context, depth, opacity * 0.90,
                w * (0.34 + Math.Sin(phase * 0.33 + 2.0) * 0.07),
                h * (0.48 + Math.Cos(phase * 0.47 + 1.5) * 0.06), w * 1.00, h * 1.04);

            // Vignette: keeps a soft dark frame so the glow reads as light rather than flat fill.
            var vignette = new RadialGradientBrush {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.82, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.86, RelativeUnit.Relative),
                GradientStops = [
                    new GradientStop(Color.FromArgb(0, 10, 9, 8), 0.34),
                    new GradientStop(Color.FromArgb(54, 10, 9, 8), 1)
                ]
            };
            context.DrawRectangle(vignette, null, bounds);

            // Contrast scrim behind the lyric column only. A wide, very soft horizontal gradient
            // keeps the ambient glow visible on both sides without letting the brightest blob
            // reduce lyric legibility; it fades out well before the album art.
            var scrim = new LinearGradientBrush {
                StartPoint = new RelativePoint(0.40, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.86, 0, RelativeUnit.Relative),
                GradientStops = [
                    new GradientStop(Color.FromArgb(0, 12, 11, 9), 0),
                    new GradientStop(Color.FromArgb(58, 12, 11, 9), 0.22),
                    new GradientStop(Color.FromArgb(58, 12, 11, 9), 0.78),
                    new GradientStop(Color.FromArgb(0, 12, 11, 9), 1)
                ]
            };
            context.DrawRectangle(scrim, null, bounds);
        }
    }

    /// <summary>
    /// Paints one soft blob. <paramref name="x"/>/<paramref name="y"/> are the top-left of the
    /// ellipse; the gradient fades from the centre outwards so overlapping blobs blend into a mesh.
    /// </summary>
    private static void Blob(DrawingContext context, Color color, double opacity,
        double x, double y, double width, double height) {
        if (opacity <= 0) return;
        byte Alpha(double value) => (byte)Math.Clamp(Math.Round(255 * opacity * value), 0, 255);
        // RadiusX/Y are relative to the ellipse's bounding box *width*, so 0.5 makes the
        // gradient reach zero exactly at the ellipse boundary. A larger value would leave a
        // visible arc where the clipped ellipse cuts a still-opaque gradient.
        var brush = new RadialGradientBrush {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops = [
                new GradientStop(Color.FromArgb(Alpha(0.62), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(Alpha(0.52), color.R, color.G, color.B), 0.20),
                new GradientStop(Color.FromArgb(Alpha(0.38), color.R, color.G, color.B), 0.40),
                new GradientStop(Color.FromArgb(Alpha(0.24), color.R, color.G, color.B), 0.58),
                new GradientStop(Color.FromArgb(Alpha(0.12), color.R, color.G, color.B), 0.76),
                new GradientStop(Color.FromArgb(Alpha(0.04), color.R, color.G, color.B), 0.90),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            ]
        };
        context.DrawEllipse(brush, null, new Rect(x, y, width, height));
    }

    private static double WrapHue(double hue) {
        hue %= 360;
        return hue < 0 ? hue + 360 : hue;
    }

    private static (double Hue, double Saturation) ToHsl(Color color) {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var delta = maximum - minimum;
        if (delta <= 0) return (0, 0);
        var lightness = (maximum + minimum) / 2;
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = maximum == r ? 60 * (((g - b) / delta) % 6)
            : maximum == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return (WrapHue(hue), saturation);
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
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));

        static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    }

    private static Color GetColor(IBrush? brush, Color fallback) =>
        brush is ISolidColorBrush solid ? solid.Color : fallback;

    private static Color Blend(Color from, Color to, double amount) {
        byte Channel(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * amount), 0, 255);
        return Color.FromArgb(
            Channel(from.A, to.A),
            Channel(from.R, to.R),
            Channel(from.G, to.G),
            Channel(from.B, to.B));
    }
}
