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

    public ImmersiveLyricsView() {
        InitializeComponent();
        // The view starts hidden; keeping the groups transparent means the first entrance never
        // flashes a fully-opaque frame before the animation's first tick.
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
            // Wait for the visibility change and layout pass so the transforms start from the
            // final arranged size instead of a stale one.
            Dispatcher.UIThread.Post(PlayEntrance, DispatcherPriority.Loaded);
        }
        else {
            CancelEntrance();
        }
    }

    /// <summary>
    /// Staged entrance: the backdrop blooms first, then the artwork settles in, the lyric column
    /// slides in from the right, and the transport bar rises last. Each group is short enough
    /// that the whole sequence is over in under a second.
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

        // Bloom the ambient field itself, not just its container opacity.
        _ = Motion.RunAsync(Backdrop, FluidBackdrop.GlowOpacityProperty, 0.15, 1.0,
            TimeSpan.FromMilliseconds(900), TimeSpan.Zero, Motion.EaseOut, token);
    }

    private void CancelEntrance() {
        _entranceCts?.Cancel();
        _entranceCts?.Dispose();
        _entranceCts = null;

        ResetGroup(Backdrop);
        ResetGroup(TopBar);
        ResetGroup(ArtworkPanel);
        ResetGroup(LyricsColumn);
        ResetGroup(TransportBar);
        Backdrop.GlowOpacity = 1;
    }

    private static void ResetGroup(Visual visual) {
        // Called only while the view is hidden, so leaving the groups transparent also prevents
        // a one-frame flash on the next entrance.
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
            // Identity transforms are removed so the lyric text stays on the untransformed raster path.
            if (!token.IsCancellationRequested) visual.RenderTransform = null;
        }
    }
}
