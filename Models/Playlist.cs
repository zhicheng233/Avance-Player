using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.Models;

public sealed class Playlist(string name) : INotifyPropertyChanged {
    private string _name = name;

    public ObservableCollection<Music> Tracks { get; } = [];

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
