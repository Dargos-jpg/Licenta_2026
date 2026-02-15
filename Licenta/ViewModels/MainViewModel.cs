using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using WifiSignalOptimizer.Models;
using WifiSignalOptimizer.Services;

namespace WifiSignalOptimizer.ViewModels;

/// <summary>
/// Main ViewModel for the application following strict MVVM pattern
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IWifiScannerService _wifiScanner;
    private readonly IMapService _mapService;
    private readonly List<SurveyPoint> _surveyPoints = [];

    [ObservableProperty]
    private ObservableCollection<WifiNetwork> _visibleNetworks = [];

    [ObservableProperty]
    private BitmapImage? _floorPlan;

    [ObservableProperty]
    private ImageSource? _heatmapOverlay;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "Gata. Încărcați un plan pentru a începe.";

    [ObservableProperty]
    private int _surveyPointCount;

    public MainViewModel(IWifiScannerService wifiScanner, IMapService mapService)
    {
        _wifiScanner = wifiScanner;
        _mapService = mapService;
    }

    /// <summary>
    /// Opens file dialog and loads a floor plan image
    /// </summary>
    [RelayCommand]
    private async Task LoadMapAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selectați Planul Clădirii",
            Filter = "Fișiere Imagine|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Toate Fișierele|*.*",
            FilterIndex = 0
        };

        if (dialog.ShowDialog() == true)
        {
            StatusMessage = "Se încarcă planul...";

            var bitmap = await _mapService.LoadFloorPlanAsync(dialog.FileName);

            if (bitmap != null)
            {
                FloorPlan = bitmap;
                StatusMessage = $"Plan încărcat: {Path.GetFileName(dialog.FileName)}";

                // Clear heatmap when new map is loaded
                HeatmapOverlay = null;
            }
            else
            {
                StatusMessage = "Eroare la încărcarea planului. Încercați alt fișier.";
            }
        }
    }

    /// <summary>
    /// Performs a WiFi scan and updates the visible networks
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsScanning = true;
            StatusMessage = "Se scanează rețelele WiFi...";

            var networks = await _wifiScanner.ScanNetworksAsync();

            if (cancellationToken.IsCancellationRequested)
                return;

            VisibleNetworks.Clear();
            foreach (var network in networks)
            {
                VisibleNetworks.Add(network);
            }

            StatusMessage = $"Scanare completă. {networks.Count} rețele găsite.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Eroare la scanare: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Records a survey point at the specified map coordinates
    /// </summary>
    [RelayCommand]
    private void RecordPoint(object parameter)
    {
        if (parameter is not System.Windows.Point point)
            return;

        if (VisibleNetworks.Count == 0)
        {
            StatusMessage = "Nu există date WiFi. Scanați mai întâi.";
            return;
        }

        var surveyPoint = new SurveyPoint
        {
            X = point.X,
            Y = point.Y,
            DetectedNetworks = [.. VisibleNetworks],
            Timestamp = DateTime.UtcNow
        };

        _surveyPoints.Add(surveyPoint);
        SurveyPointCount = _surveyPoints.Count;

        StatusMessage = $"Punctul {_surveyPoints.Count} înregistrat la ({point.X:F0}, {point.Y:F0}) " +
                       $"- {VisibleNetworks.Count} rețele detectate";
    }

    /// <summary>
    /// Generates a heatmap overlay based on recorded survey points
    /// </summary>
    [RelayCommand]
    private async Task GenerateHeatmapAsync()
    {
        if (_surveyPoints.Count < 2)
        {
            StatusMessage = "Necesare cel puțin 2 puncte pentru generarea heatmap-ului.";
            return;
        }

        if (FloorPlan == null)
        {
            StatusMessage = "Încărcați mai întâi un plan.";
            return;
        }

        try
        {
            StatusMessage = "Se generează heatmap-ul...";

            // Get dimensions from floor plan
            int width = FloorPlan.PixelWidth;
            int height = FloorPlan.PixelHeight;

            // Run on background thread to avoid UI freeze
            var heatmap = await Task.Run(() =>
            {
                var service = new HeatmapService();
                return service.GenerateHeatmap(_surveyPoints, width, height);
            });

            // Freeze the bitmap for cross-thread access
            if (heatmap != null && heatmap.CanFreeze)
            {
                heatmap.Freeze();
            }

            HeatmapOverlay = heatmap;
            StatusMessage = $"Heatmap generat cu succes din {_surveyPoints.Count} puncte.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Eroare la generarea heatmap-ului: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets all recorded survey points (for future AI analysis)
    /// </summary>
    public IReadOnlyList<SurveyPoint> GetSurveyPoints() => _surveyPoints.AsReadOnly();

    /// <summary>
    /// Clears all survey points
    /// </summary>
    [RelayCommand]
    private void ClearSurveyPoints()
    {
        _surveyPoints.Clear();
        SurveyPointCount = 0;
        HeatmapOverlay = null; // Clear heatmap when points are cleared
        StatusMessage = "Toate punctele au fost șterse.";

        // Send message to clear visual elements from Canvas
        WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
    }
}

/// <summary>
/// Message to signal that survey points should be cleared from UI
/// </summary>
public sealed class ClearSurveyPointsMessage
{
}