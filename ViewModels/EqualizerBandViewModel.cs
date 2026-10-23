using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.ViewModels;

/// <summary>
/// One normalized equalizer band control. The LibVLC backend maps the five displayed levels onto
/// its native ten-band equalizer; the simulated demo backend ignores them because it has no audio.
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
