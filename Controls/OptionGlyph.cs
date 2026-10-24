using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Avance_Player.Controls;

public enum OptionGlyphKind {
    Sliders,
    Speed,
    Loop,
    Clock
}

/// <summary>
/// “更多播放选项”小菜单里的单色图标。用几何图形画，颜色跟着当前 palette 的前景色走，
/// 不依赖字体里有没有这个符号。
/// </summary>
public sealed class OptionGlyph : TemplatedControl {
    public static readonly StyledProperty<OptionGlyphKind> KindProperty =
        AvaloniaProperty.Register<OptionGlyph, OptionGlyphKind>(nameof(Kind));

    static OptionGlyph() {
        AffectsRender<OptionGlyph>(KindProperty, ForegroundProperty);
    }

    public OptionGlyphKind Kind {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var brush = Foreground ?? Brushes.Gray;
        var pen = new Pen(brush, Math.Max(1.1, width * 0.085));

        switch (Kind) {
            case OptionGlyphKind.Sliders:
                DrawSliders(context, brush, pen, width, height);
                break;
            case OptionGlyphKind.Speed:
                DrawSpeed(context, brush, pen, width, height);
                break;
            case OptionGlyphKind.Loop:
                DrawLoop(context, pen, width, height);
                break;
            default:
                DrawClock(context, brush, pen, width, height);
                break;
        }
    }

    private static void DrawSliders(DrawingContext context, IBrush brush, Pen pen, double width, double height) {
        double[] xs = [width * 0.22, width * 0.5, width * 0.78];
        double[] knobY = [height * 0.36, height * 0.64, height * 0.30];
        var knobRadius = Math.Max(1.2, width * 0.09);
        for (var index = 0; index < xs.Length; index++) {
            context.DrawLine(pen, new Point(xs[index], height * 0.12), new Point(xs[index], height * 0.88));
            context.DrawEllipse(brush, null, new Point(xs[index], knobY[index]), knobRadius, knobRadius);
        }
    }

    private static void DrawSpeed(DrawingContext context, IBrush brush, Pen pen, double width, double height) {
        var center = new Point(width * 0.5, height * 0.6);
        var radius = new Size(width * 0.4, width * 0.4);
        var gauge = new StreamGeometry();
        using (var builder = gauge.Open()) {
            builder.BeginFigure(PointOnCircle(center, radius, 165), false);
            builder.ArcTo(PointOnCircle(center, radius, 15), radius, 0, false, SweepDirection.Clockwise);
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, gauge);

        var needleEnd = PointOnCircle(center, new Size(width * 0.26, width * 0.26), -58);
        context.DrawLine(pen, center, needleEnd);
        context.DrawEllipse(brush, null, center, Math.Max(1, width * 0.075), Math.Max(1, width * 0.075));
    }

    private static void DrawLoop(DrawingContext context, Pen pen, double width, double height) {
        var center = new Point(width * 0.5, height * 0.5);
        var radius = new Size(width * 0.36, width * 0.36);
        const double startAngle = 24;
        const double endAngle = 338;
        var arc = new StreamGeometry();
        using (var builder = arc.Open()) {
            builder.BeginFigure(PointOnCircle(center, radius, startAngle), false);
            builder.ArcTo(PointOnCircle(center, radius, endAngle), radius, 0, true, SweepDirection.Clockwise);
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);

        // 箭头放在顺时针扫过来的末端，看起来才像“循环”。
        var tip = PointOnCircle(center, radius, endAngle);
        var radians = endAngle * Math.PI / 180;
        var tangent = new Vector(-Math.Sin(radians), Math.Cos(radians));
        var normal = new Vector(-tangent.Y, tangent.X);
        var head = Math.Max(2.4, width * 0.24);
        var arrow = new StreamGeometry();
        using (var builder = arrow.Open()) {
            builder.BeginFigure(tip + tangent * head, true);
            builder.LineTo(tip - normal * (head * 0.62));
            builder.LineTo(tip + normal * (head * 0.62));
            builder.EndFigure(true);
        }
        context.DrawGeometry(pen.Brush, null, arrow);
    }

    private static void DrawClock(DrawingContext context, IBrush brush, Pen pen, double width, double height) {
        var center = new Point(width * 0.5, height * 0.5);
        var radius = new Size(width * 0.4, width * 0.4);
        context.DrawEllipse(null, pen, center, radius.Width, radius.Height);
        context.DrawLine(pen, center, new Point(center.X, center.Y - radius.Height * 0.52));
        context.DrawLine(pen, center, new Point(center.X + radius.Width * 0.4, center.Y + radius.Height * 0.22));
        context.DrawEllipse(brush, null, center, Math.Max(0.9, width * 0.06), Math.Max(0.9, width * 0.06));
    }

    private static Point PointOnCircle(Point center, Size radius, double degrees) {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius.Width,
            center.Y + Math.Sin(radians) * radius.Height);
    }
}
