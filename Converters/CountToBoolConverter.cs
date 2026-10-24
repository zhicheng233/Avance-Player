using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Avance_Player.Converters;

/// <summary>
/// 把集合数量转成能直接绑可见性的 bool。集合为空又想显示空状态，就把 <see cref="Invert"/> 设上。
/// </summary>
public sealed class CountToBoolConverter : IValueConverter {
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        var count = value is int number ? number : 0;
        return Invert ? count == 0 : count > 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
