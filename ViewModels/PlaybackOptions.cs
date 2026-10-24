using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.ViewModels;

/// <summary>
/// 播放设置弹层里一个可选标签。定时关闭的选项和悬浮控制台里那几行短选项都用它，
/// UI 双语，不用 combo box。
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
