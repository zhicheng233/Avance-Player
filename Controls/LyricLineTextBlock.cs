using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avance_Player.Models;

namespace Avance_Player.Controls;

/// <summary>
/// 把 <see cref="LyricLine"/> 拆成一个个词来画的 TextBlock。播放走到哪，前面的词就填到哪。
/// 当前词是一条会动的渐变，不是整词突然变色；整行歌词也用同一套扫过去的逻辑。
/// </summary>
public sealed class LyricLineTextBlock : TextBlock {
    public static readonly StyledProperty<LyricLine?> LineProperty =
        AvaloniaProperty.Register<LyricLineTextBlock, LyricLine?>(nameof(Line));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<LyricLineTextBlock, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> FutureBrushProperty =
        AvaloniaProperty.Register<LyricLineTextBlock, IBrush?>(nameof(FutureBrush));

    public static readonly StyledProperty<IBrush?> SungBrushProperty =
        AvaloniaProperty.Register<LyricLineTextBlock, IBrush?>(nameof(SungBrush));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<LyricLineTextBlock, double>(nameof(Position));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<LyricLineTextBlock, bool>(nameof(IsPlaying));

    private readonly DispatcherTimer _motionTimer;
    private LyricLine? _subscribedLine;
    private bool _attached;
    private bool _hasAnchor;
    private double _anchorPosition;
    private long _anchorTimestamp;
    private double _velocity = 1.0;

    public LyricLineTextBlock() {
        _motionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _motionTimer.Tick += (_, _) => {
            if (!IsEffectivelyVisible) {
                _motionTimer.Stop();
                return;
            }
            UpdateBrushes();
        };
    }

    public LyricLine? Line {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public IBrush? AccentBrush {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? FutureBrush {
        get => GetValue(FutureBrushProperty);
        set => SetValue(FutureBrushProperty, value);
    }

    public IBrush? SungBrush {
        get => GetValue(SungBrushProperty);
        set => SetValue(SungBrushProperty, value);
    }

    /// <summary>播放位置，单位秒。两次更新之间由控件自己外推。</summary>
    public double Position {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public bool IsPlaying {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == LineProperty) {
            // 列表复用行的时候，计时器可能还在跑，行却已经换了。
            // 这里要按当前时钟重新起算，不能沿用上一行的锚点。
            _anchorPosition = Position;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _hasAnchor = true;
            _velocity = IsPlaying ? 1.0 : 0;
            SubscribeToLine();
            Rebuild();
            UpdateMotionState();
        }
        else if (change.Property == PositionProperty) {
            OnPositionChanged();
        }
        else if (change.Property == IsPlayingProperty) {
            OnPlayingChanged();
        }
        else if (change.Property == AccentBrushProperty
                 || change.Property == FutureBrushProperty
                 || change.Property == SungBrushProperty
                 || change.Property == ForegroundProperty) {
            UpdateBrushes();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        SubscribeToLine();
        Rebuild();
        UpdateMotionState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _attached = false;
        _motionTimer.Stop();
        UnsubscribeFromLine();
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeToLine() {
        if (!_attached) return;
        var line = Line;
        if (ReferenceEquals(_subscribedLine, line)) return;

        UnsubscribeFromLine();
        _subscribedLine = line;
        if (_subscribedLine is not null) _subscribedLine.PropertyChanged += Line_PropertyChanged;
    }

    private void UnsubscribeFromLine() {
        if (_subscribedLine is null) return;
        _subscribedLine.PropertyChanged -= Line_PropertyChanged;
        _subscribedLine = null;
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(LyricLine.IsCurrent)) {
            UpdateMotionState();
            UpdateBrushes();
        }
        else if (e.PropertyName == nameof(LyricLine.CurrentWordIndex)) {
            UpdateBrushes();
        }
    }

    private void Rebuild() {
        var inlines = Inlines;
        if (inlines is null) return;
        inlines.Clear();

        var line = Line;
        if (line is null) return;

        if (line.Words.Count == 0) {
            inlines.Add(new Run(line.Text));
            UpdateBrushes();
            return;
        }

        foreach (var word in line.Words)
            inlines.Add(new Run(word.Text));

        UpdateBrushes();
    }

    private void OnPositionChanged() {
        var now = Stopwatch.GetTimestamp();
        var newPosition = Position;
        if (_hasAnchor && IsPlaying) {
            var elapsed = (now - _anchorTimestamp) / (double)Stopwatch.Frequency;
            var delta = newPosition - _anchorPosition;
            // 正常更新就是按播放倍率往前走（这里最多 2×），再加一点计时器抖动。
            // 跳得比这快基本就是用户在拖进度，别当成倍速变化。
            var expectedMax = elapsed * 2.5 + 0.15;
            if (elapsed > 0.001 && delta >= 0 && delta <= expectedMax) {
                _velocity = delta / elapsed;
            }
            else {
                _velocity = 1.0;
            }
        }

        _anchorPosition = newPosition;
        _anchorTimestamp = now;
        _hasAnchor = true;
        if (!IsPlaying) _velocity = 0;

        UpdateBrushes();
        UpdateMotionState();
    }

    private void OnPlayingChanged() {
        _anchorPosition = Position;
        _anchorTimestamp = Stopwatch.GetTimestamp();
        _hasAnchor = true;
        _velocity = IsPlaying ? 1.0 : 0;
        UpdateMotionState();
        UpdateBrushes();
    }

    private void UpdateMotionState() {
        var shouldRun = _attached && IsEffectivelyVisible && Line?.IsCurrent == true && IsPlaying;
        if (shouldRun) {
            if (_motionTimer.IsEnabled) return;

            _anchorPosition = Position;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _hasAnchor = true;
            _velocity = 1.0;
            _motionTimer.Start();
        }
        else if (_motionTimer.IsEnabled) {
            _motionTimer.Stop();
            _anchorPosition = Position;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _hasAnchor = true;
            _velocity = 0;
        }
    }

    private double GetDisplayPosition() {
        if (!IsPlaying || !_hasAnchor) return Position;

        var elapsed = (Stopwatch.GetTimestamp() - _anchorTimestamp) / (double)Stopwatch.Frequency;
        var position = _anchorPosition + _velocity * elapsed;
        var line = Line;
        if (line is not null) {
            var start = line.Timestamp.TotalSeconds;
            var end = line.End.TotalSeconds;
            if (end > start + 0.001) position = Math.Min(position, end);
            if (position < start) position = start;
        }

        return position;
    }

    private void UpdateBrushes() {
        var line = Line;
        var inlines = Inlines;
        if (line is null || inlines is null || inlines.Count == 0) return;

        var defaultForeground = Foreground ?? Brushes.White;
        if (!line.IsCurrent) {
            for (var index = 0; index < inlines.Count; index++)
                SetBrush(inlines[index], defaultForeground);
            return;
        }

        var sung = SungBrush ?? defaultForeground;
        var future = FutureBrush ?? defaultForeground;
        var accent = AccentBrush ?? sung;
        var position = GetDisplayPosition();

        if (line.Words.Count == 0) {
            var progress = Progress(line.Timestamp, line.End, position);
            SetBrush(inlines[0], progress <= 0
                ? future
                : progress >= 1
                    ? sung
                    : CreateGradient(sung, accent, future, progress));
            return;
        }

        var useProgress = _hasAnchor || IsPlaying;
        var current = useProgress
            ? FindWordIndex(line, position)
            : Math.Clamp(line.CurrentWordIndex, -1, line.Words.Count - 1);

        for (var index = 0; index < inlines.Count && index < line.Words.Count; index++) {
            if (index < current) {
                SetBrush(inlines[index], sung);
            }
            else if (index > current) {
                SetBrush(inlines[index], future);
            }
            else if (!useProgress) {
                SetBrush(inlines[index], accent);
            }
            else {
                var word = line.Words[index];
                var progress = Progress(word.Start, word.End, position);
                SetBrush(inlines[index], progress <= 0
                    ? future
                    : progress >= 1
                        ? sung
                        : CreateGradient(sung, accent, future, progress));
            }
        }
    }

    private static void SetBrush(Inline inline, IBrush brush) {
        if (!ReferenceEquals(inline.Foreground, brush)) inline.Foreground = brush;
    }

    private static int FindWordIndex(LyricLine line, double positionSeconds) {
        var index = -1;
        for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++) {
            if (line.Words[wordIndex].Start.TotalSeconds <= positionSeconds + 0.001) index = wordIndex;
            else break;
        }
        return index;
    }

    private static double Progress(TimeSpan start, TimeSpan end, double positionSeconds) {
        var startSeconds = start.TotalSeconds;
        var endSeconds = end.TotalSeconds;
        if (endSeconds <= startSeconds + 0.001) return positionSeconds >= startSeconds ? 1 : 0;
        return Math.Clamp((positionSeconds - startSeconds) / (endSeconds - startSeconds), 0, 1);
    }

    private static IBrush CreateGradient(IBrush sung, IBrush accent, IBrush future, double progress) {
        var edge = Math.Clamp(progress, 0.001, 0.999);
        const double feather = 0.08;
        var fadeStart = Math.Max(0, edge - feather);
        var fadeEnd = Math.Min(1, edge + feather);
        var sungColor = ColorOf(sung);
        var accentColor = ColorOf(accent);
        var futureColor = ColorOf(future);

        return new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = {
                new GradientStop(sungColor, 0),
                new GradientStop(sungColor, fadeStart),
                new GradientStop(accentColor, edge),
                new GradientStop(futureColor, fadeEnd),
                new GradientStop(futureColor, 1)
            }
        };
    }

    private static Color ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color : Colors.White;
}
