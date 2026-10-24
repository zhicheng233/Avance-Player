using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Avance_Player.Converters;

/// <summary>把 0..1 的均衡器档位换成 56px 带宽里的像素高度。</summary>
public sealed class LevelHeightConverter : IValueConverter {
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var level = value is double number ? number : 0.5;
        return 8 + Math.Clamp(level, 0, 1) * 40;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
