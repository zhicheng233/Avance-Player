using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avance_Player.Controls;
using Avance_Player.ViewModels;

namespace Avance_Player.Views;

public partial class FloatingLyricsWindow : Window {
    private CancellationTokenSource? _entranceCts;

    public FloatingLyricsWindow() {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e) {
        base.OnOpened(e);
        PlayEntrance();
    }

    protected override void OnClosed(EventArgs e) {
        _entranceCts?.Cancel();
        _entranceCts?.Dispose();
        _entranceCts = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// The floating card is shown/hidden rather than recreated, so replay a short lift-and-fade
    /// each time it appears. It reads as a window sliding out of the player console.
    /// </summary>
    private void PlayEntrance() {
        _entranceCts?.Cancel();
        _entranceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _entranceCts = cts;
        var token = cts.Token;

        RootCard.Opacity = 0;
        RootCard.RenderTransformOrigin = RelativePoint.Center;

        var duration = TimeSpan.FromMilliseconds(280);
        _ = FinishEntranceAsync(duration, token);
    }

    private async Task FinishEntranceAsync(TimeSpan duration, CancellationToken token) {
        try {
            await Task.WhenAll(
                Motion.RunAsync(RootCard, Visual.OpacityProperty, 0, 1, duration, TimeSpan.Zero, Motion.EaseOut, token),
                Motion.RunTransformAsync(RootCard, Motion.TranslateScale(0, 14, 0.97),
                    Motion.TranslateScale(0, 0, 1), duration, TimeSpan.Zero, Motion.EaseOut, token));
        }
        finally {
            if (!token.IsCancellationRequested) RootCard.RenderTransform = null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        // Walk up from the hit target so presses on button content (glyphs, text) still click the
        // button instead of starting a window drag.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        if (DataContext is MainWindowViewModel viewModel) viewModel.IsFloatingLyricsVisible = false;
        e.Handled = true;
    }
}
