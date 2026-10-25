using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avance_Player.Models;
using Avance_Player.Services;
using Avance_Player.Services.Lyrics;
using Avance_Player.Services.Persistence;
using Avance_Player.Services.Playback;
using Avance_Player.Services.SystemMedia;
using Avalonia;
using Avalonia.Threading;

namespace Avance_Player.ViewModels;

public enum RepeatMode {
    Off,
    Track,
    Playlist
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable {
    private const double MinimumPlaybackSpeed = 0.5;
    private const double MaximumPlaybackSpeed = 2.0;
    private static readonly int[] SleepTimerMinuteValues = [0, 15, 30, 45, 60, 90];

    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _ambientTimer;
    private readonly DispatcherTimer _pitchApplyTimer;
    private readonly Random _random = new();
    private readonly Stack<Music> _shuffleHistory = new();
    private readonly HashSet<Music> _shufflePlayed = [];

    /// <summary>
    /// 用户手动从歌单里删掉、但文件还在的路径，值记录删除当时的文件指纹。
    /// 重扫时指纹对得上就不加回；文件被替换过（大小或写入时间变了）则按新歌处理。
    /// </summary>
    private readonly Dictionary<string, string> _dismissedPaths = new(PathComparer);

    /// <summary>正在重扫的根目录，防止同一个目录的多次通知叠着跑。</summary>
    private readonly HashSet<string> _refreshingRoots = new(PathComparer);

    private readonly PlayerServices _services;
    private readonly PlaybackService _playback;
    private readonly DebouncedStateSaver _stateSaver;
    private readonly LyricsService _lyricsService;
    private readonly LocalFolderScanner _folderScanner;
    private double _currentPositionSeconds;
    private double _seekPositionSeconds;
    private double? _pendingSeekTargetSeconds;
    private DateTime _pendingSeekUntilUtc;
    private bool _isSeeking;
    private bool _isPlaying;
    private bool _isRestoringState;
    private bool _suppressSeek;
    private bool _isInitialized;
    private bool _disposed;
    private DateTime _lastStateSaveUtc = DateTime.MinValue;
    private string _newPlaylistName = string.Empty;
    private string _statusKey = "StatusIdle";
    private object[] _statusArguments = [];
    private double _ambientPhase;
    private readonly Music _emptyTrack;
    private Music _currentTrack;
    private ObservableCollection<Music> _playbackQueue;
    private Playlist? _selectedPlaylist;
    private PageViewModel _currentPage;
    private string _selectedNavigation = "library";
    private bool _isLyricsMode;
    private bool _isSystemFullscreen;
    private bool _isShuffleMode;
    private RepeatMode _repeatMode;
    private string _activePopover = string.Empty;
    private bool _isFloatingLyricsVisible;
    private double _volume = 0.72;
    private double _lastNonZeroVolume = 0.72;
    private int _equalizerPresetIndex;
    private double _playbackSpeed = 1.0;
    private bool _isPitchSyncEnabled = true;
    private double _pitchSemitones;
    private bool _systemMediaCleared;
    private int _sleepTimerSelectionIndex;
    private DateTime? _sleepDeadlineUtc;
    private int _lastSleepRemainingSecond = -1;
    private int _abLoopState;
    private double? _abLoopStartSeconds;
    private double? _abLoopEndSeconds;
    private CategoryGroupViewModel? _openedCategoryGroup;
    private int _lastCurrentLyricIndex = int.MinValue;
    private int _currentLyricIndex = -1;
    private double _currentLyricIndexPosition = double.NaN;
    private bool _isCompactLayout;
    private string _expandedMoreSection = string.Empty;

    public MainWindowViewModel() : this(PlayerServices.CreateDefault()) {
    }

    public MainWindowViewModel(PlayerServices services) {
        _services = services;
        _playback = services.Playback;
        _stateSaver = services.StateSaver;
        _lyricsService = services.Lyrics;
        _folderScanner = services.Scanner;
        _playback.Ended += Playback_Ended;
        _playback.Failed += Playback_Failed;
        _stateSaver.SaveFailed += StateSaver_SaveFailed;
        _services.SystemMedia.CommandReceived += SystemMedia_CommandReceived;
        _services.FolderWatch.Changed += FolderWatch_Changed;

        Text = new LocalizedText();
        Text.PropertyChanged += Text_PropertyChanged;

        // 曲库从真的空开始。导入文件夹才会往里加；空歌曲只是个绑定占位，
        // 控制台不会把它当歌行显示。
        Tracks = [];
        Playlists = [];
        RecentTracks = [];
        _emptyTrack = CreateEmptyTrack();
        _currentTrack = _emptyTrack;
        _playbackQueue = Tracks;
        RefreshPlaybackOptions();
        Palette = MonetPaletteService.FromColorSeed(_currentTrack.ArtworkColor);
        _currentPage = new LibraryPageViewModel(Tracks);

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter?.ToString() ?? "library"));
        OpenPlaylistCommand = new RelayCommand(parameter => {
            if (parameter is Playlist playlist) OpenPlaylist(playlist);
        });
        OpenCategoryGroupCommand = new RelayCommand(parameter => {
            if (parameter is CategoryGroupViewModel group) {
                _openedCategoryGroup = group;
                CurrentPage = new TrackCollectionPageViewModel(group.Title, group.Subtitle, group.Tracks);
                SelectedNavigation = "library";
            }
        });
        PlayTrackCommand = new RelayCommand(parameter => {
            if (parameter is not Music track) return;
            var queue = PlaybackQueue.Contains(track) ? PlaybackQueue : Tracks;
            PlayTrack(track, queue);
        });
        AddTrackToPlaylistCommand = new RelayCommand(parameter => {
            if (parameter is Music track) AddTrackToPlaylist(track);
        });
        RemoveTrackFromPlaylistCommand = new RelayCommand(parameter => {
            if (parameter is Music track && SelectedPlaylist is not null && SelectedPlaylist.Tracks.Remove(track)) {
                // 手动删掉的别再被文件夹重扫塞回来。记下删除时的文件指纹，
                // 文件真被换过（重新下载/重新压制）就会再次出现。
                if (!string.IsNullOrWhiteSpace(track.FilePath)
                    && FileFingerprint(track.FilePath) is { } fingerprint) {
                    _dismissedPaths[track.FilePath] = fingerprint;
                }
                (CurrentPage as PlaylistDetailPageViewModel)?.NotifyTracksChanged();
                QueueStateSave();
            }
        });
        TogglePlaybackCommand = new RelayCommand(_ => TogglePlayback());
        StopCommand = new RelayCommand(_ => Stop());
        NextTrackCommand = new RelayCommand(_ => MoveTrack(1));
        PreviousTrackCommand = new RelayCommand(_ => MoveTrack(-1));
        ToggleShuffleCommand = new RelayCommand(_ => IsShuffleMode = !IsShuffleMode);
        CycleRepeatModeCommand = new RelayCommand(_ => CycleRepeatMode());
        TogglePopoverCommand = new RelayCommand(parameter => TogglePopover(parameter?.ToString() ?? string.Empty));
        ClosePopoverCommand = new RelayCommand(_ => ActivePopover = string.Empty);
        ToggleMoreSectionCommand = new RelayCommand(parameter => ToggleMoreSection(parameter?.ToString() ?? string.Empty));
        ToggleMuteCommand = new RelayCommand(_ => ToggleMute());
        ToggleFloatingLyricsCommand = new RelayCommand(_ => {
            if (!HasCurrentTrack && !IsFloatingLyricsVisible) {
                SetStatus("StatusNoTrack");
                return;
            }

            IsFloatingLyricsVisible = !IsFloatingLyricsVisible;
        });
        CycleEqualizerCommand = new RelayCommand(_ => CycleEqualizer());
        SelectEqualizerCommand = new RelayCommand(parameter => {
            if (parameter is PlaybackOptionViewModel option) {
                EqualizerPresetIndex = option.Index;
                SetStatus("StatusEqualizer", EqualizerPresetLabel);
            }
        });
        SetAbLoopACommand = new RelayCommand(_ => SetAbLoopA());
        SetAbLoopBCommand = new RelayCommand(_ => SetAbLoopB());
        ClearAbLoopCommand = new RelayCommand(_ => ClearAbLoop());
        SelectSleepTimerCommand = new RelayCommand(parameter => {
            if (parameter is PlaybackOptionViewModel option) SleepTimerSelectionIndex = option.Index;
        });
        OpenPlayerCommand = new RelayCommand(_ => {
            if (!HasCurrentTrack) {
                SetStatus("StatusNoTrack");
                return;
            }

            IsLyricsMode = true;
        });
        ExitLyricsCommand = new RelayCommand(_ => ExitLyricsMode());
        ToggleLanguageCommand = new RelayCommand(_ => {
            Text.ToggleLanguage();
            QueueStateSave();
        });
        ToggleFullscreenCommand = new RelayCommand(_ => IsSystemFullscreen = !IsSystemFullscreen);
        SeekToLyricCommand = new RelayCommand(parameter => {
            if (parameter is LyricLine line) CurrentPositionSeconds = line.Timestamp.TotalSeconds;
        });
        CreatePlaylistCommand = new RelayCommand(_ => CreatePlaylist());
        DeletePlaylistCommand = new RelayCommand(_ => DeleteSelectedPlaylist());

        // 100ms，进度填充和歌词时钟看起来够顺，又不用每帧跑计时器。
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
        // 大概 30fps，环境光场和进度波浪够滑，不用每帧计时。
        _ambientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _ambientTimer.Tick += AmbientTimer_Tick;
        _ambientTimer.Start();
        // 改音调挺贵（原生 runtime 可能得重载 media），等滑块停一下再应用，
        // 别指针每动一下就来一次。
        _pitchApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pitchApplyTimer.Tick += PitchApplyTimer_Tick;
        _playback.SetVolume(_volume);
        _playback.SetRate(PlaybackSpeed);
        _playback.SetPitch(PitchSemitones, IsPitchSyncEnabled);
        ApplyEqualizer();
        UpdateCurrentLyric();
        UpdateSystemMedia();
    }

    /// <summary>系统媒体控制要求把应用拉到前台时触发。</summary>
    public event Action? RaiseRequested;

    /// <summary>系统媒体控制要求退出时触发。</summary>
    public event Action? QuitRequested;

    public ObservableCollection<Music> Tracks { get; }
    public ObservableCollection<Playlist> Playlists { get; }
    public ObservableCollection<Music> RecentTracks { get; }
    public ObservableCollection<Music> PlaybackQueue {
        get => _playbackQueue;
        private set {
            if (ReferenceEquals(_playbackQueue, value)) return;
            _playbackQueue = value;
            OnPropertyChanged();
            ResetShuffleState();
            OnPropertyChanged(nameof(QueueCountLabel));
        }
    }
    public LocalizedText Text { get; }
    public MonetPalette Palette { get; private set; }

    /// <summary>
    /// 沉浸歌词流体背景用的封面色。没封面的歌就没位图，退回 UI 那套灰调。
    /// </summary>
    public string LyricsBackdropBase => CurrentTrack.ArtworkBackgroundColor ?? Palette.WindowBackground;
    public string LyricsBackdropAccent => CurrentTrack.ArtworkAccentColor ?? Palette.Accent;
    public string LyricsBackdropSecondary => CurrentTrack.ArtworkSecondaryColor ?? Palette.AccentSecondary;

    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenPlaylistCommand { get; }
    public RelayCommand OpenCategoryGroupCommand { get; }
    public RelayCommand PlayTrackCommand { get; }
    public RelayCommand AddTrackToPlaylistCommand { get; }
    public RelayCommand RemoveTrackFromPlaylistCommand { get; }
    public RelayCommand TogglePlaybackCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextTrackCommand { get; }
    public RelayCommand PreviousTrackCommand { get; }
    public RelayCommand ToggleShuffleCommand { get; }
    public RelayCommand CycleRepeatModeCommand { get; }
    public RelayCommand TogglePopoverCommand { get; }
    public RelayCommand ClosePopoverCommand { get; }
    public RelayCommand ToggleMoreSectionCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleFloatingLyricsCommand { get; }
    public RelayCommand CycleEqualizerCommand { get; }
    public RelayCommand SelectEqualizerCommand { get; }
    public RelayCommand SetAbLoopACommand { get; }
    public RelayCommand SetAbLoopBCommand { get; }
    public RelayCommand ClearAbLoopCommand { get; }
    public RelayCommand SelectSleepTimerCommand { get; }
    public RelayCommand OpenPlayerCommand { get; }
    public RelayCommand CreatePlaylistCommand { get; }
    public RelayCommand DeletePlaylistCommand { get; }
    public RelayCommand ExitLyricsCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand ToggleFullscreenCommand { get; }
    public RelayCommand SeekToLyricCommand { get; }

    public bool IsLyricsMode {
        get => _isLyricsMode;
        set {
            if (_isLyricsMode == value) return;
            _isLyricsMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNormalMode));
        }
    }

    public bool IsNormalMode => !IsLyricsMode;

    public bool IsSystemFullscreen {
        get => _isSystemFullscreen;
        set {
            if (_isSystemFullscreen == value) return;
            _isSystemFullscreen = value;
            OnPropertyChanged();
        }
    }

    public bool IsShuffleMode {
        get => _isShuffleMode;
        set {
            if (_isShuffleMode == value) return;
            _isShuffleMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackOrderLabel));
            ResetShuffleState();
            QueueStateSave();
            UpdateSystemMedia();
        }
    }

    public RepeatMode RepeatMode {
        get => _repeatMode;
        private set {
            if (_repeatMode == value) return;
            _repeatMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RepeatModeLabel));
            OnPropertyChanged(nameof(IsRepeatOne));
            OnPropertyChanged(nameof(IsRepeatAll));
            OnPropertyChanged(nameof(IsRepeatActive));
            QueueStateSave();
            UpdateSystemMedia();
        }
    }

    public bool IsRepeatOne => RepeatMode == RepeatMode.Track;
    public bool IsRepeatAll => RepeatMode == RepeatMode.Playlist;
    public bool IsRepeatActive => RepeatMode != RepeatMode.Off;

    public bool IsFloatingLyricsVisible {
        get => _isFloatingLyricsVisible;
        set {
            if (_isFloatingLyricsVisible == value) return;
            _isFloatingLyricsVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FloatingLyricsStateLabel));
            QueueStateSave();
        }
    }

    public string FloatingLyricsStateLabel => Text[IsFloatingLyricsVisible ? "StateOn" : "StateOff"];

    public string ActivePopover {
        get => _activePopover;
        set {
            if (_activePopover == value) return;
            _activePopover = value;
            if (value != "more") ExpandedMoreSection = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsQueuePopoverOpen));
            OnPropertyChanged(nameof(IsSoundPopoverOpen));
            OnPropertyChanged(nameof(IsMorePopoverOpen));
            OnPropertyChanged(nameof(HasActivePopover));
        }
    }

    public bool IsQueuePopoverOpen => ActivePopover == "queue";
    public bool IsSoundPopoverOpen => ActivePopover == "sound";
    public bool IsMorePopoverOpen => ActivePopover == "more";
    public bool HasActivePopover => !string.IsNullOrEmpty(ActivePopover);

    /// <summary>
    /// “更多播放选项”里当前展开的是哪一段。菜单是一列紧凑的行，
    /// 点哪行就在原地展开控件，不是一次全铺开。
    /// </summary>
    public string ExpandedMoreSection {
        get => _expandedMoreSection;
        private set {
            if (_expandedMoreSection == value) return;
            _expandedMoreSection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMoreEqualizerOpen));
            OnPropertyChanged(nameof(IsMoreSpeedOpen));
            OnPropertyChanged(nameof(IsMoreAbLoopOpen));
            OnPropertyChanged(nameof(IsMoreSleepTimerOpen));
        }
    }

    public bool IsMoreEqualizerOpen => ExpandedMoreSection == "equalizer";
    public bool IsMoreSpeedOpen => ExpandedMoreSection == "speed";
    public bool IsMoreAbLoopOpen => ExpandedMoreSection == "abloop";
    public bool IsMoreSleepTimerOpen => ExpandedMoreSection == "sleep";

    /// <summary>菜单行右边那个小值，比如 "Warm"、"1.25×"。</summary>
    public string EqualizerMenuValue => EqualizerPresetLabel;
    public string SpeedMenuValue => PlaybackSpeedLabel;
    public string AbLoopMenuValue => _abLoopState switch {
        1 => "A " + FormatSeconds(_abLoopStartSeconds ?? 0),
        2 => FormatSeconds(_abLoopStartSeconds ?? 0) + " – " + FormatSeconds(_abLoopEndSeconds ?? 0),
        _ => Text["StateOff"]
    };
    public string SleepTimerMenuValue => HasSleepTimer ? SleepTimerRemainingLabel : Text["SleepOff"];

    public bool IsCompactLayout {
        get => _isCompactLayout;
        set {
            if (_isCompactLayout == value) return;
            _isCompactLayout = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWideLayout));
            OnPropertyChanged(nameof(ConsoleMargin));
            OnPropertyChanged(nameof(PopoverMargin));
            OnPropertyChanged(nameof(StatusCardMargin));
        }
    }

    public bool IsWideLayout => !IsCompactLayout;

    /// <summary>
    /// 宽屏时控制台放在内容列下面，侧边栏的状态卡才能贴到最底。
    /// 窄屏退回全宽控制台，并在它上面给状态卡留出底部空间。
    /// </summary>
    public Thickness ConsoleMargin => IsWideLayout
        ? new Thickness(256, 0, 18, 10)
        : new Thickness(18, 0, 18, 10);

    public Thickness PopoverMargin => IsWideLayout
        ? new Thickness(256, 0, 18, 16)
        : new Thickness(18, 0, 18, 16);

    public Thickness StatusCardMargin => IsWideLayout
        ? new Thickness(2, 14, 2, 14)
        : new Thickness(2, 14, 2, 90);

    public double Volume {
        get => _volume;
        set {
            var nextValue = Math.Clamp(value, 0, 1);
            if (Math.Abs(_volume - nextValue) < 0.005) return;
            _volume = nextValue;
            if (nextValue > 0.005) _lastNonZeroVolume = nextValue;
            _playback.SetVolume(nextValue);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumePercentage));
            OnPropertyChanged(nameof(IsMuted));
            QueueStateSave();
            UpdateSystemMedia();
        }
    }

    public int VolumePercentage => (int)Math.Round(Volume * 100);
    public bool IsMuted => Volume <= 0.005;

    public int EqualizerPresetIndex {
        get => _equalizerPresetIndex;
        private set {
            var nextValue = Math.Clamp(value, 0, EqualizerPresetKeys.Length - 1);
            if (_equalizerPresetIndex == nextValue) return;
            _equalizerPresetIndex = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EqualizerPresetLabel));
            OnPropertyChanged(nameof(EqualizerMenuValue));
            SelectOption(EqualizerOptions, nextValue);
            UpdateEqualizerBands();
            ApplyEqualizer();
            QueueStateSave();
        }
    }

    public double PlaybackSpeed {
        get => _playbackSpeed;
        set {
            var nextValue = Math.Round(Math.Clamp(value, MinimumPlaybackSpeed, MaximumPlaybackSpeed), 2);
            if (Math.Abs(_playbackSpeed - nextValue) < 0.001) return;
            _playbackSpeed = nextValue;
            _playback.SetRate(nextValue);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackSpeedLabel));
            OnPropertyChanged(nameof(SpeedMenuValue));
            QueueStateSave();
            UpdateSystemMedia();
        }
    }

    public bool IsPitchSyncEnabled {
        get => _isPitchSyncEnabled;
        set {
            if (_isPitchSyncEnabled == value) return;
            _isPitchSyncEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PitchSyncStateLabel));
            _pitchApplyTimer.Stop();
            _playback.SetPitch(_pitchSemitones, value);
            SetStatus("StatusPitchSync", PitchSyncStateLabel);
            QueueStateSave();
        }
    }

    public double PitchSemitones {
        get => _pitchSemitones;
        set {
            var nextValue = Math.Round(Math.Clamp(value, -12, 12), 1);
            if (Math.Abs(_pitchSemitones - nextValue) < 0.01) return;
            _pitchSemitones = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PitchLabel));
            OnPropertyChanged(nameof(ShowPitchUnsupportedNote));
            _pitchApplyTimer.Stop();
            _pitchApplyTimer.Start();
            QueueStateSave();
        }
    }

    public int SleepTimerSelectionIndex {
        get => _sleepTimerSelectionIndex;
        set {
            var nextValue = Math.Clamp(value, 0, SleepTimerMinuteValues.Length - 1);
            if (_sleepTimerSelectionIndex == nextValue) return;
            _sleepTimerSelectionIndex = nextValue;
            var minutes = SleepTimerMinuteValues[nextValue];
            _sleepDeadlineUtc = minutes == 0 ? null : DateTime.UtcNow.AddMinutes(minutes);
            _lastSleepRemainingSecond = -1;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SleepTimerRemainingLabel));
            OnPropertyChanged(nameof(HasSleepTimer));
            OnPropertyChanged(nameof(SleepTimerMenuValue));
            foreach (var option in SleepTimerOptions) option.IsSelected = option.Index == nextValue;
            SetStatus(minutes == 0 ? "StatusSleepCancelled" : "StatusSleepSet", minutes);
        }
    }

    public string PlaybackSpeedLabel => $"{PlaybackSpeed:0.00}×";
    public string PitchLabel => Text.Format("PitchValue", PitchSemitones.ToString("+0.#;-0.#;0"));
    public string PitchSyncStateLabel => Text[IsPitchSyncEnabled ? "StateOn" : "StateOff"];
    public bool IsPitchShiftSupported => _playback.SupportsPitchShift;

    /// <summary>用户真去拖音调了再解释这个限制。</summary>
    public bool ShowPitchUnsupportedNote => !IsPitchShiftSupported && Math.Abs(PitchSemitones) > 0.01;
    public string EqualizerPresetLabel => Text[EqualizerPresetKeys[EqualizerPresetIndex]];
    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = [
        new("31", 0.5), new("125", 0.5), new("500", 0.5), new("2k", 0.5), new("8k", 0.5)
    ];
    public string PlaybackOrderLabel => Text[IsShuffleMode ? "PlaybackOrderShuffle" : "PlaybackOrderSequential"];
    public string RepeatModeLabel => Text[RepeatMode switch {
        RepeatMode.Track => "RepeatOne",
        RepeatMode.Playlist => "RepeatAll",
        _ => "RepeatOff"
    }];

    public string QueueCountLabel => Text.Format("QueueCount", PlaybackQueue.Count);
    public ObservableCollection<PlaybackOptionViewModel> SleepTimerOptions { get; } = [];
    public ObservableCollection<PlaybackOptionViewModel> EqualizerOptions { get; } = [];

    public bool HasSleepTimer => _sleepDeadlineUtc is not null;

    public string SleepTimerRemainingLabel {
        get {
            if (_sleepDeadlineUtc is not { } deadline) return Text["SleepOff"];
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return Text["SleepFinished"];
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Minutes}:{remaining.Seconds:00}";
        }
    }

    public bool IsAbLoopActive => _abLoopState == 2;
    public string AbLoopStatusLabel => _abLoopState switch {
        1 => Text["AbLoopSetA"],
        2 => Text.Format("AbLoopRange", FormatSeconds(_abLoopStartSeconds ?? 0), FormatSeconds(_abLoopEndSeconds ?? 0)),
        _ => Text["AbLoopOff"]
    };
    public double AbLoopStartFraction => _abLoopStartSeconds is { } start ? Math.Clamp(start / CurrentDurationSeconds, 0, 1) : -1;
    public double AbLoopEndFraction => _abLoopEndSeconds is { } end ? Math.Clamp(end / CurrentDurationSeconds, 0, 1) : -1;

    private static readonly string[] EqualizerPresetKeys = ["EqualizerFlat", "EqualizerWarm", "EqualizerVocal", "EqualizerBass"];

    public PageViewModel CurrentPage {
        get => _currentPage;
        private set {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged();
        }
    }

    public bool HasCurrentTrack => !ReferenceEquals(_currentTrack, _emptyTrack);

    public Music CurrentTrack {
        get => _currentTrack;
        private set {
            if (_currentTrack == value) return;
            _currentTrack.IsCurrent = false;
            _currentTrack = value;
            _currentTrack.IsCurrent = HasCurrentTrack;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCurrentTrack));
            OnPropertyChanged(nameof(IsTransportPlaying));
            OnPropertyChanged(nameof(CurrentDurationSeconds));
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(LocalQualityLabel));
            OnPropertyChanged(nameof(NoLyricsAvailable));
            Palette = MonetPaletteService.FromColorSeed(value.ArtworkColor);
            OnPropertyChanged(nameof(Palette));
            OnPropertyChanged(nameof(LyricsBackdropBase));
            OnPropertyChanged(nameof(LyricsBackdropAccent));
            OnPropertyChanged(nameof(LyricsBackdropSecondary));
            _lastCurrentLyricIndex = int.MinValue;
            // 换歌一定要丢掉上一首的索引缓存，否则新歌的前几十毫秒还会套用旧位置算出来的行。
            _currentLyricIndex = -1;
            _currentLyricIndexPosition = double.NaN;
            UpdateCurrentLyric();
            OnPropertyChanged(nameof(CurrentLyricIndex));
            OnPropertyChanged(nameof(CurrentLyric));
            OnPropertyChanged(nameof(CurrentLyricText));
            OnPropertyChanged(nameof(CurrentLyricTranslation));
            OnPropertyChanged(nameof(AbLoopStatusLabel));
            OnPropertyChanged(nameof(AbLoopStartFraction));
            OnPropertyChanged(nameof(AbLoopEndFraction));
            _ = TryLoadSidecarForCurrentTrackAsync(value);
            UpdateSystemMedia();
        }
    }

    /// <summary>
    /// 后台扫文件夹，把音频追加到歌单。同目录封面和歌词 sidecar 也一起挂上；
    /// 只有一首歌的文件夹里，歌词文件名跟音频不一样也能认。
    /// </summary>
    public async Task ImportFolderToPlaylistAsync(
        Playlist playlist,
        string folderPath,
        CancellationToken cancellationToken = default) {
        var folderName = GetFolderDisplayName(folderPath);
        SetStatus("StatusScanningFolder", folderName);

        try {
            var root = NormalizeFolderRoot(folderPath);
            if (root is null) {
                SetStatus("StatusFolderError", folderPath);
                return;
            }

            if (!playlist.FolderRoots.Contains(root, PathComparer)) {
                playlist.FolderRoots.Add(root);
            }

            var (added, removed, skipped) = await ScanIntoPlaylistAsync(playlist, root, cancellationToken);

            if (CurrentPage is PlaylistDetailPageViewModel detail && ReferenceEquals(detail.Playlist, playlist)) {
                detail.NotifyTracksChanged();
            }

            if (ReferenceEquals(SelectedPlaylist, playlist)) PlaybackQueue = playlist.Tracks;

            if (!HasCurrentTrack && Tracks.Count > 0) {
                // 先选中导入的第一首，控制台和传输控件才有目标。
                // 跟恢复时一个做法，不会自己开始放。
                ApplyPlaybackPosition(TimeSpan.Zero);
                CurrentTrack = Tracks[0];
                ResetShuffleState();
            }

            ReconcileCurrentTrack();

            OnPropertyChanged(nameof(QueueCountLabel));
            SyncFolderWatch();

            if (added == 0 && removed == 0) {
                SetStatus("StatusFolderEmpty", folderName);
            }
            else {
                SetStatus("StatusFolderImported", added, folderName, skipped);
            }

            QueueStateSave();
        }
        catch (OperationCanceledException) {
            SetStatus("StatusFolderCancelled");
        }
        catch (Exception exception) {
            SetStatus("StatusFolderError", exception.Message);
        }
    }

    /// <summary>
    /// 扫一遍根目录，再把结果同步进这个歌单。多个歌单共用一个根目录时，
    /// 用 <see cref="ScanRootAsync"/> 扫一次、再各自调用 <see cref="ApplyScanToPlaylist"/>。
    /// </summary>
    private async Task<(int Added, int Removed, int Skipped)> ScanIntoPlaylistAsync(
        Playlist playlist,
        string root,
        CancellationToken cancellationToken) {
        var result = await ScanRootAsync(root, cancellationToken);
        return ApplyScanToPlaylist(playlist, root, result);
    }

    private Task<LocalFolderScanResult> ScanRootAsync(string root, CancellationToken cancellationToken) {
        var labels = new LocalFolderScanLabels(
            Text["UnknownArtist"],
            Text["UnknownAlbum"],
            Text["UnknownGenre"]);
        return Task.Run(() => _folderScanner.ScanAsync(root, labels, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 把一次扫描结果同步进歌单：新文件加进来，磁盘上没了的移除。
    /// 只动这个根目录带来的条目，用户手加的歌和手删的记录都不受影响。
    /// </summary>
    private (int Added, int Removed, int Skipped) ApplyScanToPlaylist(
        Playlist playlist,
        string root,
        LocalFolderScanResult result) {
        var comparer = PathComparer;

        // 磁盘上已经没了的：从歌单和曲库移除。留着只会变成点了报错的死条目。
        var removed = RemoveMissingUnderRoot(playlist, root, comparer);

        var libraryPaths = new HashSet<string>(
            Tracks.Select(track => track.FilePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            comparer);
        var playlistPaths = new HashSet<string>(
            playlist.Tracks.Select(track => track.FilePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            comparer);

        var added = 0;
        var skipped = result.SkippedFileCount;
        foreach (var track in result.Tracks) {
            var path = track.FilePath;
            if (string.IsNullOrWhiteSpace(path)) continue;

            // 手删过的不加回；文件真被换过就当作新歌重新放进来。
            // 这个判断必须放在最前面：歌通常还在曲库里，走"首次入库"分支就绕过去了。
            if (IsDismissed(path)) {
                skipped++;
                continue;
            }

            if (!libraryPaths.Add(path)) {
                // 曲库里已经有了（可能来自别的文件夹），只把它补进这个歌单。
                if (!playlistPaths.Add(path)) {
                    skipped++;
                    continue;
                }

                var known = Tracks.FirstOrDefault(item => comparer.Equals(item.FilePath, path));
                if (known is null) {
                    skipped++;
                    continue;
                }

                playlist.Tracks.Add(known);
                added++;
                continue;
            }

            Tracks.Add(track);
            if (playlistPaths.Add(path)) {
                playlist.Tracks.Add(track);
                added++;
            }
            else {
                skipped++;
            }
        }

        return (added, removed, skipped);
    }

    /// <summary>
    /// 删掉歌单里那些"属于这个根目录、但磁盘上已经没了"的条目。
    ///
    /// 只按 <see cref="File.Exists"/> 判断，不看这次扫描结果：扫不到的也可能是这一轮读标签失败，
    /// 那种文件还在磁盘上，不该被当成删除。
    /// </summary>
    private int RemoveMissingUnderRoot(Playlist playlist, string root, StringComparer comparer) {
        var removed = 0;
        for (var index = playlist.Tracks.Count - 1; index >= 0; index--) {
            var track = playlist.Tracks[index];
            var path = track.FilePath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!IsUnderRoot(path, root)) continue;
            if (File.Exists(path)) continue;

            playlist.Tracks.RemoveAt(index);
            _dismissedPaths.Remove(path);
            RemoveFromLibraryIfUnused(path, comparer);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// 曲库里没有任何歌单还引用、而且不是当前播放的歌，就从曲库移除。
    /// 这样删掉的文件不会在"音乐库"里留一堆灰条。
    /// </summary>
    private void RemoveFromLibraryIfUnused(string path, StringComparer comparer) {
        var stillUsed = Playlists.Any(playlist =>
            playlist.Tracks.Any(track => comparer.Equals(track.FilePath, path)));
        if (stillUsed) return;

        // 正在放的那首也照删：文件已经没了，列表里留着只会点一下报错。
        // 播放本身不受影响，libvlc 还握着已经打开的句柄，切歌时自然就离开了。
        for (var index = Tracks.Count - 1; index >= 0; index--) {
            if (!comparer.Equals(Tracks[index].FilePath, path)) continue;
            ForgetShuffleState(Tracks[index]);
            Tracks.RemoveAt(index);
        }
    }

    /// <summary>把已经不在磁盘上的歌从随机播放的历史里摘掉，免得"上一首"跳回死条目。</summary>
    private void ForgetShuffleState(Music track) {
        _shufflePlayed.Remove(track);
        if (!_shuffleHistory.Contains(track)) return;

        // Stack 只能从顶上弹，要删中间的一项就得按原顺序重建一遍。
        // 枚举是栈顶优先，反转后就是从底往上，按这个顺序压回去顺序不变。
        var kept = _shuffleHistory.Reverse().Where(item => !ReferenceEquals(item, track)).ToList();
        _shuffleHistory.Clear();
        foreach (var item in kept) _shuffleHistory.Push(item);
    }

    /// <summary>
    /// 当前曲目被删掉之后收个尾：正在放就让它自然播完，没在放就换个目标，
    /// 否则控制台会停在一个已经不存在的条目上。
    /// </summary>
    private void ReconcileCurrentTrack() {
        if (ReferenceEquals(CurrentTrack, _emptyTrack)) return;
        if (IsPlaying) return;

        var path = CurrentTrack.FilePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return;

        var replacement = PlaybackQueue.FirstOrDefault() ?? Tracks.FirstOrDefault();
        ApplyPlaybackPosition(TimeSpan.Zero);
        CurrentTrack = replacement ?? _emptyTrack;
        AbLoopReset();
    }

    private static bool IsUnderRoot(string path, string root) {
        try {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
                   || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
        }
        catch (Exception) {
            return false;
        }
    }

    private static string? NormalizeFolderRoot(string folderPath) {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        try {
            if (!Path.IsPathFullyQualified(folderPath)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>路径比较方式跟持久化那层保持一致：Windows 不区分大小写。</summary>
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? System.StringComparer.OrdinalIgnoreCase
        : System.StringComparer.Ordinal;

    private bool IsDismissed(string path) {
        if (!_dismissedPaths.TryGetValue(path, out var remembered)) return false;

        var current = FileFingerprint(path);
        if (current is null) {
            // 文件暂时读不到（网络盘掉线之类）：保守起见还是不加回。
            return true;
        }

        // 老记录没有指纹：补上当前值，别让下一次重扫就把它当成换过的文件。
        if (string.IsNullOrEmpty(remembered)) {
            _dismissedPaths[path] = current;
            return true;
        }

        if (string.Equals(current, remembered, StringComparison.Ordinal)) return true;

        // 文件确实被换过了，手删记录失效，允许它重新出现。
        _dismissedPaths.Remove(path);
        return false;
    }

    /// <summary>
    /// 文件指纹：大小 + 最后写入时间。只用来判断"还是不是原来那首歌"，
    /// 不读内容所以很便宜；读不到就返回 null。
    /// </summary>
    private static string? FileFingerprint(string path) {
        try {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// 让监听服务盯住所有歌单的文件夹根目录。导入、删除歌单、恢复之后都要调一次。
    /// </summary>
    private void SyncFolderWatch() {
        if (_isRestoringState || _disposed) return;
        _services.FolderWatch.Sync(AllFolderRoots());
    }

    private IEnumerable<string> AllFolderRoots() {
        var seen = new HashSet<string>(PathComparer);
        foreach (var playlist in Playlists) {
            foreach (var root in playlist.FolderRoots) {
                if (!string.IsNullOrWhiteSpace(root) && seen.Add(root)) yield return root;
            }
        }
    }

    /// <summary>文件夹有了变化：重扫那个根目录，多个歌单共用同一个根目录时各扫各的。</summary>
    private void FolderWatch_Changed(string rootFolder) {
        if (_disposed) return;
        Dispatcher.UIThread.Post(async () => await RefreshFolderAsync(rootFolder));
    }

    /// <summary>
    /// 重扫一个根目录并同步到所有引用它的歌单。后台线程不会碰 ObservableCollection，
    /// 扫描完再回 UI 线程改列表。
    /// </summary>
    public async Task RefreshFolderAsync(string rootFolder, CancellationToken cancellationToken = default) {
        if (_disposed) return;
        var root = NormalizeFolderRoot(rootFolder);
        if (root is null) return;

        var targets = Playlists
            .Where(playlist => playlist.FolderRoots.Contains(root, PathComparer))
            .ToList();
        if (targets.Count == 0) return;

        // 同一个根目录正在扫就别叠加，等这一轮结束。
        if (!_refreshingRoots.Add(root)) return;
        try {
            // 一个根目录只扫一次；多个歌单共用它时把同一份结果各套一遍。
            var result = await ScanRootAsync(root, cancellationToken);

            var added = 0;
            var removed = 0;
            var skipped = 0;
            foreach (var playlist in targets) {
                cancellationToken.ThrowIfCancellationRequested();
                var (playlistAdded, playlistRemoved, playlistSkipped) =
                    ApplyScanToPlaylist(playlist, root, result);
                added += playlistAdded;
                removed += playlistRemoved;
                skipped += playlistSkipped;
            }

            if (added == 0 && removed == 0) return;

            if (CurrentPage is PlaylistDetailPageViewModel detail && targets.Contains(detail.Playlist)) {
                detail.NotifyTracksChanged();
            }

            var selectedAffected = SelectedPlaylist is { } selected && targets.Contains(selected);
            if (selectedAffected) PlaybackQueue = SelectedPlaylist!.Tracks;

            if (!HasCurrentTrack && Tracks.Count > 0) {
                ApplyPlaybackPosition(TimeSpan.Zero);
                CurrentTrack = Tracks[0];
                ResetShuffleState();
            }

            ReconcileCurrentTrack();

            OnPropertyChanged(nameof(QueueCountLabel));
            SetStatus("StatusFolderRefreshed", added, GetFolderDisplayName(root), removed);
            QueueStateSave();
        }
        catch (OperationCanceledException) {
            // 退出时取消，不用报。
        }
        catch (Exception exception) {
            SetStatus("StatusFolderError", exception.Message);
        }
        finally {
            _refreshingRoots.Remove(root);
        }
    }

    private static string GetFolderDisplayName(string folderPath) {
        var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }

    public async Task LoadLyricsFromFileAsync(string path) {
        if (!HasCurrentTrack) {
            SetStatus("StatusNoTrack");
            return;
        }

        try {
            var track = CurrentTrack;
            var document = await _lyricsService.LoadAsync(path, track.Duration);
            if (!ReferenceEquals(track, CurrentTrack)) return;
            // 存完整路径，手动选的歌词重启后还在；状态提示里只显示文件名。
            ApplyLyrics(document, path, manual: true);
            QueueStateSave();
            ActivePopover = string.Empty;
        }
        catch (LyricsParseException exception) {
            SetStatus("StatusLyricsError", exception.Message);
        }
        catch (Exception exception) {
            SetStatus("StatusLyricsError", exception.Message);
        }
    }

    /// <summary>把解析好的歌词挂到当前歌曲上，刷新所有歌词绑定。</summary>
    public void ApplyLyrics(LyricsDocument document, string? sourcePath = null, bool manual = false) {
        CurrentTrack.Lyrics = document.Lines;
        CurrentTrack.LyricsFormat = document.Format;
        CurrentTrack.LyricsSourcePath = sourcePath;
        CurrentTrack.HasManualLyrics = manual;

        _lastCurrentLyricIndex = int.MinValue;
        _currentLyricIndex = -1;
        _currentLyricIndexPosition = double.NaN;
        UpdateCurrentLyric();
        OnPropertyChanged(nameof(NoLyricsAvailable));
        OnPropertyChanged(nameof(CurrentLyric));
        OnPropertyChanged(nameof(CurrentLyricText));
        OnPropertyChanged(nameof(CurrentLyricTranslation));
        OnPropertyChanged(nameof(HasCurrentLyricTranslation));

        var sourceLabel = sourcePath is not null ? Path.GetFileName(sourcePath) : document.Format.ToString().ToUpperInvariant();
        SetStatus("StatusLyricsLoaded", sourceLabel,
            document.HasWordTiming ? Text["LyricsWordLevel"] : Text["LyricsLineLevel"]);
    }

    /// <summary>
    /// sidecar 加载故意保守：只给没有手动歌词、也还没有歌词的歌跑。
    /// 导入的或者本来就有的歌词，不会背着用户被换掉。
    /// </summary>
    private async Task TryLoadSidecarForCurrentTrackAsync(Music track) {
        if (track.HasManualLyrics || track.Lyrics.Count > 0) return;

        try {
            var document = await _lyricsService.TryLoadSidecarAsync(track.FilePath, track.Duration);
            if (document is not null && ReferenceEquals(track, CurrentTrack) && track.Lyrics.Count == 0) {
                ApplyLyrics(document, Path.GetFileName(track.FilePath));
            }
        }
        catch (LyricsParseException exception) {
            if (ReferenceEquals(track, CurrentTrack)) SetStatus("StatusLyricsError", exception.Message);
        }
        catch (Exception exception) {
            if (ReferenceEquals(track, CurrentTrack)) SetStatus("StatusLyricsError", exception.Message);
        }
    }

    public Playlist? SelectedPlaylist {
        get => _selectedPlaylist;
        private set {
            if (_selectedPlaylist == value) return;
            _selectedPlaylist = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaying {
        get => _isPlaying;
        set {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTransportPlaying));
            UpdateSystemMedia();
        }
    }

    /// <summary>没有当前歌曲时为 false，空控制台显示播放图标。</summary>
    public bool IsTransportPlaying => HasCurrentTrack && IsPlaying;

    public double CurrentDurationSeconds => Math.Max(1, CurrentTrack.Duration.TotalSeconds);
    public bool NoLyricsAvailable => CurrentTrack.Lyrics.Count == 0;
    public double AmbientPhase => _ambientPhase;
    public LyricLine? CurrentLyric => CurrentLyricIndex >= 0 ? CurrentTrack.Lyrics[CurrentLyricIndex] : null;
    public string CurrentLyricText => CurrentLyricIndex >= 0 ? CurrentTrack.Lyrics[CurrentLyricIndex].Text : string.Empty;
    public string CurrentLyricTranslation => CurrentLyricIndex >= 0 ? CurrentTrack.Lyrics[CurrentLyricIndex].Translation ?? string.Empty : string.Empty;
    public bool HasCurrentLyricTranslation => !string.IsNullOrWhiteSpace(CurrentLyricTranslation);
    public string CurrentTimeLabel => !HasCurrentTrack
        ? "0:00"
        : TimeSpan.FromSeconds(
            _isSeeking ? _seekPositionSeconds : _pendingSeekTargetSeconds ?? _currentPositionSeconds).ToString(@"m\:ss");
    public string DurationLabel => HasCurrentTrack ? CurrentTrack.Duration.ToString(@"m\:ss") : "0:00";
    public string LocalQualityLabel => HasCurrentTrack ? CurrentTrack.LocalQualityLabel : string.Empty;

    public double CurrentPositionSeconds {
        get => _currentPositionSeconds;
        set {
            if (!HasCurrentTrack) {
                _pendingSeekTargetSeconds = null;
                SetPosition(0, seek: false);
                return;
            }

            var target = Math.Clamp(value, 0, CurrentDurationSeconds);
            if (_playback.IsLoaded && Math.Abs(target - _currentPositionSeconds) >= 0.001) {
                _pendingSeekTargetSeconds = target;
                _pendingSeekUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
            }
            else if (!_playback.IsLoaded) {
                _pendingSeekTargetSeconds = null;
            }

            SetPosition(target, seek: true);
        }
    }

    /// <summary>
    /// 进度条显示的位置。指针按着时直接更新，松手才提交给后端，
    /// 播放时钟没法把滑块拽走。
    /// </summary>
    public double SeekPositionSeconds {
        get => _seekPositionSeconds;
        set {
            if (!HasCurrentTrack) {
                if (Math.Abs(_seekPositionSeconds) < 0.001) return;
                _seekPositionSeconds = 0;
                OnPropertyChanged();
                return;
            }

            var nextValue = Math.Clamp(value, 0, CurrentDurationSeconds);
            if (Math.Abs(_seekPositionSeconds - nextValue) < 0.001) return;
            _seekPositionSeconds = nextValue;
            OnPropertyChanged();
            if (_isSeeking) OnPropertyChanged(nameof(CurrentTimeLabel));
        }
    }

    public bool IsSeeking => _isSeeking;

    public void BeginSeek() {
        if (!HasCurrentTrack || _isSeeking) return;
        _isSeeking = true;
        OnPropertyChanged(nameof(IsSeeking));
        OnPropertyChanged(nameof(CurrentTimeLabel));
    }

    public void CommitSeek(double seconds) {
        if (!HasCurrentTrack) {
            _isSeeking = false;
            _pendingSeekTargetSeconds = null;
            if (Math.Abs(_seekPositionSeconds) > 0.001) {
                _seekPositionSeconds = 0;
                OnPropertyChanged(nameof(SeekPositionSeconds));
            }
            OnPropertyChanged(nameof(IsSeeking));
            OnPropertyChanged(nameof(CurrentTimeLabel));
            return;
        }

        var target = Math.Clamp(seconds, 0, CurrentDurationSeconds);
        _isSeeking = false;
        _seekPositionSeconds = target;
        OnPropertyChanged(nameof(IsSeeking));
        OnPropertyChanged(nameof(SeekPositionSeconds));

        if (Math.Abs(target - _currentPositionSeconds) < 0.001) {
            _pendingSeekTargetSeconds = null;
            OnPropertyChanged(nameof(CurrentTimeLabel));
            return;
        }

        _pendingSeekTargetSeconds = _playback.IsLoaded ? target : null;
        _pendingSeekUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
        SetPosition(target, seek: true);
        OnPropertyChanged(nameof(CurrentTimeLabel));
    }

    private void SetPosition(double seconds, bool seek) {
        var nextValue = Math.Clamp(seconds, 0, CurrentDurationSeconds);
        if (Math.Abs(_currentPositionSeconds - nextValue) < 0.001) return;
        _currentPositionSeconds = nextValue;
        if (!_isSeeking) {
            _seekPositionSeconds = nextValue;
            OnPropertyChanged(nameof(SeekPositionSeconds));
        }
        // 这个方法不是属性 setter，CallerMemberName 会报 "SetPosition"，
        // 进度条这类绑定就永远看不到播放时钟在走。
        OnPropertyChanged(nameof(CurrentPositionSeconds));
        OnPropertyChanged(nameof(CurrentTimeLabel));
        UpdateCurrentLyric();
        if (seek && !_suppressSeek && _playback.IsLoaded) {
            _playback.Seek(TimeSpan.FromSeconds(nextValue));
        }
        if (seek) UpdateSystemMedia();
    }

    private void ApplyPlaybackPosition(TimeSpan position) {
        _suppressSeek = true;
        try {
            SetPosition(position.TotalSeconds, seek: false);
        }
        finally {
            _suppressSeek = false;
        }
    }

    public string NewPlaylistName {
        get => _newPlaylistName;
        set {
            if (_newPlaylistName == value) return;
            _newPlaylistName = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage => Text.Format(_statusKey, _statusArguments);

    public string SelectedNavigation {
        get => _selectedNavigation;
        private set {
            if (_selectedNavigation == value) return;
            _selectedNavigation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLibrarySelected));
            OnPropertyChanged(nameof(IsArtistsSelected));
            OnPropertyChanged(nameof(IsAlbumsSelected));
            OnPropertyChanged(nameof(IsGenresSelected));
            OnPropertyChanged(nameof(IsPlaylistsSelected));
            OnPropertyChanged(nameof(IsRecentSelected));
        }
    }

    public bool IsLibrarySelected => SelectedNavigation == "library";
    public bool IsArtistsSelected => SelectedNavigation == "artists";
    public bool IsAlbumsSelected => SelectedNavigation == "albums";
    public bool IsGenresSelected => SelectedNavigation == "genres";
    public bool IsPlaylistsSelected => SelectedNavigation == "playlists";
    public bool IsRecentSelected => SelectedNavigation == "recent";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Navigate(string destination) {
        _openedCategoryGroup = null;
        SelectedNavigation = destination;
        //分类逻辑
        CurrentPage = destination switch {
            "artists" => CreateCategoryPage(Text["ArtistsTitle"], Text["ArtistsSubtitle"], track => track.Artist),
            "albums" => CreateCategoryPage(Text["AlbumsTitle"], Text["AlbumsSubtitle"], track => track.Album),
            "genres" => CreateCategoryPage(Text["GenresTitle"], Text["GenresSubtitle"], track => track.Genre),
            "playlists" => new PlaylistsPageViewModel(Playlists),
            "recent" => new TrackCollectionPageViewModel(Text["RecentlyTitle"], Text["RecentlySubtitle"], RecentTracks.ToArray()),
            _ => new LibraryPageViewModel(Tracks)
        };
    }

    /// <summary>
    /// 按某个字段把曲库分组，拼出一个分类浏览页。
    /// </summary>
    /// <param name="title">页面标题。</param>
    /// <param name="subtitle">页面副标题。</param>
    /// <param name="selector">从歌曲里取分类键，比如歌手名或专辑名。</param>
    private CategoryBrowsePageViewModel CreateCategoryPage(
        string title,
        string subtitle,
        Func<Music, string> selector) {
        var groups = Tracks
            .GroupBy(selector)
            .Select(group => new CategoryGroupViewModel(
                group.Key,
                Text.Format(group.Count() == 1 ? "SongCountSingular" : "SongCount", group.Count()),
                group.First().ArtworkColor,
                group.ToArray()))
            .ToList();
        return new CategoryBrowsePageViewModel(title, subtitle,
            new ObservableCollection<CategoryGroupViewModel>(groups));
    }

    private void Text_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != "Item[]") return;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CurrentTimeLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(EqualizerPresetLabel));
        OnPropertyChanged(nameof(PlaybackOrderLabel));
        OnPropertyChanged(nameof(RepeatModeLabel));
        OnPropertyChanged(nameof(QueueCountLabel));
        OnPropertyChanged(nameof(SleepTimerRemainingLabel));
        OnPropertyChanged(nameof(AbLoopStatusLabel));
        OnPropertyChanged(nameof(EqualizerMenuValue));
        OnPropertyChanged(nameof(SpeedMenuValue));
        OnPropertyChanged(nameof(AbLoopMenuValue));
        OnPropertyChanged(nameof(SleepTimerMenuValue));
        OnPropertyChanged(nameof(CurrentLyric));
        OnPropertyChanged(nameof(CurrentLyricText));
        OnPropertyChanged(nameof(CurrentLyricTranslation));
        OnPropertyChanged(nameof(FloatingLyricsStateLabel));
        OnPropertyChanged(nameof(PitchSyncStateLabel));
        OnPropertyChanged(nameof(PitchLabel));
        RefreshPlaybackOptions();

        if (CurrentPage is PlaylistDetailPageViewModel detail) {
            CurrentPage = new PlaylistDetailPageViewModel(detail.Playlist, Tracks);
        }
        else if (_openedCategoryGroup is not null) {
            var count = _openedCategoryGroup.Tracks.Count;
            CurrentPage = new TrackCollectionPageViewModel(
                _openedCategoryGroup.Title,
                Text.Format(count == 1 ? "SongCountSingular" : "SongCount", count),
                _openedCategoryGroup.Tracks);
        }
        else {
            Navigate(_selectedNavigation);
        }
    }

    private void OpenPlaylist(Playlist playlist) {
        _openedCategoryGroup = null;
        SelectedPlaylist = playlist;
        SelectedNavigation = "playlists";
        CurrentPage = new PlaylistDetailPageViewModel(playlist, Tracks);
        if (playlist.Tracks.Count > 0) PlaybackQueue = playlist.Tracks;
    }

    private void PlayTrack(Music track, ObservableCollection<Music>? queue = null) {
        if (queue is not null) PlaybackQueue = queue;
        // 先把位置归零再换歌：CurrentTrack 的 setter 会按当前时钟重算歌词行，
        // 拿上一首的位置去算新歌的话，切歌瞬间会先跳到某一行的中间。
        ApplyPlaybackPosition(TimeSpan.Zero);
        CurrentTrack = track;
        _pendingSeekTargetSeconds = null;
        _isSeeking = false;
        AbLoopReset();
        if (!StartTrack(track, TimeSpan.Zero, autoplay: true)) return;

        RecentTracks.Remove(track);
        RecentTracks.Insert(0, track);
        while (RecentTracks.Count > 12) RecentTracks.RemoveAt(RecentTracks.Count - 1);
    }

    private bool StartTrack(Music track, TimeSpan startAt, bool autoplay) {
        if (!HasCurrentTrack) return false;
        if (track.IsLocalFile && !track.HasPlayableFile) {
            _playback.Stop();
            IsPlaying = false;
            ApplyPlaybackPosition(TimeSpan.Zero);
            SetStatus("StatusFileMissing", track.FilePath);
            QueueStateSave();
            return false;
        }

        var result = _playback.Start(ToAudioSource(track), startAt, autoplay);
        if (!result.Success) {
            IsPlaying = false;
            SetStatus(track.IsLocalFile ? "StatusPlaybackUnavailable" : "StatusPlaybackFailed",
                result.Error ?? string.Empty);
            QueueStateSave();
            return false;
        }

        if (startAt > TimeSpan.Zero) {
            // start seek 落下去之前，后端会先报一会儿 0。UI 先停在请求的位置，
            // 别一下弹回开头。
            _pendingSeekTargetSeconds = startAt.TotalSeconds;
            _pendingSeekUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
        }
        else {
            _pendingSeekTargetSeconds = null;
        }

        ApplyPlaybackPosition(startAt);
        IsPlaying = autoplay;
        if (autoplay) {
            SetStatus(track.IsLocalFile ? "StatusPlaying" : "StatusPlayingDemo", track.Title);
        }
        QueueStateSave();
        return true;
    }

    private static AudioSource ToAudioSource(Music track) =>
        new(track.FilePath, track.Duration, track.IsLocalFile);

    private void TogglePlayback() {
        if (!HasCurrentTrack) {
            SetStatus("StatusNoTrack");
            return;
        }

        if (_playback.IsLoaded) {
            if (IsPlaying) {
                _playback.Pause();
                IsPlaying = false;
                SetStatus("StatusPaused");
            }
            else {
                if (CurrentTrack.IsLocalFile && !CurrentTrack.HasPlayableFile) {
                    SetStatus("StatusFileMissing", CurrentTrack.FilePath);
                    return;
                }

                _playback.Play();
                IsPlaying = true;
                SetStatus(CurrentTrack.IsLocalFile ? "StatusPlaying" : "StatusPlayingDemo", CurrentTrack.Title);
            }

            QueueStateSave();
            return;
        }

        var startAt = _currentPositionSeconds >= CurrentDurationSeconds - 0.5
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_currentPositionSeconds);
        StartTrack(CurrentTrack, startAt, autoplay: true);
    }

    private void MoveTrack(int direction) {
        if (PlaybackQueue.Count == 0) return;
        if (direction > 0 && IsShuffleMode) {
            PlayNextShuffle();
            return;
        }

        if (direction < 0 && IsShuffleMode && _shuffleHistory.Count > 0) {
            var previous = _shuffleHistory.Pop();
            ApplyPlaybackPosition(TimeSpan.Zero);
            CurrentTrack = previous;
            AbLoopReset();
            StartTrack(previous, TimeSpan.Zero, autoplay: true);
            return;
        }

        var currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex < 0) currentIndex = 0;
        var nextIndex = (currentIndex + direction + PlaybackQueue.Count) % PlaybackQueue.Count;
        PlayTrack(PlaybackQueue[nextIndex]);
    }

    private void AdvanceAfterTrackEnd() {
        switch (RepeatMode) {
            case RepeatMode.Track:
                RestartCurrentTrack();
                return;
            case RepeatMode.Off when IsLastSequentialTrack():
                _playback.Stop();
                IsPlaying = false;
                ApplyPlaybackPosition(TimeSpan.FromSeconds(CurrentDurationSeconds));
                SetStatus("StatusQueueEnded");
                QueueStateSave();
                return;
            default:
                MoveTrack(1);
                return;
        }
    }

    private bool IsLastSequentialTrack() => !IsShuffleMode && PlaybackQueue.IndexOf(CurrentTrack) == PlaybackQueue.Count - 1;

    private void PlayNextShuffle() {
        if (PlaybackQueue.Count <= 1) {
            if (RepeatMode == RepeatMode.Off) {
                _playback.Stop();
                IsPlaying = false;
                SetStatus("StatusQueueEnded");
                QueueStateSave();
            }
            else {
                RestartCurrentTrack();
            }
            return;
        }

        var unplayed = PlaybackQueue.Where(track => !_shufflePlayed.Contains(track) && !ReferenceEquals(track, CurrentTrack)).ToList();
        if (unplayed.Count == 0) {
            // 随机且不循环时，全部听过一遍就停。
            if (RepeatMode == RepeatMode.Off) {
                _playback.Stop();
                IsPlaying = false;
                SetStatus("StatusQueueEnded");
                QueueStateSave();
                return;
            }
            _shufflePlayed.Clear();
            unplayed = PlaybackQueue.Where(track => !ReferenceEquals(track, CurrentTrack)).ToList();
        }

        if (unplayed.Count == 0) {
            RestartCurrentTrack();
            return;
        }

        var next = unplayed[_random.Next(unplayed.Count)];
        _shufflePlayed.Add(CurrentTrack);
        _shuffleHistory.Push(CurrentTrack);
        ApplyPlaybackPosition(TimeSpan.Zero);
        CurrentTrack = next;
        AbLoopReset();
        StartTrack(next, TimeSpan.Zero, autoplay: true);
    }

    private void RestartCurrentTrack() {
        if (!HasCurrentTrack) return;
        ApplyPlaybackPosition(TimeSpan.Zero);
        if (_playback.IsLoaded) {
            _pendingSeekTargetSeconds = 0;
            _pendingSeekUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
            _playback.Seek(TimeSpan.Zero);
            _playback.Play();
            IsPlaying = true;
            QueueStateSave();
            return;
        }

        StartTrack(CurrentTrack, TimeSpan.Zero, autoplay: true);
    }

    private void ResetShuffleState() {
        _shuffleHistory.Clear();
        _shufflePlayed.Clear();
        if (PlaybackQueue.Count > 0 && HasCurrentTrack) _shufflePlayed.Add(CurrentTrack);
    }

    private void CycleRepeatMode() {
        RepeatMode = RepeatMode switch {
            RepeatMode.Off => RepeatMode.Playlist,
            RepeatMode.Playlist => RepeatMode.Track,
            _ => RepeatMode.Off
        };
        SetStatus("StatusRepeatMode", RepeatModeLabel);
    }

    private void TogglePopover(string popover) {
        ActivePopover = ActivePopover == popover ? string.Empty : popover;
    }

    private void ToggleMoreSection(string section) {
        ExpandedMoreSection = ExpandedMoreSection == section ? string.Empty : section;
    }

    public void ClosePopoverIfAny() => ActivePopover = string.Empty;

    private void ToggleMute() {
        if (IsMuted) Volume = _lastNonZeroVolume;
        else Volume = 0;
    }

    private void CycleEqualizer() {
        EqualizerPresetIndex = (EqualizerPresetIndex + 1) % EqualizerPresetKeys.Length;
        SetStatus("StatusEqualizer", EqualizerPresetLabel);
    }

    private void UpdateEqualizerBands() {
        double[] levels = EqualizerPresetIndex switch {
            1 => [0.62, 0.58, 0.50, 0.44, 0.38],
            2 => [0.32, 0.44, 0.66, 0.60, 0.42],
            3 => [0.78, 0.68, 0.48, 0.36, 0.30],
            _ => [0.50, 0.50, 0.50, 0.50, 0.50]
        };
        for (var i = 0; i < EqualizerBands.Count; i++) EqualizerBands[i].Level = levels[i];
    }

    private void ApplyEqualizer() {
        var levels = EqualizerBands.Select(band => band.Level).ToArray();
        _playback.SetEqualizer(new EqualizerSettings(levels));
    }

    private void SetAbLoopA() {
        _abLoopStartSeconds = CurrentPositionSeconds;
        _abLoopEndSeconds = null;
        _abLoopState = 1;
        NotifyAbLoopChanged();
        SetStatus("StatusAbLoopA", FormatSeconds(_abLoopStartSeconds.Value));
    }

    private void SetAbLoopB() {
        if (_abLoopStartSeconds is null) {
            SetAbLoopA();
            return;
        }

        if (CurrentPositionSeconds <= _abLoopStartSeconds.Value + 0.5) {
            SetStatus("StatusAbLoopInvalid");
            return;
        }

        _abLoopEndSeconds = CurrentPositionSeconds;
        _abLoopState = 2;
        NotifyAbLoopChanged();
        SetStatus("StatusAbLoopB", FormatSeconds(_abLoopEndSeconds.Value));
    }

    private void ClearAbLoop() {
        AbLoopReset();
        SetStatus("StatusAbLoopCleared");
    }

    private void AbLoopReset() {
        _abLoopStartSeconds = null;
        _abLoopEndSeconds = null;
        _abLoopState = 0;
        NotifyAbLoopChanged();
    }

    private void NotifyAbLoopChanged() {
        OnPropertyChanged(nameof(IsAbLoopActive));
        OnPropertyChanged(nameof(AbLoopStatusLabel));
        OnPropertyChanged(nameof(AbLoopMenuValue));
        OnPropertyChanged(nameof(AbLoopStartFraction));
        OnPropertyChanged(nameof(AbLoopEndFraction));
    }

    private static string FormatSeconds(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private void RefreshPlaybackOptions() {
        SyncOptions(SleepTimerOptions, SleepTimerMinuteValues.Length, index => index == 0
            ? Text["SleepOff"]
            : Text.Format("SleepMinutes", SleepTimerMinuteValues[index]), SleepTimerSelectionIndex);
        SyncOptions(EqualizerOptions, EqualizerPresetKeys.Length, index => Text[EqualizerPresetKeys[index]], EqualizerPresetIndex);
    }

    private static void SyncOptions(ObservableCollection<PlaybackOptionViewModel> options, int count, Func<int, string> labelFactory, int selectedIndex) {
        while (options.Count < count) options.Add(new PlaybackOptionViewModel(string.Empty, options.Count));
        while (options.Count > count) options.RemoveAt(options.Count - 1);
        for (var index = 0; index < options.Count; index++) {
            options[index].Label = labelFactory(index);
            options[index].IsSelected = index == selectedIndex;
        }
    }

    private static void SelectOption(ObservableCollection<PlaybackOptionViewModel> options, int selectedIndex) {
        foreach (var option in options) option.IsSelected = option.Index == selectedIndex;
    }

    private void Stop() {
        if (!HasCurrentTrack) return;
        _pendingSeekTargetSeconds = null;
        _isSeeking = false;
        _playback.Stop();
        IsPlaying = false;
        ApplyPlaybackPosition(TimeSpan.Zero);
        SetStatus("StatusStopped");
        QueueStateSave();
    }

    private void AddTrackToPlaylist(Music track) {
        var targetPlaylist = SelectedPlaylist ?? Playlists.FirstOrDefault();
        if (targetPlaylist is null) {
            targetPlaylist = new Playlist(Text["FallbackPlaylistName"]);
            Playlists.Add(targetPlaylist);
        }

        if (!targetPlaylist.Tracks.Contains(track)) targetPlaylist.Tracks.Add(track);
        if (CurrentPage is PlaylistDetailPageViewModel detail && detail.Playlist == targetPlaylist) detail.NotifyTracksChanged();
        SetStatus("StatusAdded", track.Title, targetPlaylist.Name);
        QueueStateSave();
    }

    private void CreatePlaylist() {
        var name = NewPlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Text.Format("DefaultPlaylistName", Playlists.Count + 1);
        var playlist = new Playlist(name);
        Playlists.Add(playlist);
        NewPlaylistName = string.Empty;
        OpenPlaylist(playlist);
        SetStatus("StatusCreated", playlist.Name);
        QueueStateSave();
    }

    private void DeleteSelectedPlaylist() {
        if (SelectedPlaylist is null) return;
        var deletedName = SelectedPlaylist.Name;
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = null;
        Navigate("playlists");
        SetStatus("StatusDeleted", deletedName);
        SyncFolderWatch();
        QueueStateSave();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) {
        if (_isInitialized || _disposed) return;
        _isInitialized = true;

        PlayerState? state;
        try {
            state = _services.StateStore.Load();
        }
        catch (Exception exception) {
            SetStatus("StatusStateLoadError", exception.Message);
            return;
        }

        if (state is null) {
            var error = _services.StateStore.LastError;
            if (!string.IsNullOrWhiteSpace(error)) SetStatus("StatusStateLoadError", error);
            return;
        }

        _isRestoringState = true;
        try {
            ApplySettings(state);
            SetStatus("StatusRestoringLibrary");
            var labels = new LocalFolderScanLabels(
                Text["UnknownArtist"],
                Text["UnknownAlbum"],
                Text["UnknownGenre"]);
            var knownTracks = Tracks.ToList();
            var restored = await Task.Run(
                () => _services.LibraryRestorer.RestoreAsync(state, knownTracks, labels, cancellationToken),
                cancellationToken);
            if (_disposed) return;
            ApplyRestoredState(state, restored);
        }
        catch (OperationCanceledException) {
            SetStatus("StatusIdle");
        }
        catch (Exception exception) {
            SetStatus("StatusStateLoadError", exception.Message);
        }
        finally {
            _isRestoringState = false;
        }

        // 恢复期间不进监听：那会儿歌单还在重建，会白扫一遍。
        SyncFolderWatch();

        // 对一次账：应用没开着时往文件夹里加/删的歌，靠这次补扫才能跟上。
        // 不阻塞初始化：扫大目录可能要好一会儿，界面先出来更重要。
        _ = RefreshAllFoldersAsync(cancellationToken);
    }

    /// <summary>
    /// 把所有已导入的文件夹重扫一遍。启动时做一次，保证关机期间的变动不漏。
    /// 单个目录失败不影响其他目录，所以这里逐个吞掉异常。
    /// </summary>
    public async Task RefreshAllFoldersAsync(CancellationToken cancellationToken = default) {
        if (_disposed) return;

        var roots = AllFolderRoots().ToList();
        foreach (var root in roots) {
            if (_disposed || cancellationToken.IsCancellationRequested) return;
            try {
                await RefreshFolderAsync(root, cancellationToken);
            }
            catch (OperationCanceledException) {
                return;
            }
            catch (Exception) {
                // 单个目录挂了（网络盘、权限）不该让其余目录也跳过。
            }
        }
    }

    private void ApplySettings(PlayerState state) {
        Text.SetCulture(state.Ui.Language);
        _lastNonZeroVolume = Math.Clamp(state.Playback.LastNonZeroVolume, 0, 1);
        if (_lastNonZeroVolume <= 0.005) _lastNonZeroVolume = 0.72;
        Volume = Math.Clamp(state.Playback.Volume, 0, 1);
        IsShuffleMode = state.Playback.Shuffle;
        RepeatMode = ParseRepeatMode(state.Playback.RepeatMode);
        PlaybackSpeed = state.Playback.Speed ?? MapLegacySpeedIndex(state.Playback.SpeedIndex);
        _isPitchSyncEnabled = state.Playback.PitchSync;
        _pitchSemitones = Math.Clamp(state.Playback.PitchSemitones, -12, 12);
        OnPropertyChanged(nameof(IsPitchSyncEnabled));
        OnPropertyChanged(nameof(PitchSyncStateLabel));
        OnPropertyChanged(nameof(PitchSemitones));
        OnPropertyChanged(nameof(PitchLabel));
        OnPropertyChanged(nameof(ShowPitchUnsupportedNote));
        _playback.SetPitch(_pitchSemitones, _isPitchSyncEnabled);
        EqualizerPresetIndex = state.Playback.EqualizerPresetIndex;
        IsFloatingLyricsVisible = state.Ui.FloatingLyricsVisible;
    }

    private static double MapLegacySpeedIndex(int index) => index switch {
        0 => 0.75,
        1 => 1.0,
        2 => 1.25,
        3 => 1.5,
        4 => 2.0,
        _ => 1.0
    };

    private static int MapSpeedToLegacyIndex(double speed) => speed switch {
        <= 0.875 => 0,
        <= 1.125 => 1,
        <= 1.375 => 2,
        <= 1.75 => 3,
        _ => 4
    };

    private void ApplyRestoredState(PlayerState state, RestoredLibrary restored) {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var libraryPaths = new HashSet<string>(
            Tracks.Select(track => track.FilePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            comparer);
        foreach (var track in restored.Tracks) {
            if (!string.IsNullOrWhiteSpace(track.FilePath) && libraryPaths.Add(track.FilePath)) {
                Tracks.Add(track);
            }
        }
        OnPropertyChanged(nameof(QueueCountLabel));

        // 手删记录要在加歌之前恢复，重扫时才能照着跳过。
        _dismissedPaths.Clear();
        foreach (var (path, fingerprint) in state.Library.DismissedPaths ?? []) {
            if (string.IsNullOrWhiteSpace(path)) continue;
            _dismissedPaths[path] = fingerprint ?? string.Empty;
        }

        SelectedPlaylist = null;
        Playlists.Clear();
        foreach (var playlistState in state.Library.Playlists ?? []) {
            if (playlistState is null) continue;
            var name = string.IsNullOrWhiteSpace(playlistState.Name)
                ? Text.Format("DefaultPlaylistName", Playlists.Count + 1)
                : playlistState.Name;
            var playlist = new Playlist(name);
            var seen = new HashSet<string>(comparer);
            foreach (var path in playlistState.TrackPaths ?? []) {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
                if (restored.ByPath.TryGetValue(path, out var track) && !playlist.Tracks.Contains(track)) {
                    playlist.Tracks.Add(track);
                }
            }

            // 老状态文件没有 FolderRoots；这时按歌单里的路径反推一次，
            // 让已经有歌单的用户也能直接享受到自动刷新。
            var roots = playlistState.FolderRoots ?? [];
            foreach (var root in roots) {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var normalized = NormalizeFolderRoot(root);
                if (normalized is null || playlist.FolderRoots.Contains(normalized, PathComparer)) continue;
                playlist.FolderRoots.Add(normalized);
            }

            if (playlist.FolderRoots.Count == 0) {
                InferFolderRoots(playlist, comparer);
            }

            Playlists.Add(playlist);
        }

        PlaybackQueue = Tracks;

        if (!string.IsNullOrWhiteSpace(state.Playback.LastTrackPath)
            && restored.ByPath.TryGetValue(state.Playback.LastTrackPath, out var lastTrack)) {
            // 先把时钟归零再换歌：setter 里重算歌词行时不会拿上一首的位置去套新歌。
            // 位置随后再补上，这时才按新歌自己的时长做钳制。
            var position = Math.Clamp(state.Playback.LastPositionSeconds, 0, lastTrack.Duration.TotalSeconds);
            ApplyPlaybackPosition(TimeSpan.Zero);
            CurrentTrack = lastTrack;
            ApplyPlaybackPosition(TimeSpan.FromSeconds(position));
        }
        else if (Tracks.Count > 0) {
            // 曲库还在，但上次那首没了（比如老的 demo 路径）。选中恢复出来的第一首，
            // 但不自动播放，控制台和传输控件还有个目标。
            ApplyPlaybackPosition(TimeSpan.Zero);
            CurrentTrack = Tracks[0];
        }

        ResetShuffleState();
        SetStatus(restored.MissingPaths.Count > 0 ? "StatusRestoreMissing" : "StatusIdle",
            restored.MissingPaths.Count);
    }

    /// <summary>
    /// 老状态文件没存歌单的文件夹来源。按歌单里所有歌的共同祖目录反推一个根：
    /// 宁可推得保守（只取共同部分），也不要盯着一个过大的目录把别的东西扫进来。
    /// </summary>
    private static void InferFolderRoots(Playlist playlist, StringComparer comparer) {
        var directories = new List<string>();
        foreach (var track in playlist.Tracks) {
            if (string.IsNullOrWhiteSpace(track.FilePath)) continue;
            try {
                if (!Path.IsPathFullyQualified(track.FilePath)) continue;
                var directory = Path.GetDirectoryName(Path.GetFullPath(track.FilePath));
                if (!string.IsNullOrWhiteSpace(directory)) directories.Add(directory);
            }
            catch (Exception) {
                // 坏路径跳过。
            }
        }

        if (directories.Count == 0) return;

        // 所有歌都在同一个子目录里，那个目录就是来源；
        // 分散在多个子目录时退回它们的共同祖先。
        var common = directories[0];
        foreach (var directory in directories.Skip(1)) {
            common = CommonAncestor(common, directory);
            if (string.IsNullOrEmpty(common)) return;
        }

        if (string.IsNullOrEmpty(common) || !Directory.Exists(common)) return;
        if (!playlist.FolderRoots.Contains(common, comparer)) playlist.FolderRoots.Add(common);
    }

    private static string CommonAncestor(string left, string right) {
        try {
            var leftPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var rightPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(leftPath, rightPath, comparison)) return leftPath;

            var leftParts = leftPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightParts = rightPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var shared = new List<string>();
            for (var index = 0; index < leftParts.Length && index < rightParts.Length; index++) {
                if (!string.Equals(leftParts[index], rightParts[index], comparison)) break;
                shared.Add(leftParts[index]);
            }

            if (shared.Count == 0) return string.Empty;
            var joined = string.Join(Path.DirectorySeparatorChar, shared);
            // 拆出来第一段是根的空串（Unix），补回去。
            if (leftPath.StartsWith(Path.DirectorySeparatorChar) && !joined.StartsWith(Path.DirectorySeparatorChar)) {
                joined = Path.DirectorySeparatorChar + joined;
            }

            return joined.Length == 0 ? string.Empty : joined;
        }
        catch (Exception) {
            return string.Empty;
        }
    }

    private static RepeatMode ParseRepeatMode(string? value) {
        return Enum.TryParse<RepeatMode>(value, ignoreCase: true, out var mode)
               && Enum.IsDefined(typeof(RepeatMode), mode)
            ? mode
            : RepeatMode.Off;
    }

    private PlayerState BuildState() {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var trackPaths = Tracks
            .Select(track => track.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(comparer)
            .ToList();
        var playlists = Playlists
            .Select(playlist => new PlaylistState {
                Name = playlist.Name,
                TrackPaths = playlist.Tracks
                    .Select(track => track.FilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(comparer)
                    .ToList(),
                FolderRoots = playlist.FolderRoots
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(comparer)
                    .ToList()
            })
            .ToList();

        var lyricPaths = new Dictionary<string, string>(comparer);
        void RememberLyrics(Music track) {
            if (!track.HasManualLyrics
                || string.IsNullOrWhiteSpace(track.FilePath)
                || string.IsNullOrWhiteSpace(track.LyricsSourcePath)) return;
            try {
                // 这里别去探文件：断开的网络共享会把 UI 线程上的状态快照卡住。
                // 存不存在交给 restorer 在 UI 线程外查。
                if (!Path.IsPathFullyQualified(track.LyricsSourcePath)) return;
            }
            catch (Exception) {
                return;
            }
            lyricPaths[track.FilePath] = track.LyricsSourcePath;
        }

        foreach (var track in Tracks) RememberLyrics(track);
        RememberLyrics(CurrentTrack);

        return new PlayerState {
            Version = PlayerState.CurrentVersion,
            Playback = new PlaybackSettings {
                Volume = _volume,
                LastNonZeroVolume = _lastNonZeroVolume,
                Shuffle = _isShuffleMode,
                RepeatMode = _repeatMode.ToString(),
                SpeedIndex = MapSpeedToLegacyIndex(_playbackSpeed),
                Speed = _playbackSpeed,
                PitchSync = _isPitchSyncEnabled,
                PitchSemitones = _pitchSemitones,
                EqualizerPresetIndex = _equalizerPresetIndex,
                LastTrackPath = HasCurrentTrack && !string.IsNullOrWhiteSpace(CurrentTrack.FilePath)
                    ? CurrentTrack.FilePath
                    : null,
                LastPositionSeconds = HasCurrentTrack ? _currentPositionSeconds : 0
            },
            Library = new LibrarySettings {
                TrackPaths = trackPaths,
                Playlists = playlists,
                FolderRoots = playlists
                    .SelectMany(playlist => playlist.FolderRoots)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(comparer)
                    .ToList(),
                DismissedPaths = new Dictionary<string, string>(_dismissedPaths, comparer),
                LyricPaths = lyricPaths
            },
            Ui = new UiSettings {
                Language = Text.CultureName,
                FloatingLyricsVisible = _isFloatingLyricsVisible
            }
        };
    }

    private void QueueStateSave() {
        if (_isRestoringState || _disposed) return;
        _stateSaver.Schedule(BuildState());
    }

    public void SaveStateNow() {
        if (_isRestoringState || _disposed) return;
        _stateSaver.Flush(BuildState());
    }

    private void Playback_Ended(PlaybackEnded ended) {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() => {
            if (_disposed || _playback.CurrentSession is not { } current || current.Id != ended.Session.Id) return;

            if (IsAbLoopActive && _abLoopStartSeconds is { } start) {
                // A–B 回绕时在 A 点重载源，不去 seek 已经结束的 MediaPlayer；
                // 有些格式在停止状态会忽略 seek，结果从 0 重新开始。
                StartTrack(CurrentTrack, TimeSpan.FromSeconds(start), autoplay: true);
                return;
            }

            AdvanceAfterTrackEnd();
        });
    }

    private void Playback_Failed(PlaybackFailure failure) {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() => {
            if (_disposed) return;
            _playback.Stop();
            IsPlaying = false;
            SetStatus("StatusPlaybackFailed", failure.Message);
            QueueStateSave();
        });
    }

    private void StateSaver_SaveFailed(string error) {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() => {
            if (!_disposed) SetStatus("StatusStateSaveError", error);
        });
    }

    private void SetStatus(string key, params object[] arguments) {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void ExitLyricsMode() {
        IsLyricsMode = false;
        if (IsSystemFullscreen) IsSystemFullscreen = false;
    }

    private void SystemMedia_CommandReceived(SystemMediaCommand command) {
        Dispatcher.UIThread.Post(() => {
            if (_disposed) return;
            switch (command.Kind) {
                case SystemMediaCommandKind.Play:
                    if (!IsPlaying) TogglePlayback();
                    break;
                case SystemMediaCommandKind.Pause:
                    if (IsPlaying) TogglePlayback();
                    break;
                case SystemMediaCommandKind.TogglePlayPause:
                    TogglePlayback();
                    break;
                case SystemMediaCommandKind.Stop:
                    Stop();
                    break;
                case SystemMediaCommandKind.Next:
                    MoveTrack(1);
                    break;
                case SystemMediaCommandKind.Previous:
                    MoveTrack(-1);
                    break;
                case SystemMediaCommandKind.Seek:
                    CurrentPositionSeconds = command.Position.TotalSeconds;
                    break;
                case SystemMediaCommandKind.Shuffle:
                    IsShuffleMode = command.Flag;
                    break;
                case SystemMediaCommandKind.Repeat:
                    RepeatMode = command.Repeat switch {
                        SystemMediaRepeatMode.Track => RepeatMode.Track,
                        SystemMediaRepeatMode.Playlist => RepeatMode.Playlist,
                        _ => RepeatMode.Off
                    };
                    break;
                case SystemMediaCommandKind.Volume:
                    Volume = command.Number;
                    break;
                case SystemMediaCommandKind.Raise:
                    RaiseRequested?.Invoke();
                    break;
                case SystemMediaCommandKind.Quit:
                    QuitRequested?.Invoke();
                    break;
            }
        });
    }

    private void UpdateSystemMedia() {
        if (_disposed) return;
        if (!HasCurrentTrack) {
            if (!_systemMediaCleared) {
                _services.SystemMedia.Clear();
                _systemMediaCleared = true;
            }
            return;
        }

        _systemMediaCleared = false;
        var track = CurrentTrack;
        _services.SystemMedia.Update(new SystemMediaSnapshot(
            track.Title,
            track.Artist,
            track.Album,
            track.Duration,
            _services.SystemMediaArtwork.GetPath(track),
            track.IsLocalFile ? track.FilePath : null,
            IsPlaying,
            TimeSpan.FromSeconds(CurrentPositionSeconds),
            Volume,
            IsShuffleMode,
            RepeatMode switch {
                RepeatMode.Track => SystemMediaRepeatMode.Track,
                RepeatMode.Playlist => SystemMediaRepeatMode.Playlist,
                _ => SystemMediaRepeatMode.Off
            },
            PlaybackSpeed));
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e) {
        if (_playback.IsLoaded) {
            var snapshot = _playback.Snapshot;
            if (snapshot.IsPlaying != IsPlaying) IsPlaying = snapshot.IsPlaying;

            var positionIsPending = false;
            if (_pendingSeekTargetSeconds is { } pending) {
                // 请求的位置先留在屏幕上，等后端真的追上。不然下一次 100ms 轮询会把滑块拽回去，
                // seek 就显得不靠谱，已经播过的区域尤其明显。
                var reported = snapshot.Position.TotalSeconds;
                if (Math.Abs(reported - pending) <= 0.35 || DateTime.UtcNow >= _pendingSeekUntilUtc) {
                    _pendingSeekTargetSeconds = null;
                    ApplyPlaybackPosition(snapshot.Position);
                }
                else {
                    positionIsPending = true;
                }
            }
            else {
                ApplyPlaybackPosition(snapshot.Position);
            }

            if (!positionIsPending
                && !_isSeeking
                && snapshot.IsPlaying
                && IsAbLoopActive
                && _abLoopStartSeconds is { } start
                && _abLoopEndSeconds is { } end
                && snapshot.Position.TotalSeconds >= end - 0.02) {
                // A–B 走后端回绕，真实音频时钟和 UI 才对得上。
                _pendingSeekTargetSeconds = null;
                ApplyPlaybackPosition(TimeSpan.FromSeconds(start));
                _playback.Seek(TimeSpan.FromSeconds(start));
            }

            if (DateTime.UtcNow - _lastStateSaveUtc >= TimeSpan.FromSeconds(5)) {
                _lastStateSaveUtc = DateTime.UtcNow;
                QueueStateSave();
            }
        }

        UpdateSleepTimer();
        UpdateSystemMedia();
    }

    private void AmbientTimer_Tick(object? sender, EventArgs e) {
        // 33ms 走 0.008，保持原来 ~0.24 rad/s 的漂移，同时把更新率提上来。
        _ambientPhase += 0.008;
        OnPropertyChanged(nameof(AmbientPhase));
    }

    private void UpdateSleepTimer() {
        if (_sleepDeadlineUtc is null) return;
        var remaining = _sleepDeadlineUtc.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) {
            _sleepDeadlineUtc = null;
            _sleepTimerSelectionIndex = 0;
            foreach (var option in SleepTimerOptions) option.IsSelected = option.Index == 0;
            _playback.Stop();
            IsPlaying = false;
            ApplyPlaybackPosition(TimeSpan.Zero);
            OnPropertyChanged(nameof(SleepTimerSelectionIndex));
            OnPropertyChanged(nameof(SleepTimerRemainingLabel));
            OnPropertyChanged(nameof(SleepTimerMenuValue));
            OnPropertyChanged(nameof(HasSleepTimer));
            SetStatus("StatusSleepFinished");
            QueueStateSave();
            return;
        }

        var remainingSecond = (int)remaining.TotalSeconds;
        if (remainingSecond == _lastSleepRemainingSecond) return;
        _lastSleepRemainingSecond = remainingSecond;
        OnPropertyChanged(nameof(SleepTimerRemainingLabel));
        OnPropertyChanged(nameof(SleepTimerMenuValue));
    }

    private void UpdateCurrentLyric() {
        var position = CurrentPositionSeconds;
        // 每 100ms 的时钟都会走到这里。索引只在换行或位置跳回去时才需要重算，
        // 其余时候沿用缓存，省掉一遍全表扫描。
        if (double.IsNaN(_currentLyricIndexPosition) || position < _currentLyricIndexPosition) {
            _currentLyricIndex = FindCurrentLyricIndex(position);
        }
        else if (_currentLyricIndex < CurrentTrack.Lyrics.Count - 1) {
            var next = CurrentTrack.Lyrics[_currentLyricIndex + 1];
            if (next.Timestamp.TotalSeconds <= position) _currentLyricIndex = FindCurrentLyricIndex(position);
        }

        _currentLyricIndexPosition = position;
        var activeLine = _currentLyricIndex;
        for (var index = 0; index < CurrentTrack.Lyrics.Count; index++) {
            var line = CurrentTrack.Lyrics[index];
            line.IsCurrent = index == activeLine;
            line.CurrentWordIndex = index == activeLine ? FindCurrentWord(line, position) : -1;
        }

        if (_lastCurrentLyricIndex != activeLine) {
            _lastCurrentLyricIndex = activeLine;
            OnPropertyChanged(nameof(CurrentLyricIndex));
            OnPropertyChanged(nameof(CurrentLyric));
            OnPropertyChanged(nameof(CurrentLyricText));
            OnPropertyChanged(nameof(CurrentLyricTranslation));
            OnPropertyChanged(nameof(HasCurrentLyricTranslation));
        }
    }

    /// <summary>位置对应的歌词下标；还没唱到第一行时是 -1。</summary>
    private int FindCurrentLyricIndex(double positionSeconds) {
        var lyrics = CurrentTrack.Lyrics;
        for (var index = lyrics.Count - 1; index >= 0; index--) {
            if (lyrics[index].Timestamp.TotalSeconds <= positionSeconds) return index;
        }

        return -1;
    }

    private static int FindCurrentWord(LyricLine line, double positionSeconds) {
        if (!line.HasWordTiming) return -1;

        var index = -1;
        for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++) {
            if (line.Words[wordIndex].Start.TotalSeconds <= positionSeconds + 0.001) index = wordIndex;
            else break;
        }
        return index;
    }

    public int CurrentLyricIndex {
        get {
            // 歌词可能在当前歌上被换掉（手动选文件、sidecar 重新挂载）。
            // 缓存值如果越界就当场按当前时钟重算，别让绑定读到 OutOfRange。
            if (_currentLyricIndex >= CurrentTrack.Lyrics.Count) {
                _currentLyricIndex = FindCurrentLyricIndex(CurrentPositionSeconds);
                _currentLyricIndexPosition = CurrentPositionSeconds;
            }

            return _currentLyricIndex;
        }
    }

    private void PitchApplyTimer_Tick(object? sender, EventArgs e) {
        _pitchApplyTimer.Stop();
        _playback.SetPitch(_pitchSemitones, _isPitchSyncEnabled);
        if (IsPitchShiftSupported) SetStatus("StatusPitch", PitchLabel);
    }

    private static Music CreateEmptyTrack() => new() {
        Title = "—",
        Artist = string.Empty,
        Album = string.Empty,
        Genre = string.Empty,
        ArtworkColor = "#4A3D2E",
        Duration = TimeSpan.FromSeconds(1),
        FilePath = string.Empty,
        BitrateKbps = 0
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() {
        if (_disposed) return;
        SaveStateNow();
        _disposed = true;

        _playbackTimer.Stop();
        _playbackTimer.Tick -= PlaybackTimer_Tick;
        _ambientTimer.Stop();
        _ambientTimer.Tick -= AmbientTimer_Tick;
        _pitchApplyTimer.Stop();
        _pitchApplyTimer.Tick -= PitchApplyTimer_Tick;
        _playback.Ended -= Playback_Ended;
        _playback.Failed -= Playback_Failed;
        _stateSaver.SaveFailed -= StateSaver_SaveFailed;
        _services.SystemMedia.CommandReceived -= SystemMedia_CommandReceived;
        _services.FolderWatch.Changed -= FolderWatch_Changed;
        Text.PropertyChanged -= Text_PropertyChanged;
        _services.SystemMedia.Clear();
        _services.Dispose();
    }
}
