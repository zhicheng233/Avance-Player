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

    /// <summary>
    /// true 时按进度条用：指针移动只预览位置（写到 <see cref="Value"/>），松手才抛
    /// <see cref="SeekCompleted"/>。seek 由 view model 在那里提交，播放时钟就不会跟拖动打架。
    /// </summary>
    public static readonly StyledProperty<bool> IsSeekBarProperty =
        AvaloniaProperty.Register<WaveProgressBar, bool>(nameof(IsSeekBar));

    private const double Inset = 7;

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

        // 管播放进度的缓动、暂停时波浪收放、悬停滑块。
        // 只有这些真的在动时才跑，静止的进度条不耗资源。
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

    public bool IsSeekBar {
        get => GetValue(IsSeekBarProperty);
        set => SetValue(IsSeekBarProperty, value);
    }

    /// <summary>seek 拖动开始时抛一次，在预览值之前。</summary>
    public event EventHandler? SeekStarted;

    /// <summary>松手时抛，值是提交后的位置，单位跟控件一致。</summary>
    public event EventHandler<double>? SeekCompleted;

    // 波浪频率，弧度/像素。0.18 大概 35px 一个波长，波纹细一点密一点。
    private const double WaveFrequency = 0.18;

    // AmbientPhase 大约 0.24 rad/s；按这个比例缩放，短一点的波也能保持 ~30px/s 的移动速度。
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
            // seek/停止这种变化要立刻落位，只有连续播放才做缓动。
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

            // 未播放部分：从滑块到右端的一条平线。
            if (width - inset - progressX > 0.5) {
                context.DrawLine(trackPen, new Point(progressX, centerY), new Point(width - inset, centerY));
            }

            // A–B 循环的范围底色和标记。
            if (LoopStart >= 0 && LoopEnd > LoopStart) {
                var startX = inset + usableWidth * Math.Clamp(LoopStart, 0, 1);
                var endX = inset + usableWidth * Math.Clamp(LoopEnd, 0, 1);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(26, accentColor.R, accentColor.G, accentColor.B)), null,
                    new Rect(startX, 2, Math.Max(1, endX - startX), height - 4));
                context.DrawLine(accentPen, new Point(startX, 2), new Point(startX, height - 2));
                context.DrawLine(accentPen, new Point(endX, 2), new Point(endX, height - 2));
            }

            // 已播放部分：暂停时波浪会慢慢收成平线。
            if (progressX - inset > 0.5) {
                if (amplitude > 0.05) {
                    DrawSineWave(context, inset, progressX, centerY, amplitude, accentPen);
                } else {
                    context.DrawLine(accentPen, new Point(inset, centerY), new Point(progressX, centerY));
                }
            }

            // 滑块：当前位置的圆角竖条，悬停/拖动时带一圈柔光。
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
        if (!IsEnabled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _isDragging = true;
        if (IsSeekBar) SeekStarted?.Invoke(this, EventArgs.Empty);
        e.Pointer.Capture(this);
        SetValueFromPointer(e.GetPosition(this).X);
        EnsureMotion();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        if (!IsEnabled || !_isDragging) return;
        SetValueFromPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        if (!IsEnabled || !_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        SetValueFromPointer(e.GetPosition(this).X);
        if (IsSeekBar) SeekCompleted?.Invoke(this, Value);
        EnsureMotion();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        if (!_isDragging) return;
        // 窗口移动或者别的控件抢走指针时，捕获会丢。
        // 把当前显示的位置提交掉，别让拖动标志卡住。
        _isDragging = false;
        if (IsSeekBar) SeekCompleted?.Invoke(this, Value);
        EnsureMotion();
        InvalidateVisual();
    }

    private void SetValueFromPointer(double x) {
        var width = Bounds.Width;
        if (width <= 0) return;
        var usableWidth = Math.Max(1, width - Inset * 2);
        var fraction = Math.Clamp((x - Inset) / usableWidth, 0, 1);
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
            // 连续播放只会慢慢往前爬。往回跳或者大幅往前跳，基本就是 seek、A–B 回绕、换歌，
            // 应该直接落位，别让填充横扫过去。
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
