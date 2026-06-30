using System;
using System.Globalization;
using System.Windows.Data;

namespace WifiSignalOptimizer.Converters
{
    /// <summary>
    /// converts a double to its sign: -1 (negative), 0 (zero), 1 (positive).
    /// used for the DataTrigger on the color of delta values (red/green).
    /// </summary>
    public sealed class SignConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d)
                return d < -0.05 ? -1 : d > 0.05 ? 1 : 0;
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
