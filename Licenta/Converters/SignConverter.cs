using System;
using System.Globalization;
using System.Windows.Data;

namespace WifiSignalOptimizer.Converters
{
    /// <summary>
    /// Convertește un double în semnul său: -1 (negativ), 0 (zero), 1 (pozitiv).
    /// Folosit pentru DataTrigger pe culoarea valorilor delta (roșu/verde).
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
