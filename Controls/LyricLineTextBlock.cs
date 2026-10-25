using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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

    // 非当前行刷成普通色之后就不用再刷了。
    private bool _isPlain;

    // 当前正在扫的那条渐变，用来在进度没变时复用。
    private Inline? _gradientInline;
    private IBrush? _gradientBrush;
    private double _gradientProgress = double.NaN;
    private double _gradientStart;
    private double _gradientWidth;
    private bool _gradientRelative;
    private GradientColors _gradientColors;

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
            _isPlain = false;
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
            // 配色换了，"这行已经是普通色"的缓存也跟着失效。
            _isPlain = false;
            _gradientBrush = null;
            UpdateBrushes();
        }
        else if (change.Property == BoundsProperty) {
            // 布局宽变了（典型：当前行字号 24→32 的过渡动画逐帧改排版）。
            // 渐变是按创建那一刻的排版宽度定端点的，宽度变了不重画，
            // 渐变就铺不满新的文字宽度——超出端点的部分会被渲染成第二段亮区。
            // 暂停时运动计时器是停着的，没人每 16ms 重刷，必须在这里兜住。
            if (change.GetOldValue<Rect>().Width != change.GetNewValue<Rect>().Width) {
                _gradientBrush = null;
                UpdateBrushes();
            }
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
            // 刚变成当前行（或刚离开当前行），普通色缓存不能沿用。
            _isPlain = false;
            _gradientBrush = null;
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
        // Inline 全换过了，之前缓存的画刷和"已刷成普通色"的标记都失效了。
        _isPlain = false;
        _gradientInline = null;
        _gradientBrush = null;
        _gradientProgress = double.NaN;

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
            // 非当前行只是普通前景色。列表里每一行都在收 Position 更新，
            // 已经刷成普通色之后就没必要每 16ms 再遍历一遍 Inline。
            if (_isPlain) return;
            for (var index = 0; index < inlines.Count; index++)
                SetBrush(inlines[index], defaultForeground);
            _isPlain = true;
            _gradientInline = null;
            _gradientProgress = double.NaN;
            return;
        }

        _isPlain = false;
        var sung = SungBrush ?? defaultForeground;
        var future = FutureBrush ?? defaultForeground;
        var accent = AccentBrush ?? sung;
        var position = GetDisplayPosition();

        if (line.Words.Count == 0) {
            // 整行级（普通 LRC/SRT）：文件里只有这一行的起止时间，没有逐词信息。
            // 这种格式拿不到"唱到第几个字"，硬做扫过动画只能靠时间线性拟合，
            // 观感上会和真实演唱错位。所以干脆不做中间态：到点整行直接点亮。
            SetBrush(inlines[0], position >= line.Timestamp.TotalSeconds ? sung : future);
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
                        : PaintGradient(inlines[index], WordCharOffset(line, index), sung, accent, future, progress));
            }
        }
    }

    /// <summary>
    /// 扫过当前词的那条渐变。每 16ms 都会走到这里，进度没变、配色没变时直接复用同一条画刷，
    /// 不再每次新建 <see cref="LinearGradientBrush"/> 和五个 <see cref="GradientStop"/>。
    /// </summary>
    private IBrush PaintGradient(Inline inline, int charOffset, IBrush sung, IBrush accent, IBrush future,
        double progress, bool relativeToRun = false) {
        var colors = GradientColors.Of(sung, accent, future);
        var start = 0.0;
        var width = 0.0;
        if (!relativeToRun) (start, width) = MeasureInlineSpan(inline, charOffset);

        if (ReferenceEquals(inline, _gradientInline)
            && _gradientBrush is not null
            && colors == _gradientColors
            && relativeToRun == _gradientRelative
            && Math.Abs(progress - _gradientProgress) <= 0.001
            && Math.Abs(start - _gradientStart) <= 0.5
            && Math.Abs(width - _gradientWidth) <= 0.5) {
            return _gradientBrush;
        }

        _gradientInline = inline;
        _gradientProgress = progress;
        _gradientColors = colors;
        _gradientRelative = relativeToRun;
        _gradientStart = start;
        _gradientWidth = width;
        _gradientBrush = CreateGradient(sung, accent, future, progress, start, width, relativeToRun);
        return _gradientBrush;
    }

    /// <summary>三种颜色的打包值，用来判断渐变要不要重建。</summary>
    private readonly record struct GradientColors(uint Sung, uint Accent, uint Future) {
        public static GradientColors Of(IBrush sung, IBrush accent, IBrush future) {
            var s = ColorOf(sung);
            // 和 CreateGradient 用同一套取值：强调色可能被提升成已唱色，
            // 这里不跟着算的话，换封面只改了强调色时缓存会误命中，渐变停在旧颜色。
            var a = LiftAccent(s, ColorOf(accent));
            var f = ColorOf(future);
            return new GradientColors(Pack(s), Pack(a), Pack(f));
        }

        private static uint Pack(Color color) =>
            ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
    }

    /// <summary>这个词之前的所有词加起来占多少个字符。用于在整段排版里定位它的起点。</summary>
    private static int WordCharOffset(LyricLine line, int wordIndex) {
        var offset = 0;
        for (var index = 0; index < wordIndex && index < line.Words.Count; index++) {
            offset += line.Words[index].Text.Length;
        }

        return offset;
    }

    /// <summary>
    /// 量一个 <see cref="Inline"/> 实际占的横向范围，给渐变当绝对坐标用过。
    ///
    /// 不能拿渐变画刷自己的相对坐标糊弄：Avalonia 的文字画刷是按"整段文字"的宽度铺开的，
    /// 不是按每个 <see cref="Run"/> 的框。实测把同一个渐变铺在两个字上，两个字都会从头扫一遍，
    /// 一行里就有好几个字同时露出过渡色。也不能用 <see cref="FormattedText"/> 另排版一次来估：
    /// 字符回退和排版细节跟真正渲染出来的对不上，跨度偏一点照样会压到邻居身上。
    /// 这里直接问 <see cref="TextBlock.TextLayout"/>：按字符索引取真实位置。
    /// 但 <c>HitTestTextPosition</c> 在行尾会给出**下一行的行首**，所以词的终点要单独处理：
    /// 用 <see cref="TextLine.FirstTextSourceIndex"/>/<see cref="TextLine.Length"/> 判断这个词属于哪一行，
    /// 词正好收在一行末尾时直接用那一行的宽度当右边界。
    /// </summary>
    private (double Start, double Width) MeasureInlineSpan(Inline inline, int charOffset) {
        var text = inline is Run run ? run.Text : null;
        if (string.IsNullOrEmpty(text)) return (0, 0);

        var layout = TextLayout;
        if (layout is not null) {
            try {
                var startPoint = layout.HitTestTextPosition(charOffset);
                var endPoint = layout.HitTestTextPosition(charOffset + text.Length);
                var start = startPoint.X;
                double end;
                if (Math.Abs(startPoint.Y - endPoint.Y) < 0.5) {
                    // 同一个视觉行：终点就是词的右边界。
                    end = endPoint.X;
                }
                else {
                    // 词跨到了下一行（软换行刚好落在词的边界上）。
                    // 这个词实际续行结束，右边界取它所在那一行的宽度。
                    end = LineWidthAt(layout, charOffset) ?? start;
                }

                var width = Math.Abs(end - start);
                if (width > 0.5) return (Math.Min(start, end), width);
            }
            catch (Exception) {
                // 布局还没建好，走下面的估算。
            }
        }

        // 兜底：按同样的字体单独量一次。只在布局暂时取不到时用。
        try {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
                FontSize,
                Brushes.White);
            return (0, formatted.WidthIncludingTrailingWhitespace);
        }
        catch (Exception) {
            return (0, 0);
        }
    }

    /// <summary>
    /// 找字符 <paramref name="charOffset"/> 所在的视觉行，返回该行的绘制宽度。
    /// 换行布局下行尾那个字的终点只能这样取，否则会拿到下一行的行首 0。
    /// </summary>
    private static double? LineWidthAt(TextLayout layout, int charOffset) {
        foreach (var line in layout.TextLines) {
            var first = line.FirstTextSourceIndex;
            // 行尾常常带一个换行/空格字符，按"含尾随空白"的长度算才包得住最后一个实字。
            var last = first + line.Length;
            if (charOffset >= first && charOffset < last)
                return line.WidthIncludingTrailingWhitespace;
        }

        return null;
    }

    private void SetBrush(Inline inline, IBrush brush) {
        if (ReferenceEquals(inline.Foreground, brush)) return;
        inline.Foreground = brush;
        // 行内文字的画刷是在排版（TextLayout 构形）时被字形运行捕获的。
        // 字号过渡动画期间排版会被重建，过渡结束后残存的排版段可能还握着
        // 过期画刷——表现为整行渐变扫到一半，行尾那截永远停在旧颜色，
        // 换什么画刷都不生效。改完前景强制重排一次，保证每个字形运行
        // 都拿到当前画刷。
        InvalidateTextLayout();
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

    private static IBrush CreateGradient(IBrush sung, IBrush accent, IBrush future, double progress,
        double start, double width, bool relativeToRun = false) {
        var edge = Math.Clamp(progress, 0.001, 0.999);
        // 过渡带按"单位宽度"的比例算：这里唯一的单位就是当前这个词（约 32px），
        // 0.16 约 5px，跟随字形大小。
        var feather = 0.16;
        var fadeStart = Math.Max(0, edge - feather);
        var fadeEnd = Math.Min(1, edge + feather);
        var sungColor = ColorOf(sung);
        var accentColor = LiftAccent(sungColor, ColorOf(accent));
        var futureColor = ColorOf(future);

        // 逐词级必须用绝对坐标，把渐变夹在"当前词自己"的排版区间里。
        // 用相对坐标的话 Avalonia 会按整段文字铺开——实测同一个渐变铺在两个字上，
        // 每个字都会从整行最左端重新扫一遍，一行里好几个字同时露出过渡色。
        var absolute = !relativeToRun && width > 0.5;
        LinearGradientBrush brush;
        if (absolute) {
            brush = new LinearGradientBrush {
                StartPoint = new RelativePoint(start, 0, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(start + width, 0, RelativeUnit.Absolute)
            };
        }
        else {
            brush = new LinearGradientBrush {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
            };
        }

        brush.GradientStops.Add(new GradientStop(sungColor, 0));
        brush.GradientStops.Add(new GradientStop(sungColor, fadeStart));
        brush.GradientStops.Add(new GradientStop(accentColor, edge));
        brush.GradientStops.Add(new GradientStop(futureColor, fadeEnd));
        brush.GradientStops.Add(new GradientStop(futureColor, 1));
        return brush;
    }

    /// <summary>
    /// 参与渐变的高亮色。
    ///
    /// 扫过边界的颜色必须是整条渐变里最亮的，否则"已唱 → 高亮 → 未唱"就不再单调：
    /// 唱过的字和前面已完成的字之间会塌出一条暗带，一行看起来像被切成好几段。
    /// 强调色来自封面提取（可能是深棕或低饱和色，比已唱白暗得多），直接拿去当边界色就会这样。
    /// 所以强调色只在确实比已唱色更亮时才用，否则退回到已唱色，保证从已唱色平滑过渡到未唱色。
    /// </summary>
    private static Color LiftAccent(Color sung, Color accent) =>
        Luminance(accent) >= Luminance(sung) ? accent : sung;

    private static double Luminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;

    private static Color ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color : Colors.White;
}
