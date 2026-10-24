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
    /// 悬浮卡片是显示/隐藏，不是重建，所以每次出现都重放一下上浮淡入。
    /// 看起来像从播放器控制台里滑出来。
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
        // 从命中目标往上找，点在按钮内容（图标、文字）上也还是点按钮，不会变成拖窗口。
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
