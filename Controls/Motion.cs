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
/// Shared helpers for the hand-authored motion in this prototype. XAML transitions cover the
/// continuous hover/popover states; these helpers cover one-shot animations that need a
/// code-driven start (page changes, the immersive lyrics entrance, glyph pops).
/// </summary>
internal static class Motion {
    public static readonly Easing EaseOut = new CubicEaseOut();

    // Avalonia's built-in TransformAnimator only animates a Visual's own RenderTransform, not the
    // child TranslateTransform/ScaleTransform objects directly. Registering an interpolator for
    // ITransform lets the one-shot animations target RenderTransform safely.
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> PopTokens = new();

    static Motion() {
        Animation.RegisterCustomAnimator<ITransform, TransformOperationsAnimator>();
    }

    /// <summary>Builds a two-keyframe double animation for <paramref name="property"/>.</summary>
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
    /// Builds a transform animation from two CSS-like transform strings. Keeping both keyframes
    /// on <see cref="Visual.RenderTransformProperty"/> avoids the animation system's transform
    /// child handling, which expects a Visual rather than a TranslateTransform/ScaleTransform.
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

    /// <summary>Formats a translate/scale transform with invariant decimal separators.</summary>
    public static string TranslateScale(double x, double y, double scale) =>
        string.Create(CultureInfo.InvariantCulture,
            $"translateX({x}px) translateY({y}px) scale({scale})");

    /// <summary>Formats a uniform scale transform with invariant decimal separators.</summary>
    public static string Scale(double scale) =>
        string.Create(CultureInfo.InvariantCulture, $"scale({scale})");

    /// <summary>
    /// Runs one tween and swallows cancellation, which is the normal outcome when a newer
    /// animation supersedes an in-flight one.
    /// </summary>
    public static async Task RunAsync(Animatable target, AvaloniaProperty property, double from, double to,
        TimeSpan duration, TimeSpan delay, Easing easing, CancellationToken cancellationToken) {
        try {
            await Tween(property, from, to, duration, delay, easing).RunAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) {
            // Superseded by a newer animation; the caller owns whatever state comes next.
        }
    }

    /// <summary>
    /// Runs one transform tween on a Visual's RenderTransform. The caller clears the transform
    /// once the final identity frame has been reached.
    /// </summary>
    public static async Task RunTransformAsync(Visual visual, string from, string to,
        TimeSpan duration, TimeSpan delay, Easing easing, CancellationToken cancellationToken) {
        try {
            await TransformTween(from, to, duration, delay, easing).RunAsync(visual, cancellationToken);
        }
        catch (OperationCanceledException) {
            // Superseded by a newer animation; the caller owns whatever state comes next.
        }
    }

    /// <summary>
    /// Small tactile pop used by glyphs when a state flips (play/pause, shuffle on/off).
    /// The overshooting BackEaseOut makes the icon feel pressed rather than merely swapped.
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
    /// Interpolates <see cref="TransformOperations"/> for the one-shot animations above. The
    /// built-in animator is internal, so this mirrors its behaviour through the public
    /// <see cref="InterpolatingAnimator{T}"/> hook.
    /// </summary>
    private sealed class TransformOperationsAnimator : InterpolatingAnimator<ITransform> {
        public override ITransform Interpolate(double progress, ITransform oldValue, ITransform newValue) {
            var from = oldValue as TransformOperations ?? TransformOperations.Identity;
            var to = newValue as TransformOperations ?? TransformOperations.Identity;
            return TransformOperations.Interpolate(from, to, progress);
        }
    }
}
