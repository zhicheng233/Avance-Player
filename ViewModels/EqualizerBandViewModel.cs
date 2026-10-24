using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.ViewModels;

/// <summary>
/// 一个归一化的均衡器频段。LibVLC 后端把 UI 上五个档位映射到原生十段；
/// 模拟后端没音频，直接忽略。
/// </summary>
public sealed class EqualizerBandViewModel(string frequency, double level) : INotifyPropertyChanged {
    private double _level = level;

    public string Frequency { get; } = frequency;

    public double Level {
        get => _level;
        set {
            if (System.Math.Abs(_level - value) < 0.001) return;
            _level = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
