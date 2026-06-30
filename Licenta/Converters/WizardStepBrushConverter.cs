using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WifiSignalOptimizer.ViewModels;

namespace WifiSignalOptimizer.Converters;

/// <summary>
/// returns a blue brush for completed/active steps and grey for upcoming steps.
/// ConverterParameter = step index (0-based string).
/// </summary>
public sealed class WizardStepBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush   = new(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush CurrentBrush  = new(Color.FromRgb(147, 197, 253));
    private static readonly SolidColorBrush PendingBrush  = new(Color.FromArgb(50, 100, 116, 139));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not WizardStep currentStep) return PendingBrush;
        if (!int.TryParse(parameter?.ToString(), out int segIndex)) return PendingBrush;

        int current = (int)currentStep;
        if (segIndex < current)  return ActiveBrush;
        if (segIndex == current) return CurrentBrush;
        return PendingBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
