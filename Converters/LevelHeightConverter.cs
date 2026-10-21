using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Avance_Player.Converters;

/// <summary>Maps a 0..1 equalizer level to a pixel height inside a 56 px band area.</summary>
public sealed class LevelHeightConverter : IValueConverter {
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var level = value is double number ? number : 0.5;
        return 8 + Math.Clamp(level, 0, 1) * 40;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
