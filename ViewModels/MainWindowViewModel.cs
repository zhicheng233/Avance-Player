using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avance_Player.Models;
using Avalonia.Threading;

namespace Avance_Player.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable {
    private readonly DispatcherTimer _playbackTimer;
    private double _currentPositionSeconds;
    private bool _isPlaying;
    private string _newPlaylistName = string.Empty;
    private string _statusMessage = "Made for the moments in between.";
    private double _ambientPhase;
    private double _ambientOpacity = 0.12;
    private Music _currentTrack;
    private Playlist? _selectedPlaylist;
    private PageViewModel _currentPage;
    private string _selectedNavigation = "library";

    public MainWindowViewModel() {
        Tracks = CreateDemoTracks();
        var lateNight = new Playlist("Late night");
        lateNight.Tracks.Add(Tracks[1]);
        lateNight.Tracks.Add(Tracks[3]);
        var softFocus = new Playlist("Soft focus");
        softFocus.Tracks.Add(Tracks[0]);
        softFocus.Tracks.Add(Tracks[2]);
        Playlists = [lateNight, softFocus];
        RecentTracks = [Tracks[0], Tracks[2], Tracks[1]];
        _currentTrack = Tracks[0];
        _currentPage = new LibraryPageViewModel(Tracks);

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter?.ToString() ?? "library"));
        OpenPlaylistCommand = new RelayCommand(parameter => {
            if (parameter is Playlist playlist) OpenPlaylist(playlist);
        });
        OpenCategoryGroupCommand = new RelayCommand(parameter => {
            if (parameter is CategoryGroupViewModel group) {
                CurrentPage = new TrackCollectionPageViewModel(group.Title, group.Subtitle, group.Tracks);
                SelectedNavigation = "library";
            }
        });
        PlayTrackCommand = new RelayCommand(parameter => {
            if (parameter is Music track) PlayTrack(track);
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
        OpenPlayerCommand = new RelayCommand(_ => {
            CurrentPage = new PlayerPageViewModel();
            SelectedNavigation = "player";
        });
        CreatePlaylistCommand = new RelayCommand(_ => CreatePlaylist());
        DeletePlaylistCommand = new RelayCommand(_ => DeleteSelectedPlaylist());

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
        UpdateCurrentLyric();
    }

    public ObservableCollection<Music> Tracks { get; }
    public ObservableCollection<Playlist> Playlists { get; }
    public ObservableCollection<Music> RecentTracks { get; }

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
    public RelayCommand OpenPlayerCommand { get; }
    public RelayCommand CreatePlaylistCommand { get; }
    public RelayCommand DeletePlaylistCommand { get; }

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
            _currentTrack = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentDurationSeconds));
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(NoLyricsAvailable));
            UpdateCurrentLyric();
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
            OnPropertyChanged(nameof(PlaybackGlyph));
            StatusMessage = value ? $"Playing “{CurrentTrack.Title}”" : "Playback paused.";
        }
    }

    public string PlaybackGlyph => IsPlaying ? "Ⅱ" : "▶";
    public double CurrentDurationSeconds => Math.Max(1, CurrentTrack.Duration.TotalSeconds);
    public bool NoLyricsAvailable => CurrentTrack.Lyrics.Count == 0;
    public double AmbientOpacity {
        get => _ambientOpacity;
        private set {
            if (Math.Abs(_ambientOpacity - value) < 0.005) return;
            _ambientOpacity = value;
            OnPropertyChanged();
        }
    }
    public string CurrentTimeLabel => TimeSpan.FromSeconds(CurrentPositionSeconds).ToString(@"m\:ss");
    public string DurationLabel => CurrentTrack.Duration.ToString(@"m\:ss");

    public double CurrentPositionSeconds {
        get => _currentPositionSeconds;
        set {
            var nextValue = Math.Clamp(value, 0, CurrentDurationSeconds);
            if (Math.Abs(_currentPositionSeconds - nextValue) < 0.05) return;
            _currentPositionSeconds = nextValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTimeLabel));
            UpdateCurrentLyric();
            if (_currentPositionSeconds >= CurrentDurationSeconds) MoveTrack(1);
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

    public string StatusMessage {
        get => _statusMessage;
        private set {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

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
            OnPropertyChanged(nameof(IsPlayerSelected));
        }
    }

    public bool IsLibrarySelected => SelectedNavigation == "library";
    public bool IsArtistsSelected => SelectedNavigation == "artists";
    public bool IsAlbumsSelected => SelectedNavigation == "albums";
    public bool IsGenresSelected => SelectedNavigation == "genres";
    public bool IsPlaylistsSelected => SelectedNavigation == "playlists";
    public bool IsRecentSelected => SelectedNavigation == "recent";
    public bool IsPlayerSelected => SelectedNavigation == "player";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Navigate(string destination) {
        SelectedNavigation = destination;
        CurrentPage = destination switch {
            "artists" => CreateCategoryPage("Artists", "Find the voices behind your favorite songs.", track => track.Artist),
            "albums" => CreateCategoryPage("Albums", "A good record deserves the whole room.", track => track.Album),
            "genres" => CreateCategoryPage("Genres", "Follow a feeling, wherever it takes you.", track => track.Genre),
            "playlists" => new PlaylistsPageViewModel(Playlists),
            "recent" => new TrackCollectionPageViewModel("Recently played", "A few familiar places you've been.", RecentTracks.ToArray()),
            "player" => new PlayerPageViewModel(),
            _ => new LibraryPageViewModel(Tracks)
        };
    }

    private CategoryBrowsePageViewModel CreateCategoryPage(string title, string subtitle, Func<Music, string> selector) {
        var groups = Tracks
            .GroupBy(selector)
            .Select(group => new CategoryGroupViewModel(
                group.Key,
                $"{group.Count()} {(group.Count() == 1 ? "song" : "songs")}",
                group.First().ArtworkColor,
                group.ToArray()))
            .ToList();
        return new CategoryBrowsePageViewModel(title, subtitle, new ObservableCollection<CategoryGroupViewModel>(groups));
    }

    private void OpenPlaylist(Playlist playlist) {
        SelectedPlaylist = playlist;
        SelectedNavigation = "playlists";
        CurrentPage = new PlaylistDetailPageViewModel(playlist, Tracks);
    }

    private void PlayTrack(Music track) {
        CurrentTrack = track;
        CurrentPositionSeconds = 0;
        IsPlaying = true;
        RecentTracks.Remove(track);
        RecentTracks.Insert(0, track);
        while (RecentTracks.Count > 12) RecentTracks.RemoveAt(RecentTracks.Count - 1);
    }

    private void MoveTrack(int direction) {
        var currentIndex = Tracks.IndexOf(CurrentTrack);
        var nextIndex = (currentIndex + direction + Tracks.Count) % Tracks.Count;
        PlayTrack(Tracks[nextIndex]);
    }

    private void Stop() {
        IsPlaying = false;
        CurrentPositionSeconds = 0;
        StatusMessage = "Playback stopped.";
    }

    private void AddTrackToPlaylist(Music track) {
        var targetPlaylist = SelectedPlaylist ?? Playlists.FirstOrDefault();
        if (targetPlaylist is null) {
            targetPlaylist = new Playlist("My playlist");
            Playlists.Add(targetPlaylist);
        }

        if (!targetPlaylist.Tracks.Contains(track)) targetPlaylist.Tracks.Add(track);
        if (CurrentPage is PlaylistDetailPageViewModel detail && detail.Playlist == targetPlaylist) detail.NotifyTracksChanged();
        StatusMessage = $"Added “{track.Title}” to {targetPlaylist.Name}.";
    }

    private void CreatePlaylist() {
        var name = NewPlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = $"New playlist {Playlists.Count + 1}";
        var playlist = new Playlist(name);
        Playlists.Add(playlist);
        NewPlaylistName = string.Empty;
        OpenPlaylist(playlist);
        StatusMessage = $"Created “{playlist.Name}”.";
    }

    private void DeleteSelectedPlaylist() {
        if (SelectedPlaylist is null) return;
        var deletedName = SelectedPlaylist.Name;
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = null;
        Navigate("playlists");
        StatusMessage = $"Deleted “{deletedName}”.";
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e) {
        if (IsPlaying) CurrentPositionSeconds += 0.25;
        _ambientPhase += 0.08;
        AmbientOpacity = 0.12 + Math.Sin(_ambientPhase) * 0.045;
    }

    private void UpdateCurrentLyric() {
        var activeLine = CurrentLyricIndex;
        for (var index = 0; index < CurrentTrack.Lyrics.Count; index++) {
            CurrentTrack.Lyrics[index].IsCurrent = index == activeLine;
        }

        OnPropertyChanged(nameof(CurrentLyricIndex));
    }

    public int CurrentLyricIndex {
        get {
            for (var index = CurrentTrack.Lyrics.Count - 1; index >= 0; index--) {
                if (CurrentTrack.Lyrics[index].Timestamp.TotalSeconds <= CurrentPositionSeconds) return index;
            }
            return -1;
        }
    }

    private static ObservableCollection<Music> CreateDemoTracks() => [
        new Music {
            Title = "A Walk Through the Rain", Artist = "Mira Sol", Album = "Quiet Cities", Genre = "Indie", Duration = TimeSpan.FromSeconds(228), ArtworkColor = "#7158C8", FilePath = "demo/quiet-cities/01.flac",
            Lyrics = [new(TimeSpan.FromSeconds(0), "The streets are silver after the rain"), new(TimeSpan.FromSeconds(12), "And every window knows my name"), new(TimeSpan.FromSeconds(25), "We take the long way home tonight"), new(TimeSpan.FromSeconds(39), "Under a sky of violet light"), new(TimeSpan.FromSeconds(54), "The quiet has a sound of its own"), new(TimeSpan.FromSeconds(76), "A train is drawing lines in blue"), new(TimeSpan.FromSeconds(101), "The city keeps a light for you"), new(TimeSpan.FromSeconds(129), "We leave our hurried selves behind"), new(TimeSpan.FromSeconds(158), "And let the evening change its mind"), new(TimeSpan.FromSeconds(190), "The rain lets go, the stars come through"), new(TimeSpan.FromSeconds(213), "The long way home leads back to you")]
        },
        new Music {
            Title = "Blue Hour, Again", Artist = "Juniper Vale", Album = "Soft Signals", Genre = "Electronic", Duration = TimeSpan.FromSeconds(194), ArtworkColor = "#317F91", FilePath = "demo/soft-signals/02.flac",
            Lyrics = [new(TimeSpan.FromSeconds(0), "Stay until the blue turns gold"), new(TimeSpan.FromSeconds(14), "Tell me what the night has told"), new(TimeSpan.FromSeconds(28), "Every little light is close"), new(TimeSpan.FromSeconds(43), "Every road becomes a home"), new(TimeSpan.FromSeconds(63), "We can let the hours unwind"), new(TimeSpan.FromSeconds(86), "Leave the louder world behind"), new(TimeSpan.FromSeconds(111), "A quiet signal finds its way"), new(TimeSpan.FromSeconds(138), "And carries us into the day"), new(TimeSpan.FromSeconds(166), "Stay until the blue turns gold"), new(TimeSpan.FromSeconds(184), "There's still a story left untold")]
        },
        new Music {
            Title = "Paper Moons", Artist = "Mira Sol", Album = "Quiet Cities", Genre = "Indie", Duration = TimeSpan.FromSeconds(246), ArtworkColor = "#BF795B", FilePath = "demo/quiet-cities/04.flac",
            Lyrics = [new(TimeSpan.FromSeconds(0), "We made a world from paper moons"), new(TimeSpan.FromSeconds(16), "And hummed along to empty rooms"), new(TimeSpan.FromSeconds(31), "If morning comes, let it come slow"), new(TimeSpan.FromSeconds(46), "There's nowhere else we need to go"), new(TimeSpan.FromSeconds(71), "The ceiling turns to open skies"), new(TimeSpan.FromSeconds(98), "The truth is soft around the sides"), new(TimeSpan.FromSeconds(126), "A little hope, a little tune"), new(TimeSpan.FromSeconds(153), "We disappear into the room"), new(TimeSpan.FromSeconds(187), "We made a world from paper moons"), new(TimeSpan.FromSeconds(218), "And morning waits outside")]
        },
        new Music {
            Title = "Almost Home (feat. Leila)", Artist = "Northbound", Album = "Open Windows", Genre = "Soul", Duration = TimeSpan.FromSeconds(213), ArtworkColor = "#C86A7A", FilePath = "demo/open-windows/03.flac",
            Lyrics = [new(TimeSpan.FromSeconds(0), "I can see the porch light from here"), new(TimeSpan.FromSeconds(15), "The road gets softer when you're near"), new(TimeSpan.FromSeconds(30), "One more mile, one more song"), new(TimeSpan.FromSeconds(45), "I've been almost home all along"), new(TimeSpan.FromSeconds(67), "The windows glow against the dark"), new(TimeSpan.FromSeconds(91), "A small familiar kind of spark"), new(TimeSpan.FromSeconds(116), "The night folds up its coat of blue"), new(TimeSpan.FromSeconds(142), "And every step comes back to you"), new(TimeSpan.FromSeconds(169), "I can see the porch light shine"), new(TimeSpan.FromSeconds(196), "This way home is yours and mine")]
        },
        new Music {
            Title = "Slow Motion", Artist = "Lena Grey", Album = "Little Hours", Genre = "Acoustic", Duration = TimeSpan.FromSeconds(181), ArtworkColor = "#A58856", FilePath = "demo/little-hours/01.flac",
            Lyrics = [new(TimeSpan.FromSeconds(0), "Let the morning take its time"), new(TimeSpan.FromSeconds(13), "Let the dust become the light"), new(TimeSpan.FromSeconds(27), "Nothing here is asking us to run"), new(TimeSpan.FromSeconds(42), "We can move in slow motion"), new(TimeSpan.FromSeconds(62), "The kettle sings a quiet note"), new(TimeSpan.FromSeconds(84), "A little warmth inside my coat"), new(TimeSpan.FromSeconds(108), "The golden hour crosses the floor"), new(TimeSpan.FromSeconds(133), "We don't need anything more"), new(TimeSpan.FromSeconds(155), "Let the morning take its time"), new(TimeSpan.FromSeconds(172), "Let this little light be mine")]
        },
        new Music {
            Title = "Under the Same Sky", Artist = "Juniper Vale", Album = "Soft Signals", Genre = "Electronic", Duration = TimeSpan.FromSeconds(205), ArtworkColor = "#5F8A75", FilePath = "demo/soft-signals/05.flac",
            Lyrics = [new(TimeSpan.FromSeconds(0), "Somewhere there the day begins"), new(TimeSpan.FromSeconds(14), "Somewhere here the night folds in"), new(TimeSpan.FromSeconds(29), "Different clocks, a single sky"), new(TimeSpan.FromSeconds(44), "You and I are passing by"), new(TimeSpan.FromSeconds(66), "The same wind moves the trees"), new(TimeSpan.FromSeconds(89), "Across a thousand quiet seas"), new(TimeSpan.FromSeconds(112), "No matter where the hours lie"), new(TimeSpan.FromSeconds(137), "We're looking at the same sky"), new(TimeSpan.FromSeconds(163), "Somewhere there the day begins"), new(TimeSpan.FromSeconds(188), "Somewhere here the night folds in")]
        },
        new Music {
            Title = "Still Water", Artist = "Lena Grey", Album = "Little Hours", Genre = "Ambient", Duration = TimeSpan.FromSeconds(272), ArtworkColor = "#477D89", FilePath = "demo/little-hours/06.flac"
        }
    ];

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() {
        _playbackTimer.Stop();
        _playbackTimer.Tick -= PlaybackTimer_Tick;
    }
}
