using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avance_Player.Models;

namespace Avance_Player.ViewModels;

public abstract class PageViewModel(string title, string subtitle) {
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}

public sealed class LibraryPageViewModel(ObservableCollection<Music> tracks)
    : PageViewModel("Your library", "Everything you love, in one place.") {
    public ObservableCollection<Music> Tracks { get; } = tracks;
    public SongSearchViewModel Search { get; } = new(tracks);
}

public sealed class CategoryBrowsePageViewModel(string title, string subtitle, ObservableCollection<CategoryGroupViewModel> groups)
    : PageViewModel(title, subtitle) {
    public ObservableCollection<CategoryGroupViewModel> Groups { get; } = groups;
    public bool IsEmpty => Groups.Count == 0;
}

public sealed class CategoryGroupViewModel(string title, string subtitle, string artworkColor, IReadOnlyList<Music> tracks) {
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string ArtworkColor { get; } = artworkColor;
    public IReadOnlyList<Music> Tracks { get; } = tracks;
}

public sealed class TrackCollectionPageViewModel(string title, string subtitle, IReadOnlyList<Music> tracks)
    : PageViewModel(title, subtitle) {
    public IReadOnlyList<Music> Tracks { get; } = tracks;
    public bool IsEmpty => Tracks.Count == 0;

    // 合集页的列表是快照，过滤后单独维护一份可观察副本供搜索使用。
    public SongSearchViewModel Search { get; } = new(new ObservableCollection<Music>(tracks));
}

public sealed class PlaylistsPageViewModel(ObservableCollection<Playlist> playlists)
    : PageViewModel("Playlists", "A little order for all the feelings.") {
    public ObservableCollection<Playlist> Playlists { get; } = playlists;
    public bool IsEmpty => Playlists.Count == 0;
}

public sealed class PlaylistDetailPageViewModel(Playlist playlist, ObservableCollection<Music> library)
    : PageViewModel(playlist.Name, "Your collection, your rules."), INotifyPropertyChanged {
    public Playlist Playlist { get; } = playlist;
    public ObservableCollection<Music> Library { get; } = library;
    public bool IsEmpty => Playlist.Tracks.Count == 0;

    /// <summary>歌单页的两段列表各自可搜：歌单内容和可加入的曲库。</summary>
    public SongSearchViewModel Search { get; } = new(playlist.Tracks);
    public SongSearchViewModel LibrarySearch { get; } = new(library);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyTracksChanged() => OnPropertyChanged(nameof(IsEmpty));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
