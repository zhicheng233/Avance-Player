using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Avance_Player.Controls;

public sealed class WaveProgressBar : Control {
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<WaveProgressBar, double>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<WaveProgressBar, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<WaveProgressBar, double>(nameof(Phase));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<WaveProgressBar, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<WaveProgressBar, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<double> WaveHeightProperty =
        AvaloniaProperty.Register<WaveProgressBar, double>(nameof(WaveHeight), 2.5);

    public static readonly StyledProperty<bool> ShowThumbProperty =
        AvaloniaProperty.Register<WaveProgressBar, bool>(nameof(ShowThumb), true);

    public static readonly StyledProperty<bool> IsWavyProperty =
        AvaloniaProperty.Register<WaveProgressBar, bool>(nameof(IsWavy), true);

    public static readonly StyledProperty<double> LoopStartProperty =
        AvaloniaProperty.Register<WaveProgressBar, double>(nameof(LoopStart), -1);

    public static readonly StyledProperty<double> LoopEndProperty =
        AvaloniaProperty.Register<WaveProgressBar, double>(nameof(LoopEnd), -1);

    private readonly DispatcherTimer _motionTimer;
    private bool _isDragging;
    private bool _isPointerOver;
    private bool _isInitialized;
    private DateTime _lastMotionTick;
    private double _waveAmount;
    private double _thumbScale = 1;
    private double _displayFraction;

    static WaveProgressBar() {
        AffectsRender<WaveProgressBar>(ValueProperty, MaximumProperty, PhaseProperty,
            IsPlayingProperty, AccentBrushProperty, WaveHeightProperty, ShowThumbProperty, IsWavyProperty,
            LoopStartProperty, LoopEndProperty);
    }

    public WaveProgressBar() {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        // Drives the eased playback fill, the wave growing in/out of pause, and the hover thumb.
        // It only runs while one of those is actually moving, so an idle bar costs nothing.
        _motionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _motionTimer.Tick += (_, _) => StepMotion();
    }

    public double Value {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Phase {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public bool IsPlaying {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public IBrush? AccentBrush {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public double WaveHeight {
        get => GetValue(WaveHeightProperty);
        set => SetValue(WaveHeightProperty, value);
    }

    public bool ShowThumb {
        get => GetValue(ShowThumbProperty);
        set => SetValue(ShowThumbProperty, value);
    }

    public bool IsWavy {
        get => GetValue(IsWavyProperty);
        set => SetValue(IsWavyProperty, value);
    }

    public double LoopStart {
        get => GetValue(LoopStartProperty);
        set => SetValue(LoopStartProperty, value);
    }

    public double LoopEnd {
        get => GetValue(LoopEndProperty);
        set => SetValue(LoopEndProperty, value);
    }

    // Wave frequency in radians per pixel; ~0.18 gives a ~35px wavelength → a fine, dense ripple.
    private const double WaveFrequency = 0.18;

    // AmbientPhase advances ~0.24 rad/s; scale it so the shorter wave still travels at ~30px/s.
    private const double WavePhaseScale = 22.0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlayingProperty || change.Property == IsWavyProperty) EnsureMotion();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        EnsureMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        _motionTimer.Stop();
    }

    protected override void OnPointerEntered(PointerEventArgs e) {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        EnsureMotion();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        _isPointerOver = false;
        EnsureMotion();
    }

    public override void Render(DrawingContext context) {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var targetFraction = Math.Clamp(Value / Math.Max(1, Maximum), 0, 1);
        if (!_isInitialized) {
            _isInitialized = true;
            _displayFraction = targetFraction;
            _waveAmount = IsWavy && IsPlaying ? 1 : 0;
        }
        else if (!IsPlaying && !_isDragging) {
            // Seek/stop changes should land immediately; only continuous playback is eased.
            _displayFraction = targetFraction;
        }

        if (IsPlaying || _isPointerOver || _isDragging) EnsureMotion();

        var fraction = _displayFraction;
        var centerY = height / 2;
        var accentColor = (AccentBrush as ISolidColorBrush)?.Color ?? Color.Parse("#CDBA98");
        var accent = new SolidColorBrush(accentColor);
        var track = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));

        const double inset = 7;
        var usableWidth = Math.Max(0, width - inset * 2);
        var progressX = inset + usableWidth * fraction;

        if (IsWavy) {
            var amplitude = Math.Min(WaveHeight, height * 0.42) * _waveAmount * (_isDragging ? 1.25 : 1);
            var trackPen = new Pen(track, 3, null, PenLineCap.Round);
            var accentPen = new Pen(accent, 3.5, null, PenLineCap.Round);

            // Unplayed portion: flat line from thumb to right end.
            if (width - inset - progressX > 0.5) {
                context.DrawLine(trackPen, new Point(progressX, centerY), new Point(width - inset, centerY));
            }

            // A–B loop range shading and markers.
            if (LoopStart >= 0 && LoopEnd > LoopStart) {
                var startX = inset + usableWidth * Math.Clamp(LoopStart, 0, 1);
                var endX = inset + usableWidth * Math.Clamp(LoopEnd, 0, 1);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(26, accentColor.R, accentColor.G, accentColor.B)), null,
                    new Rect(startX, 2, Math.Max(1, endX - startX), height - 4));
                context.DrawLine(accentPen, new Point(startX, 2), new Point(startX, height - 2));
                context.DrawLine(accentPen, new Point(endX, 2), new Point(endX, height - 2));
            }

            // Played portion: the wave eases down to a flat line when playback pauses.
            if (progressX - inset > 0.5) {
                if (amplitude > 0.05) {
                    DrawSineWave(context, inset, progressX, centerY, amplitude, accentPen);
                } else {
                    context.DrawLine(accentPen, new Point(inset, centerY), new Point(progressX, centerY));
                }
            }

            // Thumb: rounded vertical pill at current position, with a soft halo on hover/drag.
            if (ShowThumb) {
                DrawThumbHalo(context, accentColor, progressX, centerY, height);
                var thumbHalf = Math.Min(9, height / 2 - 1) * _thumbScale;
                context.DrawLine(accentPen, new Point(progressX, centerY - thumbHalf), new Point(progressX, centerY + thumbHalf));
            }
        }
        else {
            var trackPen = new Pen(track, 3, null, PenLineCap.Round);
            var accentPen = new Pen(accent, 3, null, PenLineCap.Round);
            context.DrawLine(trackPen, new Point(inset, centerY), new Point(width - inset, centerY));
            if (progressX - inset > 0.5) {
                context.DrawLine(accentPen, new Point(inset, centerY), new Point(progressX, centerY));
            }

            if (ShowThumb) {
                DrawThumbHalo(context, accentColor, progressX, centerY, height);
                var radius = 4.5 * _thumbScale;
                context.DrawEllipse(accent, null, new Rect(progressX - radius, centerY - radius, radius * 2, radius * 2));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _isDragging = true;
        e.Pointer.Capture(this);
        SetValueFromPointer(e.GetPosition(this).X);
        EnsureMotion();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        if (!_isDragging) return;
        SetValueFromPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        SetValueFromPointer(e.GetPosition(this).X);
        EnsureMotion();
        InvalidateVisual();
        e.Handled = true;
    }

    private void SetValueFromPointer(double x) {
        if (Bounds.Width <= 0) return;
        var fraction = Math.Clamp(x / Bounds.Width, 0, 1);
        Value = fraction * Math.Max(1, Maximum);
        if (_isDragging) _displayFraction = fraction;
    }

    private void EnsureMotion() {
        if (!this.IsAttachedToVisualTree()) return;
        if (_motionTimer.IsEnabled) return;
        _lastMotionTick = DateTime.UtcNow;
        _motionTimer.Start();
    }

    private void StepMotion() {
        if (!this.IsEffectivelyVisible) {
            _motionTimer.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        var deltaSeconds = Math.Clamp((now - _lastMotionTick).TotalSeconds, 0.001, 0.08);
        _lastMotionTick = now;

        var waveTarget = IsWavy && IsPlaying ? 1.0 : 0.0;
        var thumbTarget = _isDragging ? 1.5 : _isPointerOver ? 1.22 : 1.0;
        var fractionTarget = Math.Clamp(Value / Math.Max(1, Maximum), 0, 1);

        _waveAmount = Approach(_waveAmount, waveTarget, deltaSeconds, 11);
        _thumbScale = Approach(_thumbScale, thumbTarget, deltaSeconds, 16);
        if (IsPlaying && !_isDragging) {
            var delta = fractionTarget - _displayFraction;
            // Continuous playback only creeps forward. A backward or large forward jump is a
            // seek, an A–B wrap, or a new track, and should land immediately instead of
            // sweeping the fill across the bar.
            if (delta < -0.0005 || delta > 0.08) {
                _displayFraction = fractionTarget;
            }
            else {
                _displayFraction = Approach(_displayFraction, fractionTarget, deltaSeconds, 16);
            }
        } else {
            _displayFraction = fractionTarget;
        }

        var settled = Math.Abs(_waveAmount - waveTarget) < 0.002
                      && Math.Abs(_thumbScale - thumbTarget) < 0.002
                      && (!IsPlaying || _isDragging || Math.Abs(_displayFraction - fractionTarget) < 0.0005);
        if (settled) {
            _waveAmount = waveTarget;
            _thumbScale = thumbTarget;
            _displayFraction = fractionTarget;
            _motionTimer.Stop();
        }

        InvalidateVisual();
    }

    private static double Approach(double current, double target, double deltaSeconds, double speed) =>
        current + (target - current) * (1 - Math.Exp(-speed * deltaSeconds));

    private void DrawThumbHalo(DrawingContext context, Color accentColor, double x, double centerY, double height) {
        if (_thumbScale <= 1.02) return;
        var radius = Math.Min(height / 2, 11) * _thumbScale;
        var alpha = (byte)Math.Clamp((_thumbScale - 1) * 170, 0, 72);
        var halo = new SolidColorBrush(Color.FromArgb(alpha, accentColor.R, accentColor.G, accentColor.B));
        context.DrawEllipse(halo, null, new Rect(x - radius, centerY - radius, radius * 2, radius * 2));
    }

    private void DrawSineWave(DrawingContext context, double startX, double endX, double centerY,
        double amplitude, Pen pen) {
        var geometry = new StreamGeometry();
        var phase = Phase * WavePhaseScale;
        using (var builder = geometry.Open()) {
            builder.BeginFigure(new Point(startX, centerY + Math.Sin(startX * WaveFrequency + phase) * amplitude), false);
            for (var x = startX + 1.5; x < endX; x += 1.5) {
                builder.LineTo(new Point(x, centerY + Math.Sin(x * WaveFrequency + phase) * amplitude));
            }

            builder.LineTo(new Point(endX, centerY + Math.Sin(endX * WaveFrequency + phase) * amplitude));
            builder.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
