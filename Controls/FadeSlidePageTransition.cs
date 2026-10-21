using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Transformation;

namespace Avance_Player.Controls;

/// <summary>
/// Page-change motion with a little more depth than a plain cross-fade: the incoming page rises
/// a few pixels while settling from a near-identity scale, and the outgoing page lifts away.
/// The offsets are deliberately small so navigation still reads as immediate.
/// </summary>
public sealed class FadeSlidePageTransition : IPageTransition {
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Vertical travel of the incoming page, in pixels.</summary>
    public double Offset { get; set; } = 14;

    /// <summary>Starting scale of the incoming page.</summary>
    public double ScaleFrom { get; set; } = 0.994;

    /// <summary>Scale the outgoing page settles to.</summary>
    public double ScaleTo { get; set; } = 0.998;

    public Easing Easing { get; set; } = new CubicEaseOut();

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken) {
        var fromTask = from is null ? Task.CompletedTask : AnimateOutAsync(from, cancellationToken);
        var toTask = to is null ? Task.CompletedTask : AnimateInAsync(to, cancellationToken);
        return Task.WhenAll(fromTask, toTask);
    }

    private async Task AnimateInAsync(Visual visual, CancellationToken cancellationToken) {
        visual.RenderTransformOrigin = RelativePoint.Center;
        var identity = Motion.TranslateScale(0, 0, 1);
        var start = Motion.TranslateScale(0, Offset, ScaleFrom);

        // Apply the first frame before the animation clock ticks so a newly created presenter
        // never flashes at full opacity for one compositor frame.
        visual.Opacity = 0;
        visual.RenderTransform = TransformOperations.Parse(start);

        try {
            await Task.WhenAll(
                Motion.RunAsync(visual, Visual.OpacityProperty, 0, 1, Duration, TimeSpan.Zero, Easing, cancellationToken),
                Motion.RunTransformAsync(visual, start, identity, Duration, TimeSpan.Zero, Easing, cancellationToken));
        }
        finally {
            // Drop the now-identity transform layer; the presenter stays in the tree until the
            // next navigation, and a permanent transform would keep text on a transformed raster path.
            if (!cancellationToken.IsCancellationRequested) visual.RenderTransform = null;
        }
    }

    private async Task AnimateOutAsync(Visual visual, CancellationToken cancellationToken) {
        visual.RenderTransformOrigin = RelativePoint.Center;
        var identity = Motion.TranslateScale(0, 0, 1);
        var end = Motion.TranslateScale(0, -Offset * 0.5, ScaleTo);

        // The old page leaves faster than the new one arrives, so the two never fight for focus.
        var duration = TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * 0.6);
        try {
            await Task.WhenAll(
                Motion.RunAsync(visual, Visual.OpacityProperty, 1, 0, duration, TimeSpan.Zero, Easing, cancellationToken),
                Motion.RunTransformAsync(visual, identity, end, duration, TimeSpan.Zero, Easing, cancellationToken));
        }
        finally {
            if (!cancellationToken.IsCancellationRequested) visual.RenderTransform = null;
        }
    }
}
