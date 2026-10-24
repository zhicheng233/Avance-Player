using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Transformation;

namespace Avance_Player.Controls;

/// <summary>
/// 页面切换比单纯淡入淡出多一点层次：新页面从接近原尺寸的位置往上浮几个像素，
/// 旧页面往上抬走。位移故意做得很小，不然导航会显得拖沓。
/// </summary>
public sealed class FadeSlidePageTransition : IPageTransition {
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>新页面从下面上来多少像素。</summary>
    public double Offset { get; set; } = 14;

    /// <summary>新页面起始缩放。</summary>
    public double ScaleFrom { get; set; } = 0.994;

    /// <summary>旧页面退场时缩到多少。</summary>
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

        // 先把第一帧写上，别等动画时钟开始跑。不然新创建的 presenter 会有一帧全亮，闪一下。
        visual.Opacity = 0;
        visual.RenderTransform = TransformOperations.Parse(start);

        try {
            await Task.WhenAll(
                Motion.RunAsync(visual, Visual.OpacityProperty, 0, 1, Duration, TimeSpan.Zero, Easing, cancellationToken),
                Motion.RunTransformAsync(visual, start, identity, Duration, TimeSpan.Zero, Easing, cancellationToken));
        }
        finally {
            // 动画结束把已经归位的 transform 层去掉。presenter 会留到下次导航，
            // 一直挂着 transform 会让文字走变换后的栅格路径，糊。
            if (!cancellationToken.IsCancellationRequested) visual.RenderTransform = null;
        }
    }

    private async Task AnimateOutAsync(Visual visual, CancellationToken cancellationToken) {
        visual.RenderTransformOrigin = RelativePoint.Center;
        var identity = Motion.TranslateScale(0, 0, 1);
        var end = Motion.TranslateScale(0, -Offset * 0.5, ScaleTo);

        // 旧页面走得比新页面快一点，两个就不会抢焦点。
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
