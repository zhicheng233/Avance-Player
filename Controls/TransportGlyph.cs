using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Avance_Player.Controls;

/// <summary>
/// 播放/暂停图标，几何绘制，不靠字体符号。
/// </summary>
public sealed class TransportGlyph : Control {
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<TransportGlyph, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TransportGlyph, IBrush?>(nameof(Fill), Brushes.Black);

    static TransportGlyph() {
        AffectsRender<TransportGlyph>(IsPlayingProperty, FillProperty);
    }

    public bool IsPlaying {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public IBrush? Fill {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        // 按下时轻轻弹一下，手感像真的开关，而不是换了个图标。
        if (change.Property == IsPlayingProperty) Motion.Pop(this, 0.82, 200);
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var brush = Fill ?? Brushes.Black;
        if (IsPlaying) {
            var barWidth = width * 0.26;
            var gap = width * 0.2;
            var left = (width - barWidth * 2 - gap) / 2;
            var top = height * 0.12;
            var barHeight = height * 0.76;
            context.DrawRectangle(brush, null, new Rect(left, top, barWidth, barHeight));
            context.DrawRectangle(brush, null, new Rect(left + barWidth + gap, top, barWidth, barHeight));
            return;
        }

        var geometry = new StreamGeometry();
        using (var builder = geometry.Open()) {
            builder.BeginFigure(new Point(width * 0.2, height * 0.08), true);
            builder.LineTo(new Point(width * 0.86, height * 0.5));
            builder.LineTo(new Point(width * 0.2, height * 0.92));
            builder.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }
}
