using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avance_Player.Models;

namespace Avance_Player.ViewModels;

/// <summary>
/// 歌曲列表页顶部的搜索过滤。按"标题 / 歌手 / 专辑"做不区分大小写的包含匹配，
/// 结果放在 <see cref="Tracks"/> 里给列表绑定，源集合本身不动。
/// </summary>
public sealed class SongSearchViewModel : INotifyPropertyChanged {
    private readonly ObservableCollection<Music> _source;
    private string _searchText = string.Empty;

    public SongSearchViewModel(ObservableCollection<Music> source) {
        _source = source;
        _source.CollectionChanged += Source_CollectionChanged;
        ResetFiltered();
    }

    /// <summary>过滤后的歌曲列表；搜索词为空时就是源集合的全部内容。</summary>
    public ObservableCollection<Music> Tracks { get; } = [];

    public string SearchText {
        get => _searchText;
        set {
            var normalized = value ?? string.Empty;
            if (_searchText == normalized) return;
            _searchText = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(IsEmpty));
            ResetFiltered();
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>源集合为空才算空；搜索没有结果时要单独提示，不能和"库是空的"混为一谈。</summary>
    public bool IsEmpty => _source.Count == 0;

    public bool HasNoResults => HasSearchText && Tracks.Count == 0 && _source.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        // 歌曲被导入 / 移除 / 重排时整份重算，列表规模不大，没必要做增量。
        ResetFiltered();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoResults));
    }

    private void ResetFiltered() {
        var matches = Matches(_source, _searchText);
        Tracks.Clear();
        foreach (var track in matches) Tracks.Add(track);
        OnPropertyChanged(nameof(HasNoResults));
    }

    private static IEnumerable<Music> Matches(IEnumerable<Music> source, string query) {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return source;
        return source.Where(track => Contains(track.Title, trimmed)
                                    || Contains(track.Artist, trimmed)
                                    || Contains(track.Album, trimmed));
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
