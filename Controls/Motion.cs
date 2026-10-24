using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Avance_Player.Controls;

/// <summary>
/// 这个原型里手写动画的公共小工具。XAML transition 管悬停、弹层这种连续状态；
/// 这里管需要代码起头的一次性动画（翻页、沉浸歌词入场、图标弹跳）。
/// </summary>
internal static class Motion {
    public static readonly Easing EaseOut = new CubicEaseOut();

    // Avalonia 自带的 TransformAnimator 只动 Visual 自己的 RenderTransform，
    // 直接动子级的 TranslateTransform/ScaleTransform 不行。
    // 给 ITransform 注册一个插值器，一次性动画就能安全地打 RenderTransform。
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> PopTokens = new();

    static Motion() {
        Animation.RegisterCustomAnimator<ITransform, TransformOperationsAnimator>();
    }

    /// <summary>生成一个两关键帧的 double 动画，目标属性是 <paramref name="property"/>。</summary>
    public static Animation Tween(AvaloniaProperty property, double from, double to,
        TimeSpan duration, TimeSpan delay, Easing easing) {
        return new Animation {
            Duration = duration,
            Delay = delay,
            FillMode = FillMode.Forward,
            Easing = easing,
            Children = {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } }
            }
        };
    }

    /// <summary>
    /// 用两个类似 CSS 的 transform 字符串做动画。两个关键帧都落在
    /// <see cref="Visual.RenderTransformProperty"/> 上，绕开动画系统对 transform 子对象的处理
    /// —— 它要的是 Visual，不是 TranslateTransform/ScaleTransform。
    /// </summary>
    public static Animation TransformTween(string from, string to,
        TimeSpan duration, TimeSpan delay, Easing easing) {
        return new Animation {
            Duration = duration,
            Delay = delay,
            FillMode = FillMode.Forward,
            Easing = easing,
            Children = {
                new KeyFrame {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.RenderTransformProperty, TransformOperations.Parse(from)) }
                },
                new KeyFrame {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.RenderTransformProperty, TransformOperations.Parse(to)) }
                }
            }
        };
    }

    /// <summary>把 translate/scale 拼成 transform 字符串，小数点用固定格式。</summary>
    public static string TranslateScale(double x, double y, double scale) =>
        string.Create(CultureInfo.InvariantCulture,
            $"translateX({x}px) translateY({y}px) scale({scale})");

    /// <summary>同上，只是纯缩放。</summary>
    public static string Scale(double scale) =>
        string.Create(CultureInfo.InvariantCulture, $"scale({scale})");

    /// <summary>
    /// 跑一个补间，取消异常直接吞掉。新动画顶掉旧动画时本来就会这样。
    /// </summary>
    public static async Task RunAsync(Animatable target, AvaloniaProperty property, double from, double to,
        TimeSpan duration, TimeSpan delay, Easing easing, CancellationToken cancellationToken) {
        try {
            await Tween(property, from, to, duration, delay, easing).RunAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) {
            // 被新动画顶掉了，后面的状态交给调用方。
        }
    }

    /// <summary>
    /// 在 Visual 的 RenderTransform 上跑一个 transform 补间。到最后一帧 identity 后由调用方清掉。
    /// </summary>
    public static async Task RunTransformAsync(Visual visual, string from, string to,
        TimeSpan duration, TimeSpan delay, Easing easing, CancellationToken cancellationToken) {
        try {
            await TransformTween(from, to, duration, delay, easing).RunAsync(visual, cancellationToken);
        }
        catch (OperationCanceledException) {
            // 被新动画顶掉了，后面的状态交给调用方。
        }
    }

    /// <summary>
    /// 图标状态切换时的小弹一下（播放/暂停、随机开/关）。
    /// BackEaseOut 会稍微过冲，按下去的手感比单纯换图标强。
    /// </summary>
    public static void Pop(Control control, double from = 0.84, double durationMilliseconds = 220) {
        if (PopTokens.TryGetValue(control, out var previous)) {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        PopTokens.AddOrUpdate(control, cts);
        control.RenderTransformOrigin = RelativePoint.Center;
        _ = PopAsync(control, from, durationMilliseconds, cts);
    }

    private static async Task PopAsync(Control control, double from, double durationMilliseconds,
        CancellationTokenSource cts) {
        var token = cts.Token;
        try {
            await RunTransformAsync(control, Scale(from), Scale(1),
                TimeSpan.FromMilliseconds(durationMilliseconds), TimeSpan.Zero, new BackEaseOut(), token);
        }
        finally {
            if (!token.IsCancellationRequested) control.RenderTransform = null;
            if (PopTokens.TryGetValue(control, out var current) && ReferenceEquals(current, cts)) {
                PopTokens.Remove(control);
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 给上面这些一次性动画做 <see cref="TransformOperations"/> 插值。
    /// Avalonia 自带的那个是 internal，这里用公开的 <see cref="InterpolatingAnimator{T}"/> 钩子照着实现一份。
    /// </summary>
    private sealed class TransformOperationsAnimator : InterpolatingAnimator<ITransform> {
        public override ITransform Interpolate(double progress, ITransform oldValue, ITransform newValue) {
            var from = oldValue as TransformOperations ?? TransformOperations.Identity;
            var to = newValue as TransformOperations ?? TransformOperations.Identity;
            return TransformOperations.Interpolate(from, to, progress);
        }
    }
}
