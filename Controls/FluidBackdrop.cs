using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Avance_Player.Controls;

/// <summary>
/// 沉浸歌词背后的慢速模糊色场。光晕是几块很宽的径向渐变，贴着边缘放，
/// 不用 shader 也有柔和的光感。色块各自按正弦轨迹漂，周期错开，看起来不会明显循环；
/// 中间特意压暗一点，歌词那列才看得清。
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

    // 每帧重建画的画刷很贵（一次 6 条径向渐变、约 40 个 GradientStop），
    // 色块位置只在 Phase 变化时才动，配色也只在切歌那几秒里慢慢漂。
    // 所以把四个色块和两层遮罩都缓存住，颜色没变就直接复用。
    private readonly RadialGradientBrush[] _blobs = new RadialGradientBrush[4];
    private readonly Color[] _blobColors = new Color[4];
    private readonly double[] _blobOpacity = [-1, -1, -1, -1];
    private RadialGradientBrush? _vignette;
    private LinearGradientBrush? _scrim;

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

            // UI 那套颜色本来就压得比较灰，界面才显得暖、稳。这里反过来，从同两个色相里重新提亮：
            // 色相不动，饱和度拉高，明度卡在中间。少了这步光晕会发灰。
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
                // 环境计时器每跳一次就往新封面的配色靠一点，切歌时是慢慢晕过去，不是啪一下换色。
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

            // 色块都挂在四角：边缘艳一点，中间歌词那列的位置压平。
            // 半径给得很大，几块光才能糊成一片。
            Blob(context, 0, highlight, opacity * 0.95,
                w * (-0.26 + Math.Sin(phase * 0.51) * 0.05),
                h * (-0.30 + Math.Cos(phase * 0.43) * 0.05), w * 0.92, h * 1.00);

            Blob(context, 1, accent, opacity,
                w * (0.36 + Math.Cos(phase * 0.37) * 0.06),
                h * (-0.22 + Math.Sin(phase * 0.61) * 0.07), w * 1.00, h * 1.08);

            Blob(context, 2, secondary, opacity * 0.98,
                w * (-0.28 + Math.Cos(phase * 0.45 + 1.1) * 0.06),
                h * (0.46 + Math.Sin(phase * 0.53 + 0.7) * 0.06), w * 1.06, h * 1.06);

            Blob(context, 3, depth, opacity * 0.90,
                w * (0.34 + Math.Sin(phase * 0.33 + 2.0) * 0.07),
                h * (0.48 + Math.Cos(phase * 0.47 + 1.5) * 0.06), w * 1.00, h * 1.04);

            // 暗角。加一圈很淡的暗边，光看起来才像光，不然就是一块平涂。
            context.DrawRectangle(GetVignette(), null, bounds);

            // 只在歌词列后面加一层对比遮罩。横向渐变拉得很宽很软，两边光还看得到，
            // 但最亮的那块不会把字压没；到专辑封面之前就已经淡完了。
            context.DrawRectangle(GetScrim(), null, bounds);
        }
    }

    private RadialGradientBrush GetVignette() => _vignette ??= new RadialGradientBrush {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.82, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.86, RelativeUnit.Relative),
        GradientStops = [
            new GradientStop(Color.FromArgb(0, 10, 9, 8), 0.34),
            new GradientStop(Color.FromArgb(54, 10, 9, 8), 1)
        ]
    };

    private LinearGradientBrush GetScrim() => _scrim ??= new LinearGradientBrush {
        StartPoint = new RelativePoint(0.40, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.86, 0, RelativeUnit.Relative),
        GradientStops = [
            new GradientStop(Color.FromArgb(0, 12, 11, 9), 0),
            new GradientStop(Color.FromArgb(58, 12, 11, 9), 0.22),
            new GradientStop(Color.FromArgb(58, 12, 11, 9), 0.78),
            new GradientStop(Color.FromArgb(0, 12, 11, 9), 1)
        ]
    };

    /// <summary>
    /// 画一块软光斑。<paramref name="x"/>/<paramref name="y"/> 是椭圆左上角，
    /// 渐变从中心往外淡，叠在一起就像一团雾。
    /// 画刷按颜色缓存：位置每帧都在动，但渐变本身只跟颜色和透明度有关。
    /// </summary>
    private void Blob(DrawingContext context, int slot, Color color, double opacity,
        double x, double y, double width, double height) {
        if (opacity <= 0) return;

        var brush = _blobs[slot];
        if (brush is null || _blobColors[slot] != color || Math.Abs(_blobOpacity[slot] - opacity) > 0.002) {
            brush = CreateBlobBrush(color, opacity);
            _blobs[slot] = brush;
            _blobColors[slot] = color;
            _blobOpacity[slot] = opacity;
        }

        context.DrawEllipse(brush, null, new Rect(x, y, width, height));
    }

    private static RadialGradientBrush CreateBlobBrush(Color color, double opacity) {
        byte Alpha(double value) => (byte)Math.Clamp(Math.Round(255 * opacity * value), 0, 255);
        // RadiusX/Y 是相对椭圆外接框宽度的，所以 0.5 正好在椭圆边上降到 0。
        // 给大了会在裁切边缘留下一圈还实着的渐变弧。
        return new RadialGradientBrush {
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
