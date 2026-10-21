using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.ViewModels;

/// <summary>
/// A selectable label in a playback-settings popover. Used for sleep-timer choices and for the
/// short option rows in the floating console, keeping the UI bilingual without a combo box.
/// </summary>
public sealed class PlaybackOptionViewModel(string label, int index) : INotifyPropertyChanged {
    private string _label = label;
    private bool _isSelected;

    public string Label {
        get => _label;
        set {
            if (_label == value) return;
            _label = value;
            OnPropertyChanged();
        }
    }

    public int Index { get; } = index;

    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
