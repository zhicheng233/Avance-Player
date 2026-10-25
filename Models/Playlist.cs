using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.Models;

public sealed class Playlist(string name) : INotifyPropertyChanged {
    private string _name = name;

    public ObservableCollection<Music> Tracks { get; } = [];

    /// <summary>
    /// 导入到这个歌单的文件夹根目录。每个根目录只在导入时记一次；
    /// 文件夹里的增删只会刷新由它带来的条目，不会碰用户单独加进来的歌。
    /// </summary>
    public List<string> FolderRoots { get; } = [];

    public string Name {
        get => _name;
        set {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
