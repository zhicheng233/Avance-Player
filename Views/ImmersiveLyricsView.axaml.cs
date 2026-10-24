using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avance_Player.Controls;
using Avance_Player.ViewModels;

namespace Avance_Player.Views;

public partial class ImmersiveLyricsView : UserControl {
    private MainWindowViewModel? _viewModel;
    private CancellationTokenSource? _entranceCts;
    private CancellationTokenSource? _exitCts;
    private bool _isExiting;

    public ImmersiveLyricsView() {
        InitializeComponent();
        // 视图一开始是隐藏的；几组元素保持透明，第一次入场才不会先闪一帧全不透明。
        ResetGroup(Backdrop);
        ResetGroup(TopBar);
        ResetGroup(ArtworkPanel);
        ResetGroup(LyricsColumn);
        ResetGroup(TransportBar);
    }

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        Unsubscribe();
        if (DataContext is MainWindowViewModel viewModel) {
            _viewModel = viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is null && DataContext is MainWindowViewModel viewModel) {
            _viewModel = viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        if (_viewModel?.IsLyricsMode == true) {
            Dispatcher.UIThread.Post(PlayEntrance, DispatcherPriority.Loaded);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        CancelEntrance();
        CancelExit();
        Unsubscribe();
    }

    private void Unsubscribe() {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(MainWindowViewModel.IsLyricsMode)) return;
        if (_viewModel?.IsLyricsMode == true) {
            // 等可见性变化和布局跑完，transform 从最终排版尺寸开始，不会拿到旧尺寸。
            Dispatcher.UIThread.Post(PlayEntrance, DispatcherPriority.Loaded);
        }
        else {
            CancelEntrance();
            CancelExit();
        }
    }

    /// <summary>
    /// 分段入场：背景先亮，然后封面落位，歌词列从右边滑进来，最后传输栏升起来。
    /// 每段都短，整套下来不到一秒。
    /// </summary>
    private void PlayEntrance() {
        if (_viewModel?.IsLyricsMode != true) return;

        _entranceCts?.Cancel();
        _entranceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _entranceCts = cts;
        var token = cts.Token;

        AnimateGroup(Backdrop, 0.0, 1.0, 620, 0, 0, 0, 1.0, token);
        AnimateGroup(TopBar, 0.0, 1.0, 420, 30, 0, -12, 1.0, token);
        AnimateGroup(ArtworkPanel, 0.0, 1.0, 560, 60, 0, 26, 0.94, token);
        AnimateGroup(LyricsColumn, 0.0, 1.0, 520, 120, 26, 0, 1.0, token);
        AnimateGroup(TransportBar, 0.0, 1.0, 480, 180, 0, 22, 1.0, token);

        // 亮的是环境光场本身，不只是容器的 opacity。
        _ = Motion.RunAsync(Backdrop, FluidBackdrop.GlowOpacityProperty, 0.15, 1.0,
            TimeSpan.FromMilliseconds(900), TimeSpan.Zero, Motion.EaseOut, token);
    }

    private void StopEntrance() {
        _entranceCts?.Cancel();
        _entranceCts?.Dispose();
        _entranceCts = null;
    }

    private void CancelEntrance() {
        StopEntrance();

        ResetGroup(Backdrop);
        ResetGroup(TopBar);
        ResetGroup(ArtworkPanel);
        ResetGroup(LyricsColumn);
        ResetGroup(TransportBar);
        Backdrop.GlowOpacity = 1;
    }

    /// <summary>
    /// 动画退回曲库。视图先留着，几组元素按入场的反序退场；
    /// 最后一帧走完，view model 才把视图藏掉。
    /// </summary>
    public void ExitWithAnimation() {
        _ = ExitWithAnimationAsync();
    }

    private async Task ExitWithAnimationAsync() {
        if (_isExiting || _viewModel?.IsLyricsMode != true) return;
        _isExiting = true;
        StopEntrance();
        CancelExit();

        var cts = new CancellationTokenSource();
        _exitCts = cts;
        var token = cts.Token;
        try {
            await Task.WhenAll(
                AnimateGroupOutAsync(TransportBar, TimeSpan.FromMilliseconds(220), TimeSpan.Zero,
                    Motion.TranslateScale(0, 20, 0.98), token),
                AnimateGroupOutAsync(LyricsColumn, TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(30),
                    Motion.TranslateScale(24, 0, 0.99), token),
                AnimateGroupOutAsync(ArtworkPanel, TimeSpan.FromMilliseconds(260), TimeSpan.FromMilliseconds(50),
                    Motion.TranslateScale(0, 18, 0.96), token),
                AnimateGroupOutAsync(TopBar, TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(70),
                    Motion.TranslateScale(0, -12, 1), token),
                AnimateGroupOutAsync(Backdrop, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(80),
                    Motion.TranslateScale(0, 0, 1), token));
        }
        finally {
            if (!token.IsCancellationRequested) _viewModel?.ExitLyricsCommand.Execute(null);
            _isExiting = false;
            if (ReferenceEquals(_exitCts, cts)) {
                _exitCts = null;
                cts.Dispose();
            }
        }
    }

    private static async Task AnimateGroupOutAsync(Visual visual, TimeSpan duration, TimeSpan delay,
        string endTransform, CancellationToken token) {
        visual.RenderTransformOrigin = RelativePoint.Center;
        var fromOpacity = visual.Opacity;
        try {
            await Task.WhenAll(
                Motion.RunAsync(visual, Visual.OpacityProperty, fromOpacity, 0, duration, delay,
                    Motion.EaseOut, token),
                Motion.RunTransformAsync(visual, Motion.TranslateScale(0, 0, 1), endTransform,
                    duration, delay, Motion.EaseOut, token));
        }
        catch (OperationCanceledException) {
            // 视图被别的路径摘掉或者藏起来了。
        }
    }

    private void CancelExit() {
        _exitCts?.Cancel();
        _exitCts?.Dispose();
        _exitCts = null;
    }

    private void BackToLibrary_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        ExitWithAnimation();
    }

    private static void ResetGroup(Visual visual) {
        // 只在视图隐藏时调用；几组元素保持透明，下次入场也不会闪一帧。
        visual.Opacity = 0;
        visual.RenderTransform = null;
    }

    private void AnimateGroup(Visual visual, double fromOpacity, double toOpacity, double durationMs,
        double delayMs, double offsetX, double offsetY, double scaleFrom, CancellationToken token) {
        visual.RenderTransformOrigin = RelativePoint.Center;
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var delay = TimeSpan.FromMilliseconds(delayMs);
        _ = AnimateGroupAsync(visual, fromOpacity, toOpacity, duration, delay,
            Motion.TranslateScale(offsetX, offsetY, scaleFrom), token);
    }

    private static async Task AnimateGroupAsync(Visual visual, double fromOpacity, double toOpacity,
        TimeSpan duration, TimeSpan delay, string startTransform, CancellationToken token) {
        try {
            await Task.WhenAll(
                Motion.RunAsync(visual, Visual.OpacityProperty, fromOpacity, toOpacity, duration, delay, Motion.EaseOut, token),
                Motion.RunTransformAsync(visual, startTransform, Motion.TranslateScale(0, 0, 1),
                    duration, delay, Motion.EaseOut, token));
        }
        finally {
            // identity transform 要清掉，歌词文字才留在没变换的栅格路径上。
            if (!token.IsCancellationRequested) visual.RenderTransform = null;
        }
    }
}
