using System.Windows;

namespace WifiSignalOptimizer.Dialogs;

/// <summary>
/// dialog for entering real-world distance for calibration
/// </summary>
public partial class CalibrationDialog : Window
{
    public double ResultMeters { get; private set; }

    public CalibrationDialog()
    {
        InitializeComponent();
        MetersInput.Focus();
        MetersInput.SelectAll();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(MetersInput.Text, out double meters) && meters > 0)
        {
            ResultMeters = meters;
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("Te rog să introduci o valoare validă mai mare decât 0.",
                "Valoare invalidă", MessageBoxButton.OK, MessageBoxImage.Warning);
            MetersInput.Focus();
            MetersInput.SelectAll();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OK_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            Cancel_Click(this, new RoutedEventArgs());
        }
    }
}