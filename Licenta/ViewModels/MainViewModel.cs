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
/// Modul de vizualizare al zonei centrale.
/// </summary>
public enum CentralViewMode { Normal, Optimized, Doctor }

public enum WizardStep
{
    ScanWifi        = 0,
    LoadPlan        = 1,
    Calibrate       = 2,   // drag 2 puncte → câți metri (Ekahau-style)
    DrawWalls       = 3,   // opțional: desenează pereți/obstacole
    MarkLocations   = 4,
    GenerateHeatmap = 5,
    OptimizeRouter  = 6,
    Done            = 7
}

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

    // === CONTINUOUS SURVEY — Signal Tracking ===
    private readonly Dictionary<string, Queue<int>> _signalHistory = new();
    private readonly Dictionary<string, double> _signalBaselines = new();
    private const int SignalHistorySize      = 10;
    private const int BaselineSampleCount    = 3;
    private const int SignalDropAlertThresholdDbm = 10;

    // === UNDO / REDO ===
    private readonly Stack<(Action Undo, Action Redo)> _undoStack = new();
    private readonly Stack<(Action Undo, Action Redo)> _redoStack = new();

    [ObservableProperty]
    private ObservableCollection<WifiNetwork> _visibleNetworks = [];

    [ObservableProperty]
    private WifiNetwork? _selectedNetwork;

    [ObservableProperty]
    private BitmapImage? _floorPlan;

    public bool HasFloorPlan => FloorPlan != null;
    public bool ShowEmptyPlaceholder => FloorPlan == null && !IsGridCanvasActive;

    [ObservableProperty]
    private ImageSource? _heatmapOverlay;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isAutoScanning;

    // Trend semnal rețea selectată (ex: "↑ +3 dBm", "↓ -8 dBm", "— stabil")
    [ObservableProperty]
    private string _selectedNetworkTrend = "";

    // Alertă drop semnal față de baseline
    [ObservableProperty]
    private bool _isSignalAlert = false;

    [ObservableProperty]
    private string _signalAlertMessage = "";

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

    // Wizardul poate fi închis cu X fără a schimba modul Basic/Expert
    [ObservableProperty]
    private bool _isWizardMinimized = false;

    // === WIZARD (Basic Mode) ===
    [ObservableProperty]
    private WizardStep _currentWizardStep = WizardStep.ScanWifi;

    public bool IsWizardVisible => !IsExpertMode && !IsWizardMinimized && CurrentWizardStep != WizardStep.Done;

    public string WizardStepNumber => $"Pas {(int)CurrentWizardStep + 1} din 7";
    public double WizardProgress   => (int)CurrentWizardStep / 7.0 * 100.0;

    public string WizardTitle => CurrentWizardStep switch
    {
        WizardStep.ScanWifi        => "Scanează rețelele WiFi",
        WizardStep.LoadPlan        => "Încarcă planul casei",
        WizardStep.Calibrate       => "Calibrează scara planului",
        WizardStep.DrawWalls       => "Adaugă pereți și obstacole",
        WizardStep.MarkLocations   => "Marchează locurile de măsurare",
        WizardStep.GenerateHeatmap => "Generează harta de semnal",
        WizardStep.OptimizeRouter  => "Optimizează poziția routerului",
        _                          => "Finalizat!"
    };

    public string WizardDescription => CurrentWizardStep switch
    {
        WizardStep.ScanWifi        => "Apasă butonul de mai jos pentru a detecta rețelele WiFi din jur.",
        WizardStep.LoadPlan        => "Alege o imagine cu planul apartamentului sau casei tale.",
        WizardStep.Calibrate       => IsCalibrated
            ? $"Calibrare reusita! Scara: {PixelsPerMeter:F0} px/m. Apasa Continua."
            : "Apasa Calibreaza, apoi trage o linie pe harta intre 2 puncte cu distanta cunoscuta (ex: un perete de 4m).",
        WizardStep.DrawWalls       => "Deseneaza peretii si obstacolele. Ai doua optiuni in bara laterala: 'Deseneaza Pereti' (trasezi peste planul incarcat) sau 'Plan Grila Nou' (canvas alb, de la zero). Apasa Continua cand esti gata.",
        WizardStep.MarkLocations   => $"Plimba-te prin casa si apasa pe harta in fiecare camera ({SurveyPointCount} marcate — minim 2 necesare).",
        WizardStep.GenerateHeatmap => "Ai suficiente puncte! Genereaza harta de acoperire WiFi acum.",
        WizardStep.OptimizeRouter  => "Harta e gata. Apasa pentru a gasi pozitia optima a routerului cu ajutorul AI.",
        _                          => "Felicitari! Urmeaza recomandarea pentru acoperire maxima in toata casa."
    };

    public string WizardActionText => CurrentWizardStep switch
    {
        WizardStep.ScanWifi        => "Scaneaza Acum",
        WizardStep.LoadPlan        => "Incarca Plan",
        WizardStep.Calibrate       => IsCalibrated ? "Continua →" : "Calibreaza Scara",
        WizardStep.DrawWalls       => "Continua →",
        WizardStep.MarkLocations   => SurveyPointCount >= 2 ? "Continua →" : "Marcheaza pe harta",
        WizardStep.GenerateHeatmap => "Genereaza Heatmap",
        WizardStep.OptimizeRouter  => "Optimizeaza cu AI",
        _                          => "Inchide Ghidul"
    };

    partial void OnIsExpertModeChanged(bool value)      => OnPropertyChanged(nameof(IsWizardVisible));
    partial void OnIsWizardMinimizedChanged(bool value)  => OnPropertyChanged(nameof(IsWizardVisible));
    partial void OnCurrentWizardStepChanged(WizardStep value)
    {
        OnPropertyChanged(nameof(IsWizardVisible));
        OnPropertyChanged(nameof(WizardStepNumber));
        OnPropertyChanged(nameof(WizardProgress));
        OnPropertyChanged(nameof(WizardTitle));
        OnPropertyChanged(nameof(WizardDescription));
        OnPropertyChanged(nameof(WizardActionText));

        // La intrarea în DrawWalls, activăm automat unealta Linie Dreaptă
        if (value == WizardStep.DrawWalls)
            WeakReferenceMessenger.Default.Send(new ActivateDrawLineForCalibrationMessage());
    }

    // IsGridCanvasActive = true când userul a ales "Plan Grilă Nou" fără să încarce o imagine
    [ObservableProperty]
    private bool _isGridCanvasActive = false;

    partial void OnIsGridCanvasActiveChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyPlaceholder));

    public void AdvanceWizardIfNeeded()
    {
        if (IsExpertMode) return;
        bool hasCanvas = FloorPlan != null || IsGridCanvasActive;
        CurrentWizardStep = CurrentWizardStep switch
        {
            WizardStep.ScanWifi        when VisibleNetworks.Count > 0 => WizardStep.LoadPlan,
            WizardStep.LoadPlan        when hasCanvas                  => WizardStep.Calibrate,
            WizardStep.Calibrate       when IsCalibrated               => WizardStep.DrawWalls,
            // DrawWalls: fără condiție automată — userul apasă manual Continuă
            WizardStep.MarkLocations   when SurveyPointCount >= 2      => WizardStep.GenerateHeatmap,
            WizardStep.GenerateHeatmap when HeatmapOverlay != null     => WizardStep.OptimizeRouter,
            WizardStep.OptimizeRouter  when IsOptimizationDone         => WizardStep.Done,
            _                                                           => CurrentWizardStep
        };
        OnPropertyChanged(nameof(WizardDescription));
        OnPropertyChanged(nameof(WizardActionText));
    }

    // Auto-avansare când calibrarea e completă (Calibrate → DrawWalls)
    partial void OnIsCalibratedChanged(bool value)
    {
        if (value) AdvanceWizardIfNeeded();
        OnPropertyChanged(nameof(WizardDescription));
        OnPropertyChanged(nameof(WizardActionText));
    }

    [RelayCommand]
    private void DismissWizard() => IsWizardMinimized = true;

    [RelayCommand]
    private void ShowWizard() => IsWizardMinimized = false;

    [RelayCommand]
    private void SkipCalibration()
    {
        if (CurrentWizardStep == WizardStep.Calibrate)
            CurrentWizardStep = WizardStep.DrawWalls;
    }

    [RelayCommand]
    private async Task ExecuteWizardStep()
    {
        switch (CurrentWizardStep)
        {
            case WizardStep.ScanWifi:
                await ScanAsync(CancellationToken.None);
                AdvanceWizardIfNeeded();
                break;
            case WizardStep.LoadPlan:
                await LoadMapAsync();
                AdvanceWizardIfNeeded();
                break;
            case WizardStep.Calibrate when IsCalibrated:
                CurrentWizardStep = WizardStep.DrawWalls;
                break;
            case WizardStep.Calibrate:
                // Pornește modul drag-calibrare (Ekahau-style)
                WeakReferenceMessenger.Default.Send(new StartCalibrationFromWizardMessage());
                break;
            case WizardStep.DrawWalls:
                // "Continuă →" — avansează la MarkLocations
                CurrentWizardStep = WizardStep.MarkLocations;
                break;
            case WizardStep.MarkLocations when SurveyPointCount >= 2:
                CurrentWizardStep = WizardStep.GenerateHeatmap;
                break;
            case WizardStep.GenerateHeatmap:
                GenerateHeatmap();
                break;
            case WizardStep.OptimizeRouter:
                CalculateOptimalRouter();
                break;
            case WizardStep.Done:
                IsExpertMode = true; // Let them explore on their own
                break;
        }
    }

    // === THRESHOLD OVERLAY — zone moarte ===
    [ObservableProperty]
    private bool _isDeadZoneOverlayActive = false;

    [ObservableProperty]
    private double _deadZoneThreshold = -75.0;  // dBm — prag implicit

    [ObservableProperty]
    private ImageSource? _deadZoneOverlay;

    // === VIEW SWITCHING ===
    [ObservableProperty]
    private CentralViewMode _centralViewMode = CentralViewMode.Normal;

    [ObservableProperty]
    private bool _isOptimizationDone = false;

    [ObservableProperty]
    private ImageSource? _optimizedHeatmapOverlay;

    [ObservableProperty]
    private NetworkHealthReport? _doctorReport;

    [ObservableProperty]
    private bool _isAnalyzingDoctor = false;

    // === STATISTICI COMPARATIVE ÎNAINTE/DUPĂ OPTIMIZARE ===
    [ObservableProperty]
    private CoverageStats? _beforeStats;

    [ObservableProperty]
    private CoverageStats? _afterStats;

    public bool HasCompareStats => BeforeStats != null && AfterStats != null;

    // ── Deltas pentru comparație ───────────────────────────────────────
    public double CoverageDelta      => HasCompareStats ? AfterStats!.CoveragePercent  - BeforeStats!.CoveragePercent  : 0;
    public double SignalDelta        => HasCompareStats ? AfterStats!.AverageSignal    - BeforeStats!.AverageSignal    : 0;
    public double ExcellentDelta     => HasCompareStats ? AfterStats!.ExcellentPercent - BeforeStats!.ExcellentPercent : 0;
    public double DeadZoneDelta      => HasCompareStats ? AfterStats!.DeadZonePercent  - BeforeStats!.DeadZonePercent  : 0;
    public double ThroughputDelta    => HasCompareStats ? AfterStats!.EstimatedThroughputMbps - BeforeStats!.EstimatedThroughputMbps : 0;
    public double UniformityDelta    => HasCompareStats ? AfterStats!.UniformityScore  - BeforeStats!.UniformityScore  : 0;

    // ── Scor AI și progres bar (0–1) pentru After ─────────────────────
    public double AfterScoreNorm     => AfterStats != null ? AfterStats.OverallScore / 100.0 : 0;
    public double BeforeScoreNorm    => BeforeStats != null ? BeforeStats.OverallScore / 100.0 : 0;

    partial void OnBeforeStatsChanged(CoverageStats? value) => NotifyStatsChanged();
    partial void OnAfterStatsChanged(CoverageStats? value)  => NotifyStatsChanged();

    private void NotifyStatsChanged()
    {
        OnPropertyChanged(nameof(HasCompareStats));
        OnPropertyChanged(nameof(CoverageDelta));
        OnPropertyChanged(nameof(SignalDelta));
        OnPropertyChanged(nameof(ExcellentDelta));
        OnPropertyChanged(nameof(DeadZoneDelta));
        OnPropertyChanged(nameof(ThroughputDelta));
        OnPropertyChanged(nameof(UniformityDelta));
        OnPropertyChanged(nameof(AfterScoreNorm));
        OnPropertyChanged(nameof(BeforeScoreNorm));
    }

    public bool IsNormalViewActive => CentralViewMode == CentralViewMode.Normal;
    public bool IsOptimizedViewActive => CentralViewMode == CentralViewMode.Optimized;
    public bool IsDoctorViewActive => CentralViewMode == CentralViewMode.Doctor;
    public bool ShowDoctorNoNetwork => IsDoctorViewActive && !IsAnalyzingDoctor && DoctorReport == null && SelectedNetwork == null;

    partial void OnCentralViewModeChanged(CentralViewMode value)
    {
        OnPropertyChanged(nameof(IsNormalViewActive));
        OnPropertyChanged(nameof(IsOptimizedViewActive));
        OnPropertyChanged(nameof(IsDoctorViewActive));
        OnPropertyChanged(nameof(ShowDoctorNoNetwork));
    }

    partial void OnIsAnalyzingDoctorChanged(bool value) => OnPropertyChanged(nameof(ShowDoctorNoNetwork));
    partial void OnDoctorReportChanged(NetworkHealthReport? value) => OnPropertyChanged(nameof(ShowDoctorNoNetwork));

    partial void OnSelectedNetworkChanged(WifiNetwork? value)
    {
        DoctorReport = null;
        UpdateSelectedNetworkTrend();
    }

    partial void OnVisibleNetworksChanged(ObservableCollection<WifiNetwork> value) => AdvanceWizardIfNeeded();
    partial void OnFloorPlanChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(HasFloorPlan));
        OnPropertyChanged(nameof(ShowEmptyPlaceholder));
        AdvanceWizardIfNeeded();
    }
    partial void OnHeatmapOverlayChanged(ImageSource? value) => AdvanceWizardIfNeeded();
    partial void OnIsOptimizationDoneChanged(bool value) => AdvanceWizardIfNeeded();
    partial void OnSurveyPointCountChanged(int value)
    {
        AdvanceWizardIfNeeded();
        OnPropertyChanged(nameof(WizardDescription));
        OnPropertyChanged(nameof(WizardActionText));
    }

    public string AutoScanButtonText => IsAutoScanning ? "⏸ Stop Auto-Scan" : "▶ Start Auto-Scan";

    // === UNDO / REDO ===
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Pushed by ViewModel internals and by MainWindow (for InkCanvas strokes).
    /// </summary>
    public void PushUndoAction(Action undo, Action redo)
    {
        _undoStack.Push((undo, redo));
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.TryPop(out var action))
        {
            action.Undo();
            _redoStack.Push(action);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.TryPop(out var action))
        {
            action.Redo();
            _undoStack.Push(action);
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    // Interval auto-scan configurabil (secunde)
    [ObservableProperty]
    private int _autoScanIntervalSeconds = 5;

    partial void OnAutoScanIntervalSecondsChanged(int value)
    {
        _autoScanTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, value));
        StatusMessage = $"Interval auto-scan: {value}s";
    }

    public MainViewModel(IWifiScannerService wifiScanner, IMapService mapService)
    {
        _wifiScanner = wifiScanner;
        _mapService = mapService;
        _projectService = new ProjectService();

        _autoScanTimer = new DispatcherTimer();
        _autoScanTimer.Interval = TimeSpan.FromSeconds(AutoScanIntervalSeconds);
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
                // Reset complet înainte de noul plan (curăță tot statul anterior)
                _surveyPoints.Clear();
                _mapElements.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                SurveyPointCount   = 0;
                HeatmapOverlay     = null;
                OptimizedHeatmapOverlay = null;
                IsOptimizationDone = false;
                CentralViewMode    = CentralViewMode.Normal;
                DoctorReport       = null;
                IsCalibrated       = false;
                PixelsPerMeter     = 50.0;
                _signalHistory.Clear();
                _signalBaselines.Clear();
                SelectedNetworkTrend = "";
                IsSignalAlert        = false;
                SignalAlertMessage   = "";

                FloorPlan = bitmap;
                StatusMessage = $"Plan încărcat: {Path.GetFileName(dialog.FileName)}";

                // Curăță canvas-urile vizuale (survey points, stampile, pereți desenați)
                WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
                WeakReferenceMessenger.Default.Send(new ResetMapCanvasMessage());
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

            // Actualizăm istoricul semnal pentru Continuous Survey
            UpdateSignalHistory(networks);

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
            StatusMessage = $"Auto-Scan pornit. Scanare la fiecare {AutoScanIntervalSeconds}s.";
            _ = ScanAsync(CancellationToken.None);
        }

        OnPropertyChanged(nameof(AutoScanButtonText));
    }

    private void AutoScanTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsScanning)
            _ = ScanAsync(CancellationToken.None);
    }

    // ─── Signal History pentru Continuous Survey ──────────────────────────────
    private void UpdateSignalHistory(IReadOnlyList<WifiNetwork> networks)
    {
        foreach (var net in networks)
        {
            if (!_signalHistory.TryGetValue(net.SSID, out var queue))
            {
                queue = new Queue<int>(SignalHistorySize);
                _signalHistory[net.SSID] = queue;
            }
            if (queue.Count >= SignalHistorySize) queue.Dequeue();
            queue.Enqueue(net.SignalStrength);

            // Calculăm baseline după primele BaselineSampleCount citiri
            if (!_signalBaselines.ContainsKey(net.SSID) && queue.Count >= BaselineSampleCount)
                _signalBaselines[net.SSID] = queue.Take(BaselineSampleCount).Average();
        }

        UpdateSelectedNetworkTrend();
    }

    private void UpdateSelectedNetworkTrend()
    {
        if (SelectedNetwork == null)
        {
            SelectedNetworkTrend = "";
            IsSignalAlert = false;
            SignalAlertMessage = "";
            return;
        }

        var ssid = SelectedNetwork.SSID;

        if (!_signalHistory.TryGetValue(ssid, out var queue) || queue.Count < 2)
        {
            SelectedNetworkTrend = "";
            return;
        }

        var values  = queue.ToArray();
        int current  = values[^1];
        int previous = values[^2];
        int delta    = current - previous;

        SelectedNetworkTrend = delta > 1  ? $"↑ +{delta} dBm" :
                               delta < -1 ? $"↓ {delta} dBm"  : "— stabil";

        // Alertă dacă semnalul a scăzut față de baseline
        if (_signalBaselines.TryGetValue(ssid, out double baseline))
        {
            double drop = baseline - current; // pozitiv = scădere
            if (drop >= SignalDropAlertThresholdDbm)
            {
                IsSignalAlert = true;
                SignalAlertMessage = $"Semnal degradat: -{(int)drop} dBm față de normal ({(int)baseline} dBm)";
            }
            else
            {
                IsSignalAlert = false;
                SignalAlertMessage = "";
            }
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

        int pointNumber = _surveyPoints.Count;
        StatusMessage = $"Punctul {pointNumber} înregistrat la ({point.X:F0}, {point.Y:F0}) " +
                       $"- {VisibleNetworks.Count} rețele detectate";

        // Push undo action
        PushUndoAction(
            undo: () =>
            {
                _surveyPoints.Remove(surveyPoint);
                SurveyPointCount = _surveyPoints.Count;
                WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
                WeakReferenceMessenger.Default.Send(new RedrawSurveyPointsMessage(_surveyPoints));
                StatusMessage = $"Undo: Punct de măsurare #{pointNumber} eliminat.";
            },
            redo: () =>
            {
                _surveyPoints.Add(surveyPoint);
                SurveyPointCount = _surveyPoints.Count;
                WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
                WeakReferenceMessenger.Default.Send(new RedrawSurveyPointsMessage(_surveyPoints));
                StatusMessage = $"Redo: Punct de măsurare #{pointNumber} readăugat.";
            });
    }

    [RelayCommand]
    private void AnalyzeNetwork()
    {
        if (SelectedNetwork == null)
        {
            StatusMessage = "Selectează o rețea din panoul din stânga pentru a rula diagnosticul.";
            return;
        }

        try
        {
            IsAnalyzingDoctor = true;
            StatusMessage = "Analizăm rețeaua folosind Machine Learning...";

            var doctor = new WifiDoctorService();
            DoctorReport = doctor.AnalyzeNetwork(SelectedNetwork, VisibleNetworks.ToList().AsReadOnly());

            StatusMessage = $"Diagnostic complet pentru '{SelectedNetwork.SSID}'. Scor sănătate: {DoctorReport.OverallHealthScore}/100.";
            CentralViewMode = CentralViewMode.Doctor;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Eroare diagnostic AI: {ex.Message}";
        }
        finally
        {
            IsAnalyzingDoctor = false;
        }
    }

    [RelayCommand]
    private void SwitchToNormalView()
    {
        CentralViewMode = CentralViewMode.Normal;
        StatusMessage = "Vizualizare: Heatmap normal.";
    }

    [RelayCommand]
    private void SwitchToOptimizedView()
    {
        if (!IsOptimizationDone)
        {
            StatusMessage = "Rulați mai întâi 'Poziție Optimă Router' pentru a vedea simularea.";
            return;
        }
        CentralViewMode = CentralViewMode.Optimized;
        StatusMessage = "Vizualizare: Simulare semnal cu router la poziția optimă.";
    }

    [RelayCommand]
    private void SwitchToDoctorView()
    {
        CentralViewMode = CentralViewMode.Doctor;
        if (SelectedNetwork == null)
        {
            StatusMessage = "Selectează o rețea din panoul din stânga pentru a rula diagnosticul.";
        }
        else if (DoctorReport == null)
        {
            AnalyzeNetwork();
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

        // Push undo action
        PushUndoAction(
            undo: () =>
            {
                _mapElements.Remove(element);
                WeakReferenceMessenger.Default.Send(new RedrawMapElementsMessage(_mapElements));
                StatusMessage = $"Undo: {elementName} eliminat.";
            },
            redo: () =>
            {
                _mapElements.Add(element);
                WeakReferenceMessenger.Default.Send(new RedrawMapElementsMessage(_mapElements));
                StatusMessage = $"Redo: {elementName} readăugat.";
            });
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
                // ── PASUL 1: Reset complet stare vizuală și date vechi ─────────
                _surveyPoints.Clear();
                _mapElements.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                SurveyPointCount  = 0;
                HeatmapOverlay    = null;
                OptimizedHeatmapOverlay = null;
                DeadZoneOverlay   = null;
                IsOptimizationDone = false;
                DoctorReport      = null;
                BeforeStats       = null;
                AfterStats        = null;
                CentralViewMode   = CentralViewMode.Normal;

                // ── PASUL 2: Încarcă planul din JSON ÎNAINTE de orice redesenare
                //    (astfel ResetMapCanvasMessage va folosi dimensiunile corecte)
                FloorPlan = null;   // curăță planul vechi din binding
                if (!string.IsNullOrEmpty(projectData.SketchPngBase64))
                {
                    var restoredPlan = await _mapService.LoadFromBase64Async(projectData.SketchPngBase64);
                    if (restoredPlan != null)
                        FloorPlan = restoredPlan;
                }

                // ── PASUL 3: Curăță complet canvas-urile vizuale (cu planul nou setat)
                WeakReferenceMessenger.Default.Send(new ResetMapCanvasMessage());

                // ── PASUL 4: Populăm datele din JSON ──────────────────────────
                foreach (var point in projectData.SurveyPoints)
                    _surveyPoints.Add(point);

                foreach (var element in projectData.MapElements)
                    _mapElements.Add(element);

                PixelsPerMeter   = projectData.PixelsPerMeter;
                IsCalibrated     = projectData.IsCalibrated;
                SurveyPointCount = _surveyPoints.Count;

                // Reconstruim VisibleNetworks din datele salvate în puncte
                var networksFromSave = _surveyPoints
                    .SelectMany(p => p.DetectedNetworks)
                    .GroupBy(n => n.SSID)
                    .Select(g => new WifiNetwork
                    {
                        SSID = g.Key,
                        BSSID = g.First().BSSID,
                        SignalStrength = (int)g.Average(n => n.SignalStrength),
                        Channel = g.First().Channel
                    })
                    .OrderByDescending(n => n.SignalStrength)
                    .ToList();

                VisibleNetworks.Clear();
                foreach (var net in networksFromSave)
                    VisibleNetworks.Add(net);

                if (VisibleNetworks.Count > 0)
                    SelectedNetwork = VisibleNetworks[0];

                // ── PASUL 5: Redesenăm vizual pe canvas-ul curat ──────────────
                WeakReferenceMessenger.Default.Send(new RedrawSurveyPointsMessage(_surveyPoints));
                WeakReferenceMessenger.Default.Send(new RedrawMapElementsMessage(_mapElements));

                if (strokes != null)
                    WeakReferenceMessenger.Default.Send(new RestoreSketchMessage(strokes));

                string calibrareInfo = IsCalibrated
                    ? $" | Scară: {PixelsPerMeter:F1} px/m"
                    : " | Fără calibrare";
                string reteleInfo = VisibleNetworks.Count > 0
                    ? $", {VisibleNetworks.Count} rețele restaurate"
                    : "";

                StatusMessage = $"Proiect încărcat! ({_surveyPoints.Count} puncte{reteleInfo}{calibrareInfo})";

                // ── PASUL 6: Generăm heatmap dacă avem suficiente date ─────────
                if (FloorPlan != null && _surveyPoints.Count >= 2 && SelectedNetwork != null)
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
    private void CalculateOptimalRouter() // ✅ Nu mai e Async
    {
        if (FloorPlan == null)
        {
            MessageBox.Show("Încarcă o hartă mai întâi!", "Eroare",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_surveyPoints.Count < 2)
        {
            MessageBox.Show("Avem nevoie de cel puțin 2 puncte pentru a delimita zona casei.",
                "Atenție", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusMessage = "Calculăm poziția optimă folosind AI Multi-Criteria...";

        double scara = IsCalibrated ? PixelsPerMeter : 50.0;
        double width = FloorPlan.PixelWidth;
        double height = FloorPlan.PixelHeight;

        // Trimitem canalul și SSID-ul rețelei selectate (bandă + calibrare TX power)
        int    channel = SelectedNetwork?.Channel ?? 0;
        string? ssid   = SelectedNetwork?.SSID;
        WeakReferenceMessenger.Default.Send(new RequestOptimalRouterMessage(width, height, scara, channel, ssid));
    }

    [RelayCommand]
    private void ClearSurveyPoints()
    {
        _surveyPoints.Clear();
        _mapElements.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        SurveyPointCount = 0;
        HeatmapOverlay   = null;
        DeadZoneOverlay  = null;
        StatusMessage    = "Toate punctele și obiectele au fost șterse.";

        // Curăță și vizual: strokes, stamps, survey points
        WeakReferenceMessenger.Default.Send(new ResetMapCanvasMessage());
        WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
    }

    /// <summary>
    /// Reset complet — ca și când aplicația tocmai a pornit.
    /// Curăță tot: plan, puncte, obstacole, heatmap, schiță.
    /// </summary>
    [RelayCommand]
    private void ResetAll()
    {
        _surveyPoints.Clear();
        _mapElements.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        SurveyPointCount        = 0;
        FloorPlan               = null;
        HeatmapOverlay          = null;
        DeadZoneOverlay         = null;
        OptimizedHeatmapOverlay = null;
        IsOptimizationDone      = false;
        BeforeStats             = null;
        AfterStats              = null;
        DoctorReport            = null;
        IsCalibrated            = false;
        PixelsPerMeter          = 50.0;
        CentralViewMode         = CentralViewMode.Normal;
        VisibleNetworks.Clear();
        SelectedNetwork         = null;
        CurrentWizardStep       = WizardStep.ScanWifi;
        StatusMessage           = "Reset complet efectuat. Încarcă un plan nou pentru a începe.";

        WeakReferenceMessenger.Default.Send(new ResetMapCanvasMessage());
        WeakReferenceMessenger.Default.Send(new ClearSurveyPointsMessage());
    }
}

public sealed class StartCalibrationFromWizardMessage { }
public sealed class ActivateDrawLineForCalibrationMessage { }

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

public sealed class RequestSaveWithSketchMessage
{
}

/// <summary>
/// Cere MainWindow să reseteze canvas-urile vizuale (pereți, stampile, transforms).
/// </summary>
public sealed class ResetMapCanvasMessage
{
}

// ✅ NOU: Mesaj pentru calcul poziție optimă router
public sealed class RequestOptimalRouterMessage
{
    public double Width   { get; }
    public double Height  { get; }
    public double  Scale      { get; }
    public int     Channel    { get; }   // canal WiFi → determină banda (2.4/5/6 GHz)
    public string? TargetSSID { get; }   // SSID-ul rețelei selectate, pt calibrare TX power

    public RequestOptimalRouterMessage(double w, double h, double scale, int channel, string? ssid)
    {
        Width = w; Height = h; Scale = scale; Channel = channel; TargetSSID = ssid;
    }
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

public sealed class OptimizedHeatmapReadyMessage
{
    public System.Windows.Point OptimalPosition { get; }

    public OptimizedHeatmapReadyMessage(System.Windows.Point pos)
    {
        OptimalPosition = pos;
    }
}