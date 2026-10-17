using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cs2ServerControl.Services;

/// <summary>Visible when the bound bool is false.</summary>
public sealed class InverseVisibilityConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}

/// <summary>Visible when the bound number is zero, for empty state hints.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        return value is 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}
