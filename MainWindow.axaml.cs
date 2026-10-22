using Avalonia;
using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avance_Player.Models;
using Avance_Player.Services.Lyrics;
using Avance_Player.ViewModels;
using Avance_Player.Views;

namespace Avance_Player;

public partial class MainWindow : Window {
    private static readonly TimeSpan LyricScrollDuration = TimeSpan.FromMilliseconds(460);
    private static readonly long SongRowDoubleTapTicks = (long)(Stopwatch.Frequency * 0.45);

    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private MainWindowViewModel? _viewModel;
    private FloatingLyricsWindow? _floatingLyricsWindow;
    private DispatcherTimer? _lyricScrollTimer;
    private ScrollViewer? _lyricScrollViewer;
    private double _lyricScrollFrom;
    private double _lyricScrollTo;
    private DateTime _lyricScrollStarted;
    private Music? _lastTappedTrack;
    private long _lastTapTimestamp;
    private long _lastRowPlaybackTimestamp;

    public MainWindow() {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        KeyDown += MainWindow_KeyDown;
        SizeChanged += (_, _) => {
            UpdateCompactLayout();
            if (_viewModel?.IsLyricsMode == true)
                Dispatcher.UIThread.Post(ScrollActiveLyric, DispatcherPriority.Background);
        };
        Opened += (_, _) => {
            UpdateCompactLayout();
            PositionWindowOnScreen();
        };
        Closed += (_, _) => {
            _floatingLyricsWindow?.Close();
            if (_viewModel is null) return;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.Dispose();
        };
    }

    private void UpdateCompactLayout() {
        if (_viewModel is null) return;
        _viewModel.IsCompactLayout = Bounds.Width < 1120;
    }

    private void SongRow_Tapped(object? sender, TappedEventArgs e) {
        if (!TryGetSongRowTrack(sender, e, out var track)) return;

        // Detect the second tap ourselves instead of relying on platform double-click counts.
        // The row only has a double-click action, so a single tap is intentionally a no-op.
        var now = Stopwatch.GetTimestamp();
        if (ReferenceEquals(track, _lastTappedTrack) && now - _lastTapTimestamp <= SongRowDoubleTapTicks) {
            _lastTappedTrack = null;
            _lastTapTimestamp = 0;
            PlaySongRow(track, now);
            e.Handled = true;
            return;
        }

        _lastTappedTrack = track;
        _lastTapTimestamp = now;
    }

    private void SongRow_DoubleTapped(object? sender, TappedEventArgs e) {
        if (!TryGetSongRowTrack(sender, e, out var track)) return;
        _lastTappedTrack = null;
        _lastTapTimestamp = 0;
        PlaySongRow(track, Stopwatch.GetTimestamp());
        e.Handled = true;
    }

    private bool TryGetSongRowTrack(object? sender, TappedEventArgs e, out Music track) {
        track = null!;
        if (_viewModel is null || sender is not Control { DataContext: Music rowTrack }) return false;
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return false;
        track = rowTrack;
        return true;
    }

    private void PlaySongRow(Music track, long now) {
        // A platform may report both Tapped and DoubleTapped for the same gesture; keep one play.
        if (now - _lastRowPlaybackTimestamp < Stopwatch.Frequency / 10) return;
        _lastRowPlaybackTimestamp = now;
        _viewModel?.PlayTrackCommand.Execute(track);
    }

    private void ContentRoot_PointerPressed(object? sender, PointerPressedEventArgs e) {
        if (_viewModel?.HasActivePopover != true) return;
        if (e.Source is Control { } source && FindPopover(source) is not null) return;
        _viewModel.ClosePopoverIfAny();
    }

    private static Control? FindPopover(Control source) {
        for (var current = source; current is not null; current = current.Parent as Control) {
            if (current.Classes.Contains("popover-panel") || current.Classes.Contains("console-shell")) return current;
        }
        return null;
    }

    private void PositionWindowOnScreen() {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        Width = Math.Min(1360, area.Width - 80);
        Height = Math.Min(900, area.Height - 80);
        Position = new PixelPoint(
            area.X + (area.Width - (int)Width) / 2,
            area.Y + (area.Height - (int)Height) / 2);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSystemFullscreen)) {
            if (_viewModel?.IsSystemFullscreen == true) {
                if (WindowState != WindowState.FullScreen) _windowStateBeforeFullscreen = WindowState;
                WindowState = WindowState.FullScreen;
            }
            else if (WindowState == WindowState.FullScreen) {
                WindowState = _windowStateBeforeFullscreen == WindowState.FullScreen
                    ? WindowState.Normal
                    : _windowStateBeforeFullscreen;
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsFloatingLyricsVisible)) {
            SyncFloatingLyricsWindow();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsMode) && _viewModel?.IsLyricsMode != true) {
            StopLyricScroll();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentLyricIndex)
                 || e.PropertyName == nameof(MainWindowViewModel.CurrentTrack)
                 || (e.PropertyName == nameof(MainWindowViewModel.IsLyricsMode) && _viewModel?.IsLyricsMode == true)) {
            Dispatcher.UIThread.Post(ScrollActiveLyric, DispatcherPriority.Background);
        }
    }

    private void SyncFloatingLyricsWindow() {
        if (_viewModel is null) return;
        if (_viewModel.IsFloatingLyricsVisible) {
            if (_floatingLyricsWindow is null) {
                _floatingLyricsWindow = new FloatingLyricsWindow { DataContext = _viewModel };
                _floatingLyricsWindow.Closed += (_, _) => _floatingLyricsWindow = null;
            }

            PositionFloatingLyricsWindow();
            _floatingLyricsWindow.Show(this);
            return;
        }

        _floatingLyricsWindow?.Hide();
    }

    private void PositionFloatingLyricsWindow() {
        if (_floatingLyricsWindow is null) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        _floatingLyricsWindow.Position = new PixelPoint(
            area.X + area.Width - (int)_floatingLyricsWindow.Width - 32,
            area.Y + area.Height - (int)_floatingLyricsWindow.Height - 40);
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Escape || _viewModel is null) return;
        if (_viewModel.HasActivePopover) {
            _viewModel.ActivePopover = string.Empty;
            e.Handled = true;
        }
        else if (_viewModel.IsLyricsMode) {
            _viewModel.IsLyricsMode = false;
            if (_viewModel.IsSystemFullscreen) _viewModel.IsSystemFullscreen = false;
            e.Handled = true;
        }
        else if (_viewModel.IsSystemFullscreen) {
            _viewModel.IsSystemFullscreen = false;
            e.Handled = true;
        }
    }

    private async void ImportLyrics_Click(object? sender, RoutedEventArgs e) {
        if (_viewModel is null) return;

        var patterns = LyricsService.SupportedExtensions.Select(extension => "*" + extension).ToList();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = _viewModel.Text["ImportLyrics"],
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType(_viewModel.Text["ImportLyricsFilter"]) { Patterns = patterns }
            ]
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await _viewModel.LoadLyricsFromFileAsync(path);
    }

    private async void ImportFolder_Click(object? sender, RoutedEventArgs e) {
        if (_viewModel?.SelectedPlaylist is not { } playlist) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = _viewModel.Text["FolderPickerTitle"],
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await _viewModel.ImportFolderToPlaylistAsync(playlist, path);
    }

    private void ScrollActiveLyric() {
        if (_viewModel is null || !_viewModel.IsLyricsMode || _viewModel.CurrentLyricIndex < 0) return;
        var lyricsList = this.GetVisualDescendants()
            .OfType<ItemsControl>()
            .FirstOrDefault(control => control.Name == "LyricsList");
        if (lyricsList is null) return;

        Dispatcher.UIThread.Post(() => {
            UpdateLyricListPadding(lyricsList);
            // The padding change invalidates layout; centring in a second background pass reads
            // the new container positions instead of the pre-padding ones.
            Dispatcher.UIThread.Post(() => CenterActiveLyric(lyricsList, _viewModel.CurrentLyricIndex),
                DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// ScrollViewer offsets clamp at zero/max, so without padding the first and last lyric can
    /// never reach the vertical centre. Half a viewport of space above the first item and below
    /// the last gives every line the same centring range.
    /// </summary>
    private static void UpdateLyricListPadding(ItemsControl lyricsList) {
        var panel = lyricsList.ItemsPanelRoot;
        var scrollViewer = lyricsList.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (panel is null || scrollViewer is null || scrollViewer.Viewport.Height <= 0 || lyricsList.ItemCount == 0) return;

        var first = lyricsList.ContainerFromIndex(0) as Control;
        var last = lyricsList.ContainerFromIndex(lyricsList.ItemCount - 1) as Control;
        if (first is null || last is null || first.Bounds.Height <= 0 || last.Bounds.Height <= 0) return;

        var top = Math.Max(0, (scrollViewer.Viewport.Height - first.Bounds.Height) / 2);
        var bottom = Math.Max(0, (scrollViewer.Viewport.Height - last.Bounds.Height) / 2);
        var margin = new Thickness(0, top, 0, bottom);
        if (panel.Margin != margin) panel.Margin = margin;
    }

    private void CenterActiveLyric(ItemsControl lyricsList, int index) {
        if (_viewModel?.IsLyricsMode != true) return;
        var container = lyricsList.ContainerFromIndex(index);
        var scrollViewer = lyricsList.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (container is null || scrollViewer is null) return;

        var position = container.TranslatePoint(new Point(0, 0), scrollViewer);
        if (position is null) return;

        var centeredOffset = scrollViewer.Offset.Y + position.Value.Y -
                             (scrollViewer.Viewport.Height - container.Bounds.Height) / 2;
        StartLyricScroll(scrollViewer, Math.Max(0, centeredOffset));
    }

    private void StartLyricScroll(ScrollViewer scrollViewer, double targetOffset) {
        if (Math.Abs(scrollViewer.Offset.Y - targetOffset) < 0.5) {
            StopLyricScroll();
            return;
        }

        _lyricScrollViewer = scrollViewer;
        _lyricScrollFrom = scrollViewer.Offset.Y;
        _lyricScrollTo = targetOffset;
        _lyricScrollStarted = DateTime.UtcNow;

        if (_lyricScrollTimer is null) {
            _lyricScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _lyricScrollTimer.Tick += LyricScrollTimer_Tick;
        }

        _lyricScrollTimer.Start();
    }

    private void LyricScrollTimer_Tick(object? sender, EventArgs e) {
        if (_lyricScrollViewer is null || _viewModel?.IsLyricsMode != true) {
            StopLyricScroll();
            return;
        }

        var elapsed = (DateTime.UtcNow - _lyricScrollStarted).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / LyricScrollDuration.TotalMilliseconds, 0, 1);
        // Quartic ease-out: quick pick-up, then a long, soft settle on the active line.
        var eased = 1 - Math.Pow(1 - progress, 4);
        var offset = _lyricScrollFrom + (_lyricScrollTo - _lyricScrollFrom) * eased;
        _lyricScrollViewer.Offset = new Vector(_lyricScrollViewer.Offset.X, offset);

        if (progress >= 1) StopLyricScroll();
    }

    private void StopLyricScroll() {
        _lyricScrollTimer?.Stop();
        _lyricScrollViewer = null;
    }
}
