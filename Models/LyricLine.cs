using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.Models;

public sealed class LyricLine(TimeSpan timestamp, string text) : INotifyPropertyChanged {
    private bool _isCurrent;

    public TimeSpan Timestamp { get; } = timestamp;
    public string Text { get; } = text;

    public bool IsCurrent {
        get => _isCurrent;
        set {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
