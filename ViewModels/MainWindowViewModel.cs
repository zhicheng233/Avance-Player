using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avance_Player.Models;
using Avance_Player.Services;
using Avance_Player.Services.Lyrics;
using Avalonia.Threading;

namespace Avance_Player.ViewModels;

public enum RepeatMode {
    Off,
    Track,
    Playlist
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable {
    private static readonly int[] SleepTimerMinuteValues = [0, 15, 30, 45, 60, 90];
    private static readonly double[] PlaybackSpeedValues = [0.75, 1.0, 1.25, 1.5, 2.0];

    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _ambientTimer;
    private readonly Random _random = new();
    private readonly Stack<Music> _shuffleHistory = new();
    private readonly HashSet<Music> _shufflePlayed = [];
    private readonly LyricsService _lyricsService = new();
    private double _currentPositionSeconds;
    private bool _isPlaying;
    private string _newPlaylistName = string.Empty;
    private string _statusKey = "StatusIdle";
    private object[] _statusArguments = [];
    private double _ambientPhase;
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
    private int _playbackSpeedIndex = 1;
    private int _sleepTimerSelectionIndex;
    private DateTime? _sleepDeadlineUtc;
    private int _lastSleepRemainingSecond = -1;
    private int _abLoopState;
    private double? _abLoopStartSeconds;
    private double? _abLoopEndSeconds;
    private CategoryGroupViewModel? _openedCategoryGroup;
    private int _lastCurrentLyricIndex = int.MinValue;
    private bool _isCompactLayout;
    private string _expandedMoreSection = string.Empty;

    public MainWindowViewModel() {
        Tracks = CreateDemoTracks();
        // Demo-only: the prototype has no audio backend, so two tracks get evenly distributed
        // word timings to exercise the word-by-word renderer before a real lyric file is imported.
        if (Tracks.Count > 0) Tracks[0].Lyrics = AddDemoWordTimings(Tracks[0].Lyrics, Tracks[0].Duration);
        var underSameSky = Tracks.FirstOrDefault(track => track.Title == "Under the Same Sky");
        if (underSameSky is not null) underSameSky.Lyrics = AddDemoWordTimings(underSameSky.Lyrics, underSameSky.Duration);
        var lateNight = new Playlist("Late night");
        lateNight.Tracks.Add(Tracks[1]);
        lateNight.Tracks.Add(Tracks[3]);
        var softFocus = new Playlist("Soft focus");
        softFocus.Tracks.Add(Tracks[0]);
        softFocus.Tracks.Add(Tracks[2]);
        Playlists = [lateNight, softFocus];
        RecentTracks = [Tracks[0], Tracks[2], Tracks[1]];
        _currentTrack = Tracks[0];
        _currentTrack.IsCurrent = true;
        _playbackQueue = Tracks;
        Text = new LocalizedText();
        Text.PropertyChanged += Text_PropertyChanged;
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
                (CurrentPage as PlaylistDetailPageViewModel)?.NotifyTracksChanged();
            }
        });
        TogglePlaybackCommand = new RelayCommand(_ => IsPlaying = !IsPlaying);
        StopCommand = new RelayCommand(_ => Stop());
        NextTrackCommand = new RelayCommand(_ => MoveTrack(1));
        PreviousTrackCommand = new RelayCommand(_ => MoveTrack(-1));
        ToggleShuffleCommand = new RelayCommand(_ => IsShuffleMode = !IsShuffleMode);
        CycleRepeatModeCommand = new RelayCommand(_ => CycleRepeatMode());
        TogglePopoverCommand = new RelayCommand(parameter => TogglePopover(parameter?.ToString() ?? string.Empty));
        ClosePopoverCommand = new RelayCommand(_ => ActivePopover = string.Empty);
        ToggleMoreSectionCommand = new RelayCommand(parameter => ToggleMoreSection(parameter?.ToString() ?? string.Empty));
        ToggleMuteCommand = new RelayCommand(_ => ToggleMute());
        ToggleFloatingLyricsCommand = new RelayCommand(_ => IsFloatingLyricsVisible = !IsFloatingLyricsVisible);
        CycleEqualizerCommand = new RelayCommand(_ => CycleEqualizer());
        SelectEqualizerCommand = new RelayCommand(parameter => {
            if (parameter is PlaybackOptionViewModel option) {
                EqualizerPresetIndex = option.Index;
                SetStatus("StatusEqualizer", EqualizerPresetLabel);
            }
        });
        CyclePlaybackSpeedCommand = new RelayCommand(_ => CyclePlaybackSpeed());
        SelectPlaybackSpeedCommand = new RelayCommand(parameter => {
            if (parameter is PlaybackOptionViewModel option) {
                PlaybackSpeedIndex = option.Index;
                SetStatus("StatusPlaybackSpeed", PlaybackSpeedLabel);
            }
        });
        SetAbLoopACommand = new RelayCommand(_ => SetAbLoopA());
        SetAbLoopBCommand = new RelayCommand(_ => SetAbLoopB());
        ClearAbLoopCommand = new RelayCommand(_ => ClearAbLoop());
        SelectSleepTimerCommand = new RelayCommand(parameter => {
            if (parameter is PlaybackOptionViewModel option) SleepTimerSelectionIndex = option.Index;
        });
        OpenPlayerCommand = new RelayCommand(_ => {
            IsLyricsMode = true;
        });
        ExitLyricsCommand = new RelayCommand(_ => ExitLyricsMode());
        ToggleLanguageCommand = new RelayCommand(_ => Text.ToggleLanguage());
        ToggleFullscreenCommand = new RelayCommand(_ => IsSystemFullscreen = !IsSystemFullscreen);
        SeekToLyricCommand = new RelayCommand(parameter => {
            if (parameter is LyricLine line) CurrentPositionSeconds = line.Timestamp.TotalSeconds;
        });
        CreatePlaylistCommand = new RelayCommand(_ => CreatePlaylist());
        DeletePlaylistCommand = new RelayCommand(_ => DeleteSelectedPlaylist());

        // 100 ms keeps the seek fill and lyric clock visibly smooth without a per-frame timer.
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
        // ~30 fps keeps the ambient field and seek wave smooth without a full per-frame timer.
        _ambientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _ambientTimer.Tick += AmbientTimer_Tick;
        _ambientTimer.Start();
        UpdateCurrentLyric();
    }

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
    public RelayCommand CyclePlaybackSpeedCommand { get; }
    public RelayCommand SelectPlaybackSpeedCommand { get; }
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
    /// Which inline section of the "more playback options" menu is expanded. The menu is a compact
    /// list of rows; each row reveals its controls in place instead of showing every control at once.
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

    /// <summary>Compact trailing value shown on each menu row (e.g. "Warm", "1.25×").</summary>
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
        }
    }

    public bool IsWideLayout => !IsCompactLayout;

    public double Volume {
        get => _volume;
        set {
            var nextValue = Math.Clamp(value, 0, 1);
            if (Math.Abs(_volume - nextValue) < 0.005) return;
            _volume = nextValue;
            if (nextValue > 0.005) _lastNonZeroVolume = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumePercentage));
            OnPropertyChanged(nameof(IsMuted));
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
        }
    }

    public int PlaybackSpeedIndex {
        get => _playbackSpeedIndex;
        private set {
            var nextValue = Math.Clamp(value, 0, PlaybackSpeedValues.Length - 1);
            if (_playbackSpeedIndex == nextValue) return;
            _playbackSpeedIndex = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackSpeedLabel));
            OnPropertyChanged(nameof(SpeedMenuValue));
            SelectOption(PlaybackSpeedOptions, nextValue);
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

    public double PlaybackSpeed => PlaybackSpeedValues[PlaybackSpeedIndex];
    public string PlaybackSpeedLabel => $"{PlaybackSpeed:0.##}×";
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
    public ObservableCollection<PlaybackOptionViewModel> PlaybackSpeedOptions { get; } = [];

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

    public Music CurrentTrack {
        get => _currentTrack;
        private set {
            if (_currentTrack == value) return;
            _currentTrack.IsCurrent = false;
            _currentTrack = value;
            _currentTrack.IsCurrent = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentDurationSeconds));
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(LocalQualityLabel));
            OnPropertyChanged(nameof(NoLyricsAvailable));
            Palette = MonetPaletteService.FromColorSeed(value.ArtworkColor);
            OnPropertyChanged(nameof(Palette));
            _lastCurrentLyricIndex = int.MinValue;
            UpdateCurrentLyric();
            OnPropertyChanged(nameof(CurrentLyricIndex));
            OnPropertyChanged(nameof(CurrentLyric));
            OnPropertyChanged(nameof(CurrentLyricText));
            OnPropertyChanged(nameof(CurrentLyricTranslation));
            OnPropertyChanged(nameof(AbLoopStatusLabel));
            OnPropertyChanged(nameof(AbLoopStartFraction));
            OnPropertyChanged(nameof(AbLoopEndFraction));
            _ = TryLoadSidecarForCurrentTrackAsync(value);
        }
    }

    public async Task LoadLyricsFromFileAsync(string path) {
        try {
            var track = CurrentTrack;
            var document = await _lyricsService.LoadAsync(path, track.Duration);
            if (!ReferenceEquals(track, CurrentTrack)) return;
            ApplyLyrics(document, Path.GetFileName(path));
            ActivePopover = string.Empty;
        }
        catch (LyricsParseException exception) {
            SetStatus("StatusLyricsError", exception.Message);
        }
        catch (Exception exception) {
            SetStatus("StatusLyricsError", exception.Message);
        }
    }

    /// <summary>Attaches parsed lyrics to the current track and refreshes every lyric binding.</summary>
    public void ApplyLyrics(LyricsDocument document, string? sourcePath = null) {
        CurrentTrack.Lyrics = document.Lines;
        CurrentTrack.LyricsFormat = document.Format;
        CurrentTrack.LyricsSourcePath = sourcePath;

        _lastCurrentLyricIndex = int.MinValue;
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
    /// Sidecar loading is deliberately conservative: it only runs for tracks that have no lyrics
    /// yet, so imported or demo lyrics are never replaced behind the user's back.
    /// </summary>
    private async Task TryLoadSidecarForCurrentTrackAsync(Music track) {
        if (track.Lyrics.Count > 0) return;

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
            SetStatus(value ? "StatusPlaying" : "StatusPaused", value ? [CurrentTrack.Title] : []);
        }
    }

    public double CurrentDurationSeconds => Math.Max(1, CurrentTrack.Duration.TotalSeconds);
    public bool NoLyricsAvailable => CurrentTrack.Lyrics.Count == 0;
    public double AmbientPhase => _ambientPhase;
    public LyricLine? CurrentLyric => CurrentLyricIndex >= 0 ? CurrentTrack.Lyrics[CurrentLyricIndex] : null;
    public string CurrentLyricText => CurrentLyricIndex >= 0 ? CurrentTrack.Lyrics[CurrentLyricIndex].Text : string.Empty;
    public string CurrentLyricTranslation => CurrentLyricIndex >= 0 ? CurrentTrack.Lyrics[CurrentLyricIndex].Translation ?? string.Empty : string.Empty;
    public bool HasCurrentLyricTranslation => !string.IsNullOrWhiteSpace(CurrentLyricTranslation);
    public string CurrentTimeLabel => TimeSpan.FromSeconds(CurrentPositionSeconds).ToString(@"m\:ss");
    public string DurationLabel => CurrentTrack.Duration.ToString(@"m\:ss");
    public string LocalQualityLabel => CurrentTrack.LocalQualityLabel;

    public double CurrentPositionSeconds {
        get => _currentPositionSeconds;
        set {
            var nextValue = Math.Clamp(value, 0, CurrentDurationSeconds);
            if (Math.Abs(_currentPositionSeconds - nextValue) < 0.05) return;
            _currentPositionSeconds = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTimeLabel));
            UpdateCurrentLyric();
            if (_currentPositionSeconds >= CurrentDurationSeconds) AdvanceAfterTrackEnd();
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
        CurrentPage = destination switch {
            "artists" => CreateCategoryPage(Text["ArtistsTitle"], Text["ArtistsSubtitle"], track => track.Artist),
            "albums" => CreateCategoryPage(Text["AlbumsTitle"], Text["AlbumsSubtitle"], track => track.Album),
            "genres" => CreateCategoryPage(Text["GenresTitle"], Text["GenresSubtitle"], track => track.Genre),
            "playlists" => new PlaylistsPageViewModel(Playlists),
            "recent" => new TrackCollectionPageViewModel(Text["RecentlyTitle"], Text["RecentlySubtitle"], RecentTracks.ToArray()),
            _ => new LibraryPageViewModel(Tracks)
        };
    }

    private CategoryBrowsePageViewModel CreateCategoryPage(string title, string subtitle, Func<Music, string> selector) {
        var groups = Tracks
            .GroupBy(selector)
            .Select(group => new CategoryGroupViewModel(
                group.Key,
                Text.Format(group.Count() == 1 ? "SongCountSingular" : "SongCount", group.Count()),
                group.First().ArtworkColor,
                group.ToArray()))
            .ToList();
        return new CategoryBrowsePageViewModel(title, subtitle, new ObservableCollection<CategoryGroupViewModel>(groups));
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
        CurrentTrack = track;
        CurrentPositionSeconds = 0;
        AbLoopReset();
        IsPlaying = true;
        SetStatus("StatusPlaying", track.Title);
        RecentTracks.Remove(track);
        RecentTracks.Insert(0, track);
        while (RecentTracks.Count > 12) RecentTracks.RemoveAt(RecentTracks.Count - 1);
    }

    private void MoveTrack(int direction) {
        if (PlaybackQueue.Count == 0) return;
        if (direction > 0 && IsShuffleMode) {
            PlayNextShuffle();
            return;
        }

        if (direction < 0 && IsShuffleMode && _shuffleHistory.Count > 0) {
            var previous = _shuffleHistory.Pop();
            CurrentTrack = previous;
            CurrentPositionSeconds = 0;
            AbLoopReset();
            IsPlaying = true;
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
                CurrentPositionSeconds = 0;
                return;
            case RepeatMode.Off when IsLastSequentialTrack():
                IsPlaying = false;
                CurrentPositionSeconds = CurrentDurationSeconds;
                SetStatus("StatusQueueEnded");
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
                IsPlaying = false;
                SetStatus("StatusQueueEnded");
            }
            return;
        }

        var unplayed = PlaybackQueue.Where(track => !_shufflePlayed.Contains(track) && !ReferenceEquals(track, CurrentTrack)).ToList();
        if (unplayed.Count == 0) {
            // With shuffle and no repeat, stop once every track has been heard.
            if (RepeatMode == RepeatMode.Off) {
                IsPlaying = false;
                SetStatus("StatusQueueEnded");
                return;
            }
            _shufflePlayed.Clear();
            unplayed = PlaybackQueue.Where(track => !ReferenceEquals(track, CurrentTrack)).ToList();
        }

        var next = unplayed[_random.Next(unplayed.Count)];
        _shufflePlayed.Add(CurrentTrack);
        _shuffleHistory.Push(CurrentTrack);
        CurrentTrack = next;
        CurrentPositionSeconds = 0;
        AbLoopReset();
        IsPlaying = true;
    }

    private void ResetShuffleState() {
        _shuffleHistory.Clear();
        _shufflePlayed.Clear();
        if (PlaybackQueue.Count > 0) _shufflePlayed.Add(CurrentTrack);
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

    private void CyclePlaybackSpeed() {
        PlaybackSpeedIndex = (PlaybackSpeedIndex + 1) % PlaybackSpeedValues.Length;
        SetStatus("StatusPlaybackSpeed", PlaybackSpeedLabel);
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
        SyncOptions(PlaybackSpeedOptions, PlaybackSpeedValues.Length, index => $"{PlaybackSpeedValues[index]:0.##}×", PlaybackSpeedIndex);
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
        IsPlaying = false;
        CurrentPositionSeconds = 0;
        SetStatus("StatusStopped");
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
    }

    private void CreatePlaylist() {
        var name = NewPlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Text.Format("DefaultPlaylistName", Playlists.Count + 1);
        var playlist = new Playlist(name);
        Playlists.Add(playlist);
        NewPlaylistName = string.Empty;
        OpenPlaylist(playlist);
        SetStatus("StatusCreated", playlist.Name);
    }

    private void DeleteSelectedPlaylist() {
        if (SelectedPlaylist is null) return;
        var deletedName = SelectedPlaylist.Name;
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = null;
        Navigate("playlists");
        SetStatus("StatusDeleted", deletedName);
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

    private void PlaybackTimer_Tick(object? sender, EventArgs e) {
        if (IsPlaying) {
            var nextPosition = CurrentPositionSeconds + 0.1 * PlaybackSpeed;
            if (IsAbLoopActive && nextPosition >= (_abLoopEndSeconds ?? CurrentDurationSeconds) - 0.02) {
                // Wrap only the simulated playback clock; manual seeks stay free.
                CurrentPositionSeconds = _abLoopStartSeconds ?? 0;
            }
            else {
                CurrentPositionSeconds = nextPosition;
            }
        }

        UpdateSleepTimer();
    }

    private void AmbientTimer_Tick(object? sender, EventArgs e) {
        // 0.008 per 33 ms keeps the original ~0.24 rad/s drift while raising the update rate.
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
            IsPlaying = false;
            CurrentPositionSeconds = 0;
            OnPropertyChanged(nameof(SleepTimerSelectionIndex));
            OnPropertyChanged(nameof(SleepTimerRemainingLabel));
            OnPropertyChanged(nameof(SleepTimerMenuValue));
            OnPropertyChanged(nameof(HasSleepTimer));
            SetStatus("StatusSleepFinished");
            return;
        }

        var remainingSecond = (int)remaining.TotalSeconds;
        if (remainingSecond == _lastSleepRemainingSecond) return;
        _lastSleepRemainingSecond = remainingSecond;
        OnPropertyChanged(nameof(SleepTimerRemainingLabel));
        OnPropertyChanged(nameof(SleepTimerMenuValue));
    }

    private void UpdateCurrentLyric() {
        var activeLine = CurrentLyricIndex;
        var position = CurrentPositionSeconds;
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
            for (var index = CurrentTrack.Lyrics.Count - 1; index >= 0; index--) {
                if (CurrentTrack.Lyrics[index].Timestamp.TotalSeconds <= CurrentPositionSeconds) return index;
            }
            return -1;
        }
    }

    /// <summary>
    /// Demo-only helper: spreads a line's duration across its words so the prototype can show
    /// word highlighting without a real audio clock or a bundled word-timed lyric file. Imported
    /// files replace these synthetic timings.
    /// </summary>
    private static IReadOnlyList<LyricLine> AddDemoWordTimings(IReadOnlyList<LyricLine> lines, TimeSpan duration) {
        var result = new List<LyricLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++) {
            var line = lines[index];
            var end = index + 1 < lines.Count ? lines[index + 1].Timestamp : duration;
            var words = Regex.Matches(line.Text, @"\S+\s*")
                .Select(match => match.Value)
                .ToList();
            if (words.Count == 0 || end <= line.Timestamp) {
                result.Add(line);
                continue;
            }

            var totalWeight = words.Sum(word => Math.Max(1, word.Trim().Length));
            var span = end - line.Timestamp;
            var cursor = line.Timestamp;
            var timedWords = new List<LyricWord>(words.Count);
            for (var wordIndex = 0; wordIndex < words.Count; wordIndex++) {
                var weight = Math.Max(1, words[wordIndex].Trim().Length);
                var wordEnd = wordIndex == words.Count - 1
                    ? end
                    : cursor + TimeSpan.FromTicks((long)(span.Ticks * (weight / (double)totalWeight)));
                if (wordEnd <= cursor) wordEnd = cursor + TimeSpan.FromMilliseconds(1);
                timedWords.Add(new LyricWord(words[wordIndex], cursor, wordEnd));
                cursor = wordEnd;
            }

            result.Add(new LyricLine(line.Timestamp, line.Text, line.Translation, line.Romanization, end, timedWords));
        }
        return result;
    }

    private static ObservableCollection<Music> CreateDemoTracks() => [
        new Music {
            Title = "A Walk Through the Rain", Artist = "Mira Sol", Album = "Quiet Cities", Genre = "Indie", Duration = TimeSpan.FromSeconds(228), ArtworkColor = "#5F7F72", FilePath = "demo/quiet-cities/01.flac", BitrateKbps = 1411,
            Lyrics = [new(TimeSpan.FromSeconds(0), "The streets are silver after the rain", "雨后的街道泛着银光"), new(TimeSpan.FromSeconds(12), "And every window knows my name", "每一扇窗都认得我的名字"), new(TimeSpan.FromSeconds(25), "We take the long way home tonight", "今夜，我们绕远路回家"), new(TimeSpan.FromSeconds(39), "Under a sky of violet light", "走在紫色天光之下"), new(TimeSpan.FromSeconds(54), "The quiet has a sound of its own", "寂静也有自己的声音"), new(TimeSpan.FromSeconds(76), "A train is drawing lines in blue", "列车在蓝色夜色里划出轨迹"), new(TimeSpan.FromSeconds(101), "The city keeps a light for you", "这座城市为你留着一盏灯"), new(TimeSpan.FromSeconds(129), "We leave our hurried selves behind", "把匆忙的自己留在身后"), new(TimeSpan.FromSeconds(158), "And let the evening change its mind", "让夜晚慢慢改变心意"), new(TimeSpan.FromSeconds(190), "The rain lets go, the stars come through", "雨停了，星光穿过云层"), new(TimeSpan.FromSeconds(213), "The long way home leads back to you", "回家的长路，最终通向你")]
        },
        new Music {
            Title = "Blue Hour, Again", Artist = "Juniper Vale", Album = "Soft Signals", Genre = "Electronic", Duration = TimeSpan.FromSeconds(194), ArtworkColor = "#3F7A86", FilePath = "demo/soft-signals/02.flac", BitrateKbps = 1536,
            Lyrics = [new(TimeSpan.FromSeconds(0), "Stay until the blue turns gold"), new(TimeSpan.FromSeconds(14), "Tell me what the night has told"), new(TimeSpan.FromSeconds(28), "Every little light is close"), new(TimeSpan.FromSeconds(43), "Every road becomes a home"), new(TimeSpan.FromSeconds(63), "We can let the hours unwind"), new(TimeSpan.FromSeconds(86), "Leave the louder world behind"), new(TimeSpan.FromSeconds(111), "A quiet signal finds its way"), new(TimeSpan.FromSeconds(138), "And carries us into the day"), new(TimeSpan.FromSeconds(166), "Stay until the blue turns gold"), new(TimeSpan.FromSeconds(184), "There's still a story left untold")]
        },
        new Music {
            Title = "Paper Moons", Artist = "Mira Sol", Album = "Quiet Cities", Genre = "Indie", Duration = TimeSpan.FromSeconds(246), ArtworkColor = "#B3785A", FilePath = "demo/quiet-cities/04.flac", BitrateKbps = 2304,
            Lyrics = [new(TimeSpan.FromSeconds(0), "We made a world from paper moons"), new(TimeSpan.FromSeconds(16), "And hummed along to empty rooms"), new(TimeSpan.FromSeconds(31), "If morning comes, let it come slow"), new(TimeSpan.FromSeconds(46), "There's nowhere else we need to go"), new(TimeSpan.FromSeconds(71), "The ceiling turns to open skies"), new(TimeSpan.FromSeconds(98), "The truth is soft around the sides"), new(TimeSpan.FromSeconds(126), "A little hope, a little tune"), new(TimeSpan.FromSeconds(153), "We disappear into the room"), new(TimeSpan.FromSeconds(187), "We made a world from paper moons"), new(TimeSpan.FromSeconds(218), "And morning waits outside")]
        },
        new Music {
            Title = "Almost Home (feat. Leila)", Artist = "Northbound", Album = "Open Windows", Genre = "Soul", Duration = TimeSpan.FromSeconds(213), ArtworkColor = "#B66F6B", FilePath = "demo/open-windows/03.flac", BitrateKbps = 1411,
            Lyrics = [new(TimeSpan.FromSeconds(0), "I can see the porch light from here"), new(TimeSpan.FromSeconds(15), "The road gets softer when you're near"), new(TimeSpan.FromSeconds(30), "One more mile, one more song"), new(TimeSpan.FromSeconds(45), "I've been almost home all along"), new(TimeSpan.FromSeconds(67), "The windows glow against the dark"), new(TimeSpan.FromSeconds(91), "A small familiar kind of spark"), new(TimeSpan.FromSeconds(116), "The night folds up its coat of blue"), new(TimeSpan.FromSeconds(142), "And every step comes back to you"), new(TimeSpan.FromSeconds(169), "I can see the porch light shine"), new(TimeSpan.FromSeconds(196), "This way home is yours and mine")]
        },
        new Music {
            Title = "Slow Motion", Artist = "Lena Grey", Album = "Little Hours", Genre = "Acoustic", Duration = TimeSpan.FromSeconds(181), ArtworkColor = "#A88E5E", FilePath = "demo/little-hours/01.flac", BitrateKbps = 1536,
            Lyrics = [new(TimeSpan.FromSeconds(0), "Let the morning take its time"), new(TimeSpan.FromSeconds(13), "Let the dust become the light"), new(TimeSpan.FromSeconds(27), "Nothing here is asking us to run"), new(TimeSpan.FromSeconds(42), "We can move in slow motion"), new(TimeSpan.FromSeconds(62), "The kettle sings a quiet note"), new(TimeSpan.FromSeconds(84), "A little warmth inside my coat"), new(TimeSpan.FromSeconds(108), "The golden hour crosses the floor"), new(TimeSpan.FromSeconds(133), "We don't need anything more"), new(TimeSpan.FromSeconds(155), "Let the morning take its time"), new(TimeSpan.FromSeconds(172), "Let this little light be mine")]
        },
        new Music {
            Title = "Under the Same Sky", Artist = "Juniper Vale", Album = "Soft Signals", Genre = "Electronic", Duration = TimeSpan.FromSeconds(205), ArtworkColor = "#5C8471", FilePath = "demo/soft-signals/05.flac", BitrateKbps = 2304,
            Lyrics = [new(TimeSpan.FromSeconds(0), "Somewhere there the day begins"), new(TimeSpan.FromSeconds(14), "Somewhere here the night folds in"), new(TimeSpan.FromSeconds(29), "Different clocks, a single sky"), new(TimeSpan.FromSeconds(44), "You and I are passing by"), new(TimeSpan.FromSeconds(66), "The same wind moves the trees"), new(TimeSpan.FromSeconds(89), "Across a thousand quiet seas"), new(TimeSpan.FromSeconds(112), "No matter where the hours lie"), new(TimeSpan.FromSeconds(137), "We're looking at the same sky"), new(TimeSpan.FromSeconds(163), "Somewhere there the day begins"), new(TimeSpan.FromSeconds(188), "Somewhere here the night folds in")]
        },
        new Music {
            Title = "Still Water", Artist = "Lena Grey", Album = "Little Hours", Genre = "Ambient", Duration = TimeSpan.FromSeconds(272), ArtworkColor = "#4B7B84", FilePath = "demo/little-hours/06.flac", BitrateKbps = 2822
        }
    ];

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() {
        _playbackTimer.Stop();
        _playbackTimer.Tick -= PlaybackTimer_Tick;
        _ambientTimer.Stop();
        _ambientTimer.Tick -= AmbientTimer_Tick;
        Text.PropertyChanged -= Text_PropertyChanged;
    }
}
