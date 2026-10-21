using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avance_Player.Models;

namespace Avance_Player.Controls;

/// <summary>
/// TextBlock that renders a <see cref="LyricLine"/> as a sequence of word runs and recolours them
/// as playback moves through the line. Line-level lyrics fall back to a single run, so the same
/// control works for LRC/SRT and word-timed formats.
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

    private LyricLine? _subscribedLine;
    private bool _attached;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == LineProperty) {
            SubscribeToLine();
            Rebuild();
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
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _attached = false;
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
        if (e.PropertyName is nameof(LyricLine.IsCurrent) or nameof(LyricLine.CurrentWordIndex))
            UpdateBrushes();
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

    private void UpdateBrushes() {
        var line = Line;
        var inlines = Inlines;
        if (line is null || inlines is null || inlines.Count == 0) return;

        var foreground = SungBrush ?? Foreground ?? Brushes.White;
        if (line.Words.Count == 0) {
            inlines[0].Foreground = foreground;
            return;
        }

        var current = Math.Clamp(line.CurrentWordIndex, -1, line.Words.Count - 1);
        for (var index = 0; index < inlines.Count; index++) {
            inlines[index].Foreground = !line.IsCurrent
                ? Foreground
                : index < current
                    ? foreground
                    : index == current
                        ? AccentBrush ?? foreground
                        : FutureBrush ?? Foreground;
        }
    }
}
