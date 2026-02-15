using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private readonly ProjectService _projectService;
    private readonly DispatcherTimer _autoScanTimer;
    private readonly List<SurveyPoint> _surveyPoints = [];
    private readonly List<MapElement> _mapElements = [];

    [ObservableProperty]
    private ObservableCollection<WifiNetwork> _visibleNetworks = [];

    [ObservableProperty]
    private BitmapImage? _floorPlan;

    [ObservableProperty]
    private ImageSource? _heatmapOverlay;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isAutoScanning;

    [ObservableProperty]
    private string _statusMessage = "Gata. Încărcați un plan pentru a începe.";

    [ObservableProperty]
    private int _surveyPointCount;

    public string AutoScanButtonText => IsAutoScanning ? "⏸️ Stop Auto-Scan" : "▶️ Start Auto-Scan";

    public MainViewModel(IWifiScannerService wifiScanner, IMapService mapService)
    {
        _wifiScanner = wifiScanner;
        _mapService = mapService;
        _projectService = new ProjectService();

        _autoScanTimer = new DispatcherTimer();
        _autoScanTimer.Interval = TimeSpan.FromSeconds(3);
        _autoScanTimer.Tick += AutoScanTimer_Tick;
    }

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
                HeatmapOverlay = null;
            }
            else
            {
                StatusMessage = "Eroare la încărcarea planului. Încercați alt fișier.";
            }
        }
    }

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

    [RelayCommand]
    private void ToggleAutoScan()
    {
        if (IsAutoScanning)
        {
            _autoScanTimer.Stop();
            IsAutoScanning = false;
            StatusMessage = "Auto-Scan oprit.";
        }
        else
        {
            _autoScanTimer.Start();
            IsAutoScanning = true;
            StatusMessage = "Auto-Scan pornit. Scanare la fiecare 3 secunde.";
            _ = ScanAsync(CancellationToken.None);
        }

        OnPropertyChanged(nameof(AutoScanButtonText));
    }

    private void AutoScanTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsScanning)
        {
            _ = ScanAsync(CancellationToken.None);
        }
    }

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
    /// Registers a map element (router, fridge, etc.) for AI analysis
    /// </summary>
    [RelayCommand]
    private void RegisterMapElement(object parameter)
    {
        if (parameter is not MapElement element)
            return;

        _mapElements.Add(element);

        string elementName = element.Type switch
        {
            ElementType.Router => "Router",
            ElementType.Fridge => "Frigider",
            ElementType.Microwave => "Microunde",
            ElementType.ConcreteWall => "Zid Beton",
            _ => "Obiect"
        };

        StatusMessage = $"{elementName} adăugat la ({element.X:F0}, {element.Y:F0}) " +
                       $"- Penalizare semnal: {element.SignalPenalty} dBm";
    }

    /// <summary>
    /// Gets all registered map elements (for AI prediction)
    /// </summary>
    public IReadOnlyList<MapElement> GetMapElements() => _mapElements.AsReadOnly();

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

            int width = FloorPlan.PixelWidth;
            int height = FloorPlan.PixelHeight;

            // ✅ CORECTAT: Folosim semnătura corectă (3 parametri)
            var heatmap = await Task.Run(() =>
            {
                var service = new HeatmapService();
                return service.GenerateHeatmap(_surveyPoints, width, height);
            });

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

    [RelayCommand]
    private void SaveProject()
    {
        if (_surveyPoints.Count == 0)
        {
            MessageBox.Show("Nu ai nicio măsurătoare de salvat!", "Informație", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // ✅ CORECTAT: Folosim semnătura corectă (1 parametru - doar points)
            _projectService.SaveProject(_surveyPoints);
            StatusMessage = $"Proiect salvat cu succes! ({_surveyPoints.Count} puncte)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Eroare la salvarea proiectului: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadProjectAsync()
    {
        try
        {
            // ✅ CORECTAT: Folosim semnătura corectă (returnează List<SurveyPoint>?)
            var loadedPoints = _projectService.LoadProject();
            
            if (loadedPoints != null && loadedPoints.Count > 0)
            {
                _surveyPoints.Clear();
                _mapElements.Clear(); // Clear map elements too
                
                foreach (var point in loadedPoints)
                {
                    _surveyPoints.Add(point);
                }
                
                SurveyPointCount = _surveyPoints.Count;
                StatusMessage = $"Proiect încărcat cu succes! ({_surveyPoints.Count} puncte)";

                WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
                WeakReferenceMessenger.Default.Send(new RedrawSurveyPointsMessage(_surveyPoints));

                if (FloorPlan != null && _surveyPoints.Count >= 2)
                {
                    await GenerateHeatmapAsync();
                }
            }
            else
            {
                StatusMessage = "Nu s-au găsit date în proiectul încărcat.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Eroare la încărcarea proiectului: {ex.Message}";
        }
    }

    public IReadOnlyList<SurveyPoint> GetSurveyPoints() => _surveyPoints.AsReadOnly();

    [RelayCommand]
    private void ClearSurveyPoints()
    {
        _surveyPoints.Clear();
        _mapElements.Clear();
        SurveyPointCount = 0;
        HeatmapOverlay = null;
        StatusMessage = "Toate punctele și obiectele au fost șterse.";

        WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
    }
}

public sealed class ClearSurveyPointsMessage
{
}

public sealed class RedrawSurveyPointsMessage
{
    public IReadOnlyList<SurveyPoint> SurveyPoints { get; }
    
    public RedrawSurveyPointsMessage(IReadOnlyList<SurveyPoint> surveyPoints)
    {
        SurveyPoints = surveyPoints;
    }
}