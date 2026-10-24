using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Avance_Player.Controls;

public enum MediaGlyphKind {
    Previous,
    Next,
    Play,
    Shuffle,
    Repeat,
    RepeatOne,
    Queue,
    More,
    Lyrics,
    Plus,
    Minus,
    Close,
    Fullscreen
}

/// <summary>
/// 单色播放控制/行内操作图标（上一首、播放、随机、循环、队列、更多、歌词、加减、关闭、全屏），
/// 全用几何图形画。以前沉浸歌词栏和悬浮控制台用的是 "|◀" 这种字符，字体度量偏得厉害；
/// 几何图形能保证每个图标都在按钮的视觉中心。
/// </summary>
public sealed class MediaGlyph : TemplatedControl {
    public static readonly StyledProperty<MediaGlyphKind> KindProperty =
        AvaloniaProperty.Register<MediaGlyph, MediaGlyphKind>(nameof(Kind));

    /// <summary><see cref="MediaGlyphKind.Shuffle"/> 用：true 画交叉箭头，表示随机开着。</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<MediaGlyph, bool>(nameof(IsActive));

    static MediaGlyph() {
        AffectsRender<MediaGlyph>(KindProperty, IsActiveProperty, ForegroundProperty);
    }

    public MediaGlyphKind Kind {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsActive {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        // 随机和循环会换形状，顺手给个小弹跳，让图标和点击对得上。
        if (change.Property == IsActiveProperty) Motion.Pop(this, 0.86, 180);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var brush = Foreground ?? Brushes.Gray;
        var size = Math.Min(width, height);
        var center = new Point(width / 2, height / 2);
        var pen = new Pen(brush, Math.Max(1.2, size * 0.1)) {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        switch (Kind) {
            case MediaGlyphKind.Previous:
                DrawSkip(context, brush, center, size, false);
                break;
            case MediaGlyphKind.Next:
                DrawSkip(context, brush, center, size, true);
                break;
            case MediaGlyphKind.Play:
                DrawPlay(context, brush, center, size);
                break;
            case MediaGlyphKind.Shuffle:
                DrawShuffle(context, brush, pen, center, size, IsActive);
                break;
            case MediaGlyphKind.Repeat:
                DrawRepeat(context, brush, pen, center, size, false);
                break;
            case MediaGlyphKind.RepeatOne:
                DrawRepeat(context, brush, pen, center, size, true);
                break;
            case MediaGlyphKind.Queue:
                DrawQueue(context, pen, center, size);
                break;
            case MediaGlyphKind.More:
                DrawMore(context, brush, center, size);
                break;
            case MediaGlyphKind.Lyrics:
                DrawLyrics(context, brush, center, size);
                break;
            case MediaGlyphKind.Plus:
                DrawPlus(context, pen, center, size, true);
                break;
            case MediaGlyphKind.Minus:
                DrawPlus(context, pen, center, size, false);
                break;
            case MediaGlyphKind.Close:
                DrawClose(context, pen, center, size);
                break;
            case MediaGlyphKind.Fullscreen:
                DrawFullscreen(context, pen, center, size);
                break;
        }
    }

    private static Point At(Point center, double size, double x, double y) =>
        new(center.X + x * size, center.Y + y * size);

    private static void DrawSkip(DrawingContext context, IBrush brush, Point center, double size, bool forward) {
        var direction = forward ? 1 : -1;
        var tip = At(center, size, 0.28 * direction, 0);
        var baseX = -0.28 * direction;
        var triangle = new StreamGeometry();
        using (var builder = triangle.Open()) {
            builder.BeginFigure(tip, true);
            builder.LineTo(At(center, size, baseX, -0.32));
            builder.LineTo(At(center, size, baseX, 0.32));
            builder.EndFigure(true);
        }
        context.DrawGeometry(brush, null, triangle);

        var barX = 0.36 * direction;
        context.DrawRectangle(brush, null,
            new Rect(center.X + barX * size - (direction < 0 ? 0.08 * size : 0), center.Y - 0.32 * size,
                0.08 * size, 0.64 * size));
    }

    private static void DrawShuffle(DrawingContext context, IBrush brush, Pen pen, Point center, double size, bool crossing) {
        if (!crossing) {
            // 顺序播放：两个反向的直箭头。
            context.DrawLine(pen, At(center, size, -0.34, -0.22), At(center, size, 0.26, -0.22));
            DrawArrowHead(context, brush, At(center, size, 0.38, -0.22), 1, size);
            context.DrawLine(pen, At(center, size, 0.34, 0.22), At(center, size, -0.26, 0.22));
            DrawArrowHead(context, brush, At(center, size, -0.38, 0.22), -1, size);
            return;
        }

        // 随机：两条路径在中间交叉。
        DrawShufflePath(context, pen, center, size, -0.22, 0.22);
        DrawShufflePath(context, pen, center, size, 0.22, -0.22);
        DrawArrowHead(context, brush, At(center, size, 0.38, 0.22), 1, size);
        DrawArrowHead(context, brush, At(center, size, 0.38, -0.22), 1, size);
    }

    private static void DrawShufflePath(DrawingContext context, Pen pen, Point center, double size, double startY, double endY) {
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open()) {
            builder.BeginFigure(At(center, size, -0.38, startY), false);
            builder.LineTo(At(center, size, -0.16, startY));
            builder.CubicBezierTo(At(center, size, -0.02, startY), At(center, size, 0.02, endY), At(center, size, 0.18, endY));
            builder.LineTo(At(center, size, 0.26, endY));
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawArrowHead(DrawingContext context, IBrush brush, Point tip, int direction, double size) {
        var head = Math.Max(3.5, size * 0.18);
        var triangle = new StreamGeometry();
        using (var builder = triangle.Open()) {
            builder.BeginFigure(tip, true);
            builder.LineTo(new Point(tip.X - head * direction, tip.Y - head * 0.55));
            builder.LineTo(new Point(tip.X - head * direction, tip.Y + head * 0.55));
            builder.EndFigure(true);
        }
        context.DrawGeometry(brush, null, triangle);
    }

    private static void DrawRepeat(DrawingContext context, IBrush brush, Pen pen, Point center, double size, bool one) {
        var radius = new Size(0.34 * size, 0.34 * size);
        const double startAngle = 38;
        const double endAngle = 322;
        var arc = new StreamGeometry();
        using (var builder = arc.Open()) {
            builder.BeginFigure(PointOnCircle(center, radius, startAngle), false);
            builder.ArcTo(PointOnCircle(center, radius, endAngle), radius, 0, true, SweepDirection.Clockwise);
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);

        var tip = PointOnCircle(center, radius, endAngle);
        var radians = endAngle * Math.PI / 180;
        var tangent = new Vector(-Math.Sin(radians), Math.Cos(radians));
        var normal = new Vector(-tangent.Y, tangent.X);
        var head = Math.Max(2.6, size * 0.2);
        var arrow = new StreamGeometry();
        using (var builder = arrow.Open()) {
            builder.BeginFigure(tip + tangent * head, true);
            builder.LineTo(tip - normal * (head * 0.6));
            builder.LineTo(tip + normal * (head * 0.6));
            builder.EndFigure(true);
        }
        context.DrawGeometry(brush, null, arrow);

        if (!one) return;

        // 循环单个的时候，圈里加个小 "1"。
        var top = At(center, size, 0.02, -0.17);
        context.DrawLine(pen, top, At(center, size, 0.02, 0.18));
        context.DrawLine(pen, top, At(center, size, -0.07, -0.06));
        context.DrawLine(pen, At(center, size, -0.08, 0.18), At(center, size, 0.12, 0.18));
    }

    private static void DrawQueue(DrawingContext context, Pen pen, Point center, double size) {
        context.DrawLine(pen, At(center, size, -0.34, -0.24), At(center, size, 0.34, -0.24));
        context.DrawLine(pen, At(center, size, -0.34, 0), At(center, size, 0.34, 0));
        context.DrawLine(pen, At(center, size, -0.34, 0.24), At(center, size, 0.34, 0.24));
    }

    private static void DrawMore(DrawingContext context, IBrush brush, Point center, double size) {
        var radius = Math.Max(1.1, size * 0.075);
        context.DrawEllipse(brush, null, At(center, size, -0.22, 0), radius, radius);
        context.DrawEllipse(brush, null, At(center, size, 0, 0), radius, radius);
        context.DrawEllipse(brush, null, At(center, size, 0.22, 0), radius, radius);
    }

    private static void DrawLyrics(DrawingContext context, IBrush brush, Point center, double size) {
        foreach (var dx in new[] { -0.18, 0.18 }) {
            context.DrawEllipse(brush, null, At(center, size, dx, -0.04), size * 0.105, size * 0.105);
            var tail = new StreamGeometry();
            using (var builder = tail.Open()) {
                builder.BeginFigure(At(center, size, dx - 0.10, 0.02), true);
                builder.LineTo(At(center, size, dx + 0.02, 0.02));
                builder.LineTo(At(center, size, dx - 0.12, 0.24));
                builder.EndFigure(true);
            }
            context.DrawGeometry(brush, null, tail);
        }
    }

    private static void DrawPlay(DrawingContext context, IBrush brush, Point center, double size) {
        // 稍微往几何中心右边挪一点，三角形的视觉重量才看着居中。
        var triangle = new StreamGeometry();
        using (var builder = triangle.Open()) {
            builder.BeginFigure(At(center, size, -0.22, -0.36), true);
            builder.LineTo(At(center, size, 0.36, 0));
            builder.LineTo(At(center, size, -0.22, 0.36));
            builder.EndFigure(true);
        }
        context.DrawGeometry(brush, null, triangle);
    }

    private static void DrawPlus(DrawingContext context, Pen pen, Point center, double size, bool vertical) {
        context.DrawLine(pen, At(center, size, -0.32, 0), At(center, size, 0.32, 0));
        if (!vertical) return;
        context.DrawLine(pen, At(center, size, 0, -0.32), At(center, size, 0, 0.32));
    }

    private static void DrawClose(DrawingContext context, Pen pen, Point center, double size) {
        context.DrawLine(pen, At(center, size, -0.3, -0.3), At(center, size, 0.3, 0.3));
        context.DrawLine(pen, At(center, size, -0.3, 0.3), At(center, size, 0.3, -0.3));
    }

    private static void DrawFullscreen(DrawingContext context, Pen pen, Point center, double size) {
        var outer = 0.34;
        var inner = 0.11;
        foreach (var (sx, sy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) }) {
            var corner = new StreamGeometry();
            using (var builder = corner.Open()) {
                builder.BeginFigure(At(center, size, sx * inner, sy * outer), false);
                builder.LineTo(At(center, size, sx * outer, sy * outer));
                builder.LineTo(At(center, size, sx * outer, sy * inner));
                builder.EndFigure(false);
            }
            context.DrawGeometry(null, pen, corner);
        }
    }

    private static Point PointOnCircle(Point center, Size radius, double degrees) {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius.Width,
            center.Y + Math.Sin(radians) * radius.Height);
    }
}
