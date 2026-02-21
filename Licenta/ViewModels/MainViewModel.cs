using System.Collections.ObjectModel;
using System.IO;
using System.Text;
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
    private WifiNetwork? _selectedNetwork;

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

    [ObservableProperty]
    private double _pixelsPerMeter = 50.0;

    [ObservableProperty]
    private bool _isCalibrated = false;

    // Modul de afișare: false = Basic (utilizatori de rând), true = Expert (tehnicieni)
    [ObservableProperty]
    private bool _isExpertMode = false;

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

            // Salvăm rețeaua curentă ca să nu dispară selecția la refresh
            string? savedSsid = SelectedNetwork?.SSID;

            var networks = await _wifiScanner.ScanNetworksAsync();

            if (cancellationToken.IsCancellationRequested)
                return;

            VisibleNetworks.Clear();
            foreach (var network in networks)
                VisibleNetworks.Add(network);

            // Re-selectăm rețeaua după refresh
            if (!string.IsNullOrEmpty(savedSsid))
                SelectedNetwork = VisibleNetworks.FirstOrDefault(n => n.SSID == savedSsid);

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

    [RelayCommand]
    private void AnalyzeNetwork()
    {
        if (SelectedNetwork == null)
        {
            MessageBox.Show("Te rog să selectezi o rețea din tabelul din stânga pentru a o analiza!",
                            "Atenție", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            StatusMessage = "Analizăm spectrul radio folosind Machine Learning (K-Means)...";

            var doctor = new WifiDoctorService();
            var report = doctor.AnalyzeNetwork(SelectedNetwork, VisibleNetworks.ToList().AsReadOnly());

            var sb = new StringBuilder();
            sb.AppendLine($"🧠 RAPORT DE SĂNĂTATE AI (ML.NET) - {report.TargetSSID}");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine($"⭐ Scor General: {report.OverallHealthScore:F1} / 100");
            sb.AppendLine($"📡 Canal Curent: {report.CurrentChannel} | 🏆 Canal Optim Recomandat: {report.BestChannel}");
            sb.AppendLine("--------------------------------------------------\n");

            sb.AppendLine("📊 ANALIZĂ CANALE (Clustering K-Means):");

            var activeChannels = report.ChannelDetails
                                       .Where(c => c.InterferenceScore > 0)
                                       .OrderByDescending(c => c.InterferenceScore)
                                       .Take(5);

            foreach (var ch in activeChannels)
            {
                sb.AppendLine($"   • Canalul {ch.Channel} -> {ch.Status} (Stres: {ch.InterferenceScore:F0} | Vecini pe canal: {ch.CoChannelNeighbors})");
            }

            sb.AppendLine("\n💡 RECOMANDĂRI:");
            if (report.ActionableInsights.Count == 0)
            {
                sb.AppendLine("   ➤ Totul arată perfect! Nu este nevoie de nicio optimizare.");
            }
            else
            {
                foreach (var insight in report.ActionableInsights)
                {
                    sb.AppendLine($"   ➤ {insight}");
                }
            }

            MessageBoxImage icon = report.OverallHealthScore >= 80 ? MessageBoxImage.Information :
                                   report.OverallHealthScore >= 50 ? MessageBoxImage.Warning :
                                   MessageBoxImage.Error;

            StatusMessage = "Analiză completă.";
            MessageBox.Show(sb.ToString(), "Doctor Wi-Fi (Sistem AI)", MessageBoxButton.OK, icon);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"A apărut o eroare la rularea modelului ML.NET: {ex.Message}",
                            "Eroare AI", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Eroare la analiza AI.";
        }
    }

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

    public IReadOnlyList<MapElement> GetMapElements() => _mapElements.AsReadOnly();

    [RelayCommand]
    private void GenerateHeatmap()
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

        if (SelectedNetwork == null)
        {
            MessageBox.Show("Te rog selectează o rețea din listă pentru a-i vedea acoperirea!",
                            "Atenție", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusMessage = "Generare anulată. Nicio rețea selectată.";
            return;
        }

        StatusMessage = $"Se generează heatmap-ul pentru {SelectedNetwork.SSID}...";

        int width = FloorPlan.PixelWidth;
        int height = FloorPlan.PixelHeight;
        double scara = IsCalibrated ? PixelsPerMeter : 50.0;

        WeakReferenceMessenger.Default.Send(
            new RequestHeatmapGenerationMessage(width, height, scara, SelectedNetwork.SSID));
    }

    // Butonul "Salvare" din panou trimite un mesaj către MainWindow
    // care colectează schița curentă și apelează SaveProjectWithSketch
    [RelayCommand]
    private void SaveProject()
    {
        // Cerem MainWindow să colecteze schița și să declanșeze salvarea completă
        WeakReferenceMessenger.Default.Send(new RequestSaveWithSketchMessage());
    }

    /// <summary>
    /// Salvare completă: apelat din MainWindow când există schiță de inclus.
    /// </summary>
    public void SaveProjectWithSketch(
        System.Windows.Ink.StrokeCollection? sketchStrokes,
        System.Windows.Media.Imaging.BitmapSource? sketchPng)
    {
        if (_surveyPoints.Count == 0 && sketchStrokes == null)
        {
            MessageBox.Show("Nu ai nicio măsurătoare sau schiță de salvat!", "Informație",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _projectService.SaveProject(
                _surveyPoints.ToList(),
                _mapElements.ToList(),
                PixelsPerMeter,
                IsCalibrated,
                sketchStrokes,
                sketchPng);

            string calibrareInfo = IsCalibrated ? $" | Scară: {PixelsPerMeter:F1} px/m" : "";
            StatusMessage = $"Proiect salvat! ({_surveyPoints.Count} puncte, " +
                            $"{_mapElements.Count} obstacole{calibrareInfo})";
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
            var (projectData, strokes) = _projectService.LoadProject();

            if (projectData != null)
            {
                _surveyPoints.Clear();
                _mapElements.Clear();

                foreach (var point in projectData.SurveyPoints)
                    _surveyPoints.Add(point);

                foreach (var element in projectData.MapElements)
                    _mapElements.Add(element);

                PixelsPerMeter = projectData.PixelsPerMeter;
                IsCalibrated = projectData.IsCalibrated;
                SurveyPointCount = _surveyPoints.Count;

                string calibrareInfo = IsCalibrated
                    ? $" | Scară: {PixelsPerMeter:F1} px/m"
                    : " | Fără calibrare";

                StatusMessage = $"Proiect încărcat! ({_surveyPoints.Count} puncte, " +
                                $"{_mapElements.Count} obstacole{calibrareInfo})";

                WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
                WeakReferenceMessenger.Default.Send(new RedrawSurveyPointsMessage(_surveyPoints));

                if (_mapElements.Count > 0)
                    WeakReferenceMessenger.Default.Send(new RedrawMapElementsMessage(_mapElements));

                // Dacă există schiță vectorială, o restaurăm pe EditorCanvas
                if (strokes != null)
                    WeakReferenceMessenger.Default.Send(new RestoreSketchMessage(strokes));

                // Dacă există PNG schiță salvat, îl restaurăm ca FloorPlan via MapService
                if (!string.IsNullOrEmpty(projectData.SketchPngBase64) && FloorPlan == null)
                {
                    var restoredPlan = await _mapService.LoadFromBase64Async(projectData.SketchPngBase64);
                    if (restoredPlan != null)
                        FloorPlan = restoredPlan;
                }

                if (FloorPlan != null && _surveyPoints.Count >= 2)
                    GenerateHeatmap();
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
    private async Task CalculateOptimalRouterAsync()
    {
        if (FloorPlan == null)
        {
            MessageBox.Show("Încarcă o hartă mai întâi!", "Eroare",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusMessage = "Calculăm poziția optimă folosind algoritmul Min-Max...";

        var elements = _mapElements.ToList();
        var surveys = _surveyPoints.ToList();
        double scara = IsCalibrated ? PixelsPerMeter : 50.0;
        double width = FloorPlan.PixelWidth;
        double height = FloorPlan.PixelHeight;

        // Rulăm algoritmul Min-Max pe background thread (poate dura 1-2 sec)
        Point optimalPosition = await Task.Run(() =>
        {
            var optimizer = new RouterOptimizationService();
            return optimizer.FindOptimalPosition(elements, surveys, width, height, scara);
        });

        StatusMessage = $"✅ Poziție optimă: X={optimalPosition.X:F0}, Y={optimalPosition.Y:F0} " +
                        $"— semnal uniform garantat în toată casa.";

        WeakReferenceMessenger.Default.Send(new ShowOptimalRouterMessage(optimalPosition));
    }

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

public sealed class RedrawMapElementsMessage
{
    public IReadOnlyList<MapElement> MapElements { get; }

    public RedrawMapElementsMessage(IReadOnlyList<MapElement> mapElements)
    {
        MapElements = mapElements;
    }
}

public sealed class RestoreSketchMessage
{
    public System.Windows.Ink.StrokeCollection Strokes { get; }

    public RestoreSketchMessage(System.Windows.Ink.StrokeCollection strokes)
    {
        Strokes = strokes;
    }
}

/// <summary>
/// ViewModel trimite acest mesaj când vrea să salveze.
/// MainWindow îl primește, colectează datele schiței și apelează SaveProjectWithSketch.
/// </summary>
public sealed class RequestSaveWithSketchMessage
{
}

public sealed class ShowOptimalRouterMessage
{
    public Point OptimalPoint { get; }
    public ShowOptimalRouterMessage(Point point) { OptimalPoint = point; }
}

public sealed class RequestHeatmapGenerationMessage
{
    public int Width { get; }
    public int Height { get; }
    public double Scale { get; }
    public string TargetSSID { get; }

    public RequestHeatmapGenerationMessage(int w, int h, double scale, string targetSsid)
    {
        Width = w; Height = h; Scale = scale; TargetSSID = targetSsid;
    }
}