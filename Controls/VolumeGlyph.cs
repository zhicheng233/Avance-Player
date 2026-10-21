using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Avance_Player.Controls;

/// <summary>
/// Monochrome speaker glyph. Drawn rather than composed from emoji so the icon keeps the
/// interface's foreground colour on every platform.
/// </summary>
public sealed class VolumeGlyph : TemplatedControl {
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<VolumeGlyph, double>(nameof(Level), 1);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<VolumeGlyph, bool>(nameof(IsMuted));

    static VolumeGlyph() {
        AffectsRender<VolumeGlyph>(LevelProperty, IsMutedProperty, ForegroundProperty);
    }

    public double Level {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsMuted {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var brush = Foreground ?? Brushes.Gray;
        var body = new StreamGeometry();
        using (var builder = body.Open()) {
            builder.BeginFigure(new Point(width * 0.10, height * 0.38), true);
            builder.LineTo(new Point(width * 0.30, height * 0.38));
            builder.LineTo(new Point(width * 0.50, height * 0.18));
            builder.LineTo(new Point(width * 0.50, height * 0.82));
            builder.LineTo(new Point(width * 0.30, height * 0.62));
            builder.LineTo(new Point(width * 0.10, height * 0.62));
            builder.EndFigure(true);
        }
        context.DrawGeometry(brush, null, body);

        if (IsMuted) {
            context.DrawLine(new Pen(brush, Math.Max(1.4, width * 0.07)), 
                new Point(width * 0.62, height * 0.34), new Point(width * 0.88, height * 0.66));
            context.DrawLine(new Pen(brush, Math.Max(1.4, width * 0.07)),
                new Point(width * 0.88, height * 0.34), new Point(width * 0.62, height * 0.66));
            return;
        }

        var level = Math.Clamp(Level, 0, 1);
        var pen = new Pen(brush, Math.Max(1.3, width * 0.055));
        if (level > 0.02) {
            DrawArc(context, pen, new Point(width * 0.55, height * 0.5),
                new Size(width * 0.15, height * 0.19));
        }
        if (level > 0.35) {
            DrawArc(context, pen, new Point(width * 0.55, height * 0.5),
                new Size(width * 0.29, height * 0.34));
        }
    }

    private static void DrawArc(DrawingContext context, Pen pen, Point center, Size radius) {
        var start = PointOnArc(center, radius, -52);
        var end = PointOnArc(center, radius, 52);
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open()) {
            builder.BeginFigure(start, false);
            builder.ArcTo(end, radius, 0, false, SweepDirection.Clockwise);
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnArc(Point center, Size radius, double degrees) {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius.Width,
            center.Y + Math.Sin(radians) * radius.Height);
    }
}
