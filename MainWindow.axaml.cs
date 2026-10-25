using Avalonia;
using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avance_Player.Models;
using Avance_Player.Services.Input;
using Avance_Player.Services.Lyrics;
using Avance_Player.ViewModels;
using Avance_Player.Views;

namespace Avance_Player;

public partial class MainWindow : Window {
    private static readonly TimeSpan LyricScrollDuration = TimeSpan.FromMilliseconds(460);
    private static readonly long SongRowDoubleTapTicks = (long)(Stopwatch.Frequency * 0.45);
    private const int RetryCenterAttempts = 6;

    /// <summary>
    /// 补列表上下 padding 的重试次数。刚进歌词页时第一行的容器还没量出高度，
    /// 那一帧算不出 padding，得等布局在下面几帧里跑完。
    /// </summary>
    private const int LyricPaddingRetryAttempts = 12;

    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private MainWindowViewModel? _viewModel;
    private FloatingLyricsWindow? _floatingLyricsWindow;
    private DispatcherTimer? _lyricScrollTimer;
    private ScrollViewer? _lyricScrollViewer;
    private double _lyricScrollFrom;
    private double _lyricScrollTo;
    private DateTime _lyricScrollStarted;
    private int _lyricScrollGeneration;
    private Music? _lastTappedTrack;
    private long _lastTapTimestamp;
    private long _lastRowPlaybackTimestamp;
    private GlobalHotkeyService? _globalHotkeys;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private NativeMenuItem? _trayShowHideItem;
    private NativeMenuItem? _trayPlayPauseItem;
    private DispatcherTimer? _seekRepeatTimer;
    private int _seekDirection;

    // 空格是否还按着：用来吃掉系统的按键自动重复，避免播放状态被连翻。
    private bool _spaceHeld;
    private DateTime _seekStartedUtc;
    private long _lastVolumeStepTimestamp;
    private bool _quitRequested;
    private bool _isClosed;

    public MainWindow() {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.Text.PropertyChanged += Text_PropertyChanged;
        _viewModel.RaiseRequested += ShowFromSystemMedia;
        _viewModel.QuitRequested += QuitFromSystemMedia;

        // 用 tunnel 阶段，Space 不会先激活当前焦点按钮，方向键也不会先滚页面，
        // 播放快捷键才抢得到。
        AddHandler(KeyDownEvent, MainWindow_PreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, MainWindow_PreviewKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        Activated += (_, _) => {
            if (_globalHotkeys is not null) _globalHotkeys.AppIsActive = true;
            UpdateTrayMenu();
        };
        Deactivated += (_, _) => {
            if (_globalHotkeys is not null) _globalHotkeys.AppIsActive = false;
            StopSeekRepeat();
        };

        CreateTrayIcon();
        Closing += MainWindow_Closing;
        _globalHotkeys = new GlobalHotkeyService(HandleGlobalHotkey);
        _globalHotkeys.AppIsActive = IsActive;
        _globalHotkeys.Start();

        SizeChanged += (_, _) => {
            UpdateCompactLayout();
            if (_viewModel?.IsLyricsMode == true)
                RequestLyricScroll();
        };
        Opened += async (_, _) => {
            UpdateCompactLayout();
            PositionWindowOnScreen();
            UpdateTrayMenu();
            if (_viewModel is not null) await _viewModel.InitializeAsync();
        };
        Closed += (_, _) => {
            _isClosed = true;
            _floatingLyricsWindow?.Close();
            StopSeekRepeat();
            _globalHotkeys?.Dispose();
            if (_viewModel is not null) {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.Text.PropertyChanged -= Text_PropertyChanged;
                _viewModel.RaiseRequested -= ShowFromSystemMedia;
                _viewModel.QuitRequested -= QuitFromSystemMedia;
                _viewModel.Dispose();
                _viewModel = null;
            }

            try {
                if (_trayIcons is not null && Application.Current is { } application)
                    TrayIcon.SetIcons(application, new TrayIcons());
                _trayIcon?.Dispose();
            }
            catch (Exception) {
                // 托盘原生销毁时偶尔会撞上，忽略就行。
            }
            finally {
                _trayIcon = null;
                _trayIcons = null;
            }
        };
    }

    private void UpdateCompactLayout() {
        if (_viewModel is null) return;
        _viewModel.IsCompactLayout = Bounds.Width < 1120;
    }

    private void ProgressBar_SeekStarted(object? sender, EventArgs e) => _viewModel?.BeginSeek();

    private void ProgressBar_SeekCompleted(object? sender, double seconds) => _viewModel?.CommitSeek(seconds);

    private void SongRow_Tapped(object? sender, TappedEventArgs e) {
        if (!TryGetSongRowTrack(sender, e, out var track)) return;

        // 双击自己判，不靠平台的双击计数。
        // 行上只有双击动作，单击本来就该什么都不做。
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
        // 有的平台同一次手势会同时报 Tapped 和 DoubleTapped，这里只放一次播放。
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

        if (e.PropertyName is nameof(MainWindowViewModel.IsPlaying)
            or nameof(MainWindowViewModel.CurrentTrack)
            or nameof(MainWindowViewModel.Volume)) {
            UpdateTrayMenu();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentLyricIndex)
            || (e.PropertyName == nameof(MainWindowViewModel.IsLyricsMode) && _viewModel?.IsLyricsMode == true)) {
            RequestLyricScroll();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentTrack)) {
            // 切歌：上一首的滚动动画和排队的居中请求全部作废，再从新歌的第一行重新算。
            CancelLyricScroll();
            RequestLyricScroll();
        }
    }

    /// <summary>作废所有在途的滚动请求，并停掉正在跑的滚动动画。</summary>
    private void CancelLyricScroll() {
        _lyricScrollGeneration++;
        StopLyricScroll();
    }

    private void SyncFloatingLyricsWindow() {
        if (_viewModel is null) return;
        if (_viewModel.IsFloatingLyricsVisible) {
            if (_floatingLyricsWindow is null) {
                _floatingLyricsWindow = new FloatingLyricsWindow { DataContext = _viewModel };
                _floatingLyricsWindow.Closed += (_, _) => _floatingLyricsWindow = null;
                // 窗口按内容自适应高度，切歌/歌词行出现或消失时高度会变，跟着重新贴底。
                _floatingLyricsWindow.ContentSizeChanged += (_, _) => PositionFloatingLyricsWindow();
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
        // 窗口高度随内容走（没有歌词行时更矮），所以每次都按当前实际高度重新贴底，
        // 不能用固定的设计高度，否则内容变多变少后会飘。
        var height = _floatingLyricsWindow.Bounds.Height;
        if (!double.IsFinite(height) || height <= 1) height = _floatingLyricsWindow.Height;
        if (!double.IsFinite(height) || height <= 1) height = 120;
        _floatingLyricsWindow.Position = new PixelPoint(
            area.X + area.Width - (int)_floatingLyricsWindow.Width - 32,
            area.Y + area.Height - (int)Math.Round(height) - 40);
    }

    private void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e) {
        if (_viewModel is null || IsTextInputFocused()) return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (control && alt) {
            switch (e.Key) {
                case Key.P:
                    _viewModel.TogglePlaybackCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Left:
                    _viewModel.PreviousTrackCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Right:
                    _viewModel.NextTrackCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Up:
                    AdjustVolume(0.03);
                    e.Handled = true;
                    return;
                case Key.Down:
                    AdjustVolume(-0.03);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Space && !control && !alt && !meta) {
            // 按住空格时系统会不停补发 KeyDown，每一下都会把播放状态翻一次，
            // 松手那一刻落在哪个状态全看运气——表现出来就是"暂停了又自己接着放"。
            // 这里只认按下后的第一下，等 KeyUp 才重新放行。方向键的自动重复是故意保留的。
            if (_spaceHeld) {
                e.Handled = true;
                return;
            }

            _spaceHeld = true;
            _viewModel.TogglePlaybackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right && control && !alt && !meta) {
            if (e.Key == Key.Left) _viewModel.PreviousTrackCommand.Execute(null);
            else _viewModel.NextTrackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right && !control && !alt && !meta) {
            StartSeek(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Up or Key.Down && !control && !alt && !meta) {
            AdjustVolume(e.Key == Key.Up ? 0.03 : -0.03);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && HandleEscape()) e.Handled = true;
    }

    private void MainWindow_PreviewKeyUp(object? sender, KeyEventArgs e) {
        // 空格松手才允许下一次切换，避免自动重复连着翻状态。
        if (e.Key == Key.Space) _spaceHeld = false;
        if (e.Key is Key.Left or Key.Right) StopSeekRepeat();
    }

    private bool HandleEscape() {
        if (_viewModel is null) return false;
        if (_viewModel.HasActivePopover) {
            _viewModel.ActivePopover = string.Empty;
            return true;
        }

        if (_viewModel.IsLyricsMode) {
            ImmersiveView.ExitWithAnimation();
            return true;
        }

        if (_viewModel.IsSystemFullscreen) {
            _viewModel.IsSystemFullscreen = false;
            return true;
        }

        return false;
    }

    private bool IsTextInputFocused() {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox or NumericUpDown or AutoCompleteBox;
    }

    private void StartSeek(int direction) {
        if (_viewModel is null || _seekDirection == direction) return;
        StopSeekRepeat();
        _seekDirection = direction;
        _seekStartedUtc = DateTime.UtcNow;
        SeekBy(direction * 5);

        if (_seekRepeatTimer is null) {
            _seekRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _seekRepeatTimer.Tick += SeekRepeatTimer_Tick;
        }
        _seekRepeatTimer.Start();
    }

    private void SeekRepeatTimer_Tick(object? sender, EventArgs e) {
        if (_viewModel is null || _seekDirection == 0) {
            StopSeekRepeat();
            return;
        }

        var held = DateTime.UtcNow - _seekStartedUtc;
        var step = held.TotalMilliseconds switch {
            < 420 => 0,
            < 1100 => 1.5,
            < 2200 => 3.0,
            _ => 6.0
        };
        if (step > 0) SeekBy(_seekDirection * step);
    }

    private void SeekBy(double seconds) {
        if (_viewModel is null) return;
        _viewModel.CurrentPositionSeconds = Math.Clamp(
            _viewModel.CurrentPositionSeconds + seconds,
            0,
            _viewModel.CurrentDurationSeconds);
    }

    private void StopSeekRepeat() {
        _seekDirection = 0;
        _seekRepeatTimer?.Stop();
    }

    private void AdjustVolume(double delta) {
        if (_viewModel is null) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _lastVolumeStepTimestamp < Stopwatch.Frequency / 16) return;
        _lastVolumeStepTimestamp = now;
        _viewModel.Volume = Math.Clamp(_viewModel.Volume + delta, 0, 1);
    }

    private void HandleGlobalHotkey(GlobalHotkeyAction action) {
        Dispatcher.UIThread.Post(() => {
            // hook 和激活事件在某些合成器上会抢跑。窗口已经在前台时，快捷键归 tunnel 的按键处理；
            // 最小化或隐藏的窗口还是得走全局。
            if (_viewModel is null || (IsActive && IsWindowShown)) return;
            switch (action) {
                case GlobalHotkeyAction.TogglePlayback:
                    _viewModel.TogglePlaybackCommand.Execute(null);
                    break;
                case GlobalHotkeyAction.PreviousTrack:
                    _viewModel.PreviousTrackCommand.Execute(null);
                    break;
                case GlobalHotkeyAction.NextTrack:
                    _viewModel.NextTrackCommand.Execute(null);
                    break;
                case GlobalHotkeyAction.VolumeUp:
                    AdjustVolume(0.03);
                    break;
                case GlobalHotkeyAction.VolumeDown:
                    AdjustVolume(-0.03);
                    break;
            }
        });
    }

    private void ShowFromSystemMedia() {
        Dispatcher.UIThread.Post(ShowMainWindow);
    }

    private void ShowMainWindow() {
        if (_isClosed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_globalHotkeys is not null) _globalHotkeys.AppIsActive = true;
        if (_viewModel?.IsFloatingLyricsVisible == true) SyncFloatingLyricsWindow();
        UpdateTrayMenu();
    }

    private void HideMainWindow() {
        if (_isClosed) return;
        StopSeekRepeat();
        _viewModel?.SaveStateNow();
        Hide();
        if (_globalHotkeys is not null) _globalHotkeys.AppIsActive = false;
        _floatingLyricsWindow?.Hide();
        UpdateTrayMenu();
    }

    private void QuitFromSystemMedia() {
        Dispatcher.UIThread.Post(QuitApplication);
    }

    /// <summary>
    /// 关窗口时托盘把播放器留在后台；真要退出得走托盘或系统媒体命令。
    /// 没有可用的托盘宿主时，关窗口还是得退出。
    /// </summary>
    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e) {
        if (_quitRequested || _trayIcon is null) return;
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
        e.Cancel = true;
        HideMainWindow();
    }

    private void QuitApplication() {
        if (_isClosed) return;
        _quitRequested = true;
        Close();
    }

    private void CreateTrayIcon() {
        if (_viewModel is null) return;
        try {
            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Avance-Player/Assets/tray.png")));
            Icon = icon;
            _trayIcon = new TrayIcon {
                Icon = icon,
                ToolTipText = _viewModel.Text["TrayTooltip"],
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ToggleWindowVisibility();
            RebuildTrayMenu();
            _trayIcons = new TrayIcons { _trayIcon };
            if (Application.Current is { } application) TrayIcon.SetIcons(application, _trayIcons);
        }
        catch (Exception) {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayIcons = null;
        }
    }

    private bool IsWindowShown => IsVisible && WindowState != WindowState.Minimized;

    private void RebuildTrayMenu() {
        if (_trayIcon is null || _viewModel is null) return;

        try {
            _trayShowHideItem = new NativeMenuItem {
                Header = _viewModel.Text[IsWindowShown ? "TrayHide" : "TrayShow"],
                Command = new RelayCommand(_ => ToggleWindowVisibility())
            };
            _trayPlayPauseItem = new NativeMenuItem {
                Header = _viewModel.Text["TrayPlayPause"],
                Command = _viewModel.TogglePlaybackCommand
            };

            var menu = new NativeMenu();
            menu.Items.Add(_trayShowHideItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(_trayPlayPauseItem);
            menu.Items.Add(new NativeMenuItem {
                Header = _viewModel.Text["TrayPrevious"],
                Command = _viewModel.PreviousTrackCommand
            });
            menu.Items.Add(new NativeMenuItem {
                Header = _viewModel.Text["TrayNext"],
                Command = _viewModel.NextTrackCommand
            });
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem {
                Header = _viewModel.Text["TrayQuit"],
                Command = new RelayCommand(_ => QuitApplication())
            });
            _trayIcon.Menu = menu;
        }
        catch (Exception) {
            // 运行中平台托盘宿主可能会没。
        }
    }

    private void UpdateTrayMenu() {
        if (_trayIcon is null || _viewModel is null) return;

        try {
            _trayIcon.ToolTipText = $"{_viewModel.Text["TrayTooltip"]} · {_viewModel.CurrentTrack.Title}";
            if (_trayShowHideItem is not null)
                _trayShowHideItem.Header = _viewModel.Text[IsWindowShown ? "TrayHide" : "TrayShow"];
            if (_trayPlayPauseItem is not null)
                _trayPlayPauseItem.Header = _viewModel.Text[_viewModel.IsPlaying ? "TrayPause" : "TrayPlay"];
        }
        catch (Exception) {
            // 托盘宿主没了就忽略。
        }
    }

    private void ToggleWindowVisibility() {
        if (_isClosed) return;
        if (IsVisible && WindowState != WindowState.Minimized) {
            HideMainWindow();
            return;
        }

        ShowMainWindow();
    }

    private void Text_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName is "Item[]" or "Item") {
            RebuildTrayMenu();
            UpdateTrayMenu();
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

    private void RequestLyricScroll() {
        if (_viewModel is null || !_viewModel.IsLyricsMode) return;
        var lyricsList = FindLyricsList();
        if (lyricsList is null) {
            // 歌词页刚切进来时视图可能还没挂到可视树，这时拿不到列表。
            // 别把请求丢掉，等布局跑一轮再试，否则第一行会一直顶在最上面。
            RetryRequestLyricScroll(++_lyricScrollGeneration, LyricPaddingRetryAttempts);
            return;
        }

        // 每次请求都换一代令牌。旧代里排队的后台回调看到令牌不对就直接放弃，
        // 免得切歌时上一首的滚动把新歌的位置又拽回去。
        var generation = ++_lyricScrollGeneration;
        Dispatcher.UIThread.Post(
            () => ApplyLyricScroll(lyricsList, generation, LyricPaddingRetryAttempts),
            DispatcherPriority.Background);
    }

    private ItemsControl? FindLyricsList() => this.GetVisualDescendants()
        .OfType<ItemsControl>()
        .FirstOrDefault(control => control.Name == "LyricsList");

    /// <summary>
    /// 视图还没挂进可视树时的补救：按帧重试找列表，找到就接着走正常的居中流程。
    /// </summary>
    private void RetryRequestLyricScroll(int generation, int attemptsLeft) {
        if (attemptsLeft <= 0) return;
        Dispatcher.UIThread.Post(() => {
            if (generation != _lyricScrollGeneration || _viewModel?.IsLyricsMode != true) return;

            if (FindLyricsList() is not { } lyricsList) {
                RetryRequestLyricScroll(generation, attemptsLeft - 1);
                return;
            }

            ApplyLyricScroll(lyricsList, generation, LyricPaddingRetryAttempts);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 先补上下 padding，再居中。<paramref name="paddingAttemptsLeft"/> 是留给"容器还没量出高度"的重试次数：
    /// 刚进歌词页、刚切歌时第一行的容器还没有 Bounds，这时算不出 padding，
    /// 而 0 padding 下 ScrollViewer 的 offset 被夹在 0，第一行就永远顶在最上面。
    /// </summary>
    private void ApplyLyricScroll(ItemsControl lyricsList, int generation, int paddingAttemptsLeft) {
        if (generation != _lyricScrollGeneration) return;

        var paddingReady = UpdateLyricListPadding(lyricsList);
        // 改 padding 会让布局失效；等下一轮后台排版再居中，读到的才是新容器位置。
        Dispatcher.UIThread.Post(() => {
            if (generation != _lyricScrollGeneration) return;

            if (paddingReady) {
                CenterActiveLyric(lyricsList, _viewModel?.CurrentLyricIndex ?? -1);
                return;
            }

            if (paddingAttemptsLeft <= 0) return;
            ApplyLyricScroll(lyricsList, generation, paddingAttemptsLeft - 1);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// ScrollViewer 的 offset 卡在 0/max，不加 padding 的话第一行和最后一行永远到不了垂直中心。
    /// 第一项上面、最后一项下面各留半屏，每行才有一样的居中范围。
    /// 返回 false 表示这一帧还算不出来（容器没就绪），调用方应该重试。
    /// </summary>
    private static bool UpdateLyricListPadding(ItemsControl lyricsList) {
        var panel = lyricsList.ItemsPanelRoot;
        var scrollViewer = lyricsList.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (panel is null || scrollViewer is null || scrollViewer.Viewport.Height <= 0 || lyricsList.ItemCount == 0) {
            return false;
        }

        var first = lyricsList.ContainerFromIndex(0) as Control;
        var last = lyricsList.ContainerFromIndex(lyricsList.ItemCount - 1) as Control;
        if (first is null || last is null || first.Bounds.Height <= 0 || last.Bounds.Height <= 0) {
            return false;
        }

        var top = Math.Max(0, (scrollViewer.Viewport.Height - first.Bounds.Height) / 2);
        var bottom = Math.Max(0, (scrollViewer.Viewport.Height - last.Bounds.Height) / 2);
        var margin = new Thickness(0, top, 0, bottom);
        if (panel.Margin != margin) panel.Margin = margin;
        return true;
    }

    /// <summary>
    /// 把当前行滚到垂直中心。<paramref name="index"/> 为负（还没唱到第一行）时回到顶部，
    /// 原来是直接 return，切歌后列表就停在上一首的位置不动。
    /// </summary>
    private void CenterActiveLyric(ItemsControl lyricsList, int index) {
        if (_viewModel?.IsLyricsMode != true) return;
        var scrollViewer = lyricsList.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null) return;

        if (index < 0) {
            StartLyricScroll(scrollViewer, 0);
            return;
        }

        var container = lyricsList.ContainerFromIndex(index);
        if (container is null) {
            // 虚拟化还没把这一行的容器造出来。下几帧再试，别把这次的居中丢掉。
            RetryCenterActiveLyric(lyricsList, index, RetryCenterAttempts);
            return;
        }

        var position = container.TranslatePoint(new Point(0, 0), scrollViewer);
        if (position is null) {
            RetryCenterActiveLyric(lyricsList, index, RetryCenterAttempts);
            return;
        }

        var centeredOffset = scrollViewer.Offset.Y + position.Value.Y -
                             (scrollViewer.Viewport.Height - container.Bounds.Height) / 2;
        StartLyricScroll(scrollViewer, Math.Max(0, centeredOffset));
    }

    /// <summary>容器还没就绪时按帧重试；超过次数就认了，反正下一行一定会再来一次请求。</summary>
    private void RetryCenterActiveLyric(ItemsControl lyricsList, int index, int attemptsLeft) {
        if (attemptsLeft <= 0) return;
        var generation = _lyricScrollGeneration;
        Dispatcher.UIThread.Post(() => {
            if (generation != _lyricScrollGeneration || _viewModel?.IsLyricsMode != true) return;
            if (lyricsList.ContainerFromIndex(index) is not Control) {
                RetryCenterActiveLyric(lyricsList, index, attemptsLeft - 1);
                return;
            }

            CenterActiveLyric(lyricsList, index);
        }, DispatcherPriority.Background);
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
        // 四次方 ease-out：起步快，最后慢慢贴到当前行。
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
