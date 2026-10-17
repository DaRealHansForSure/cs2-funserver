using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Cs2ServerControl.LiveMap;

/// <summary>Turns 0..100 into a star column width, for bars without a fixed width.</summary>
public sealed class PercentToStarConverter : MarkupExtension, IValueConverter {
    /// <summary>True for the empty part of the bar (100 minus the value).</summary>
    public bool Remainder { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        double percent = value is double number ? Math.Clamp(number, 0, 100) : 0;
        double share = Remainder ? 100 - percent : percent;
        return new GridLength(share, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }

    public override object ProvideValue(IServiceProvider serviceProvider) {
        return this;
    }
}

