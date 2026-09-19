using CommunityToolkit.Mvvm.Messaging;
using MahApps.Metro.IconPacks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WifiSignalOptimizer.Dialogs;
using WifiSignalOptimizer.Models;
using WifiSignalOptimizer.Services;
using WifiSignalOptimizer.ViewModels;

namespace WifiSignalOptimizer.Views;

/// <summary>
/// interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    // image manipulation variables
    private Point _lastMousePosition;
    private bool _isDragging = false;
    private double _currentZoom = 1.0;
    private double _currentRotation = 0.0;
    private TransformGroup _imageTransformGroup = null!;
    private ScaleTransform _scaleTransform = null!;
    private RotateTransform _rotateTransform = null!;
    private TranslateTransform _translateTransform = null!;

    // optimized view pan/zoom variables
    private bool _isOptimizedPanning = false;
    private Point _optimizedPanStart;
    private double _optimizedZoom = 1.0;
    private ScaleTransform _optScaleTransform = new ScaleTransform(1, 1);
    private TranslateTransform _optTranslateTransform = new TranslateTransform(0, 0);

    // drawing mode variables
    private Point _startPoint;
    private bool _isDrawingLine = false;
    private bool _isLineToolActive = false;
    private bool _isCalibrating = false;
    private Line? _calibrationLine;
    private const int GRID_SIZE = 20;

    // drag and drop for stamps
    private FrameworkElement? _draggedImage;
    private Point _dragOffset;
    private bool _isDraggingElement = false;

    public MainWindow()
    {
        InitializeComponent();

        var wifiScanner = new WifiScannerService();
        var mapService = new MapService();
        _viewModel = new MainViewModel(wifiScanner, mapService);

        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.TryAutoLoadLastProjectAsync();

        WeakReferenceMessenger.Default.Register<ClearSurveyPointsMessage>(this, (r, m) =>
        {
            ClearVisualSurveyPoints();
        });

        WeakReferenceMessenger.Default.Register<RedrawSurveyPointsMessage>(this, (r, m) =>
        {
            RedrawSurveyPoints(m.SurveyPoints);
        });

        WeakReferenceMessenger.Default.Register<RedrawMapElementsMessage>(this, (r, m) =>
        {
            RedrawMapElements(m.MapElements);
        });

        WeakReferenceMessenger.Default.Register<RestoreSketchMessage>(this, (r, m) =>
        {
            EditorCanvas.Strokes.Clear();
            foreach (var stroke in m.Strokes)
                EditorCanvas.Strokes.Add(stroke);

            // do not enable the drawing interface here, only display the sketch
            EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        });

        WeakReferenceMessenger.Default.Register<RequestSaveWithSketchMessage>(this, (r, m) =>
        {
            CollectSketchAndSave();
        });

        WeakReferenceMessenger.Default.Register<StartCalibrationFromWizardMessage>(this, (r, m) =>
        {
            if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed)
            {
                _viewModel.StatusMessage = "Incarca mai intai un plan!";
                return;
            }
            _isCalibrating = true;
            Mouse.OverrideCursor = Cursors.Cross;
            _viewModel.StatusMessage = "CALIBRARE: Tine click stanga apasat si trage o linie intre 2 puncte cu distanta cunoscuta.";
        });

        WeakReferenceMessenger.Default.Register<ActivateDrawLineForCalibrationMessage>(this, (r, m) =>
        {
            if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed)
                return;
            if (ToolDrawLine != null)
                ToolDrawLine.IsChecked = true; // triggers ToolDrawLine_Checked automatically
            _viewModel.StatusMessage = "Deseneaza linii drepte peste peretii din plan. Foloseste trusa de unelte din dreapta.";
        });

        WeakReferenceMessenger.Default.Register<ResetMapCanvasMessage>(this, (r, m) =>
        {
            // clear all visual canvases
            EditorCanvas.Strokes.Clear();
            EditorCanvas.EditingMode = InkCanvasEditingMode.None;
            ObjectsCanvas.Children.Clear();
            OptimizedObjectsCanvas?.Children.Clear();
            SurveyPointsCanvas.Children.Clear();    // also includes the survey points

            GridBackground.Visibility = Visibility.Collapsed;
            _viewModel.IsGridCanvasActive = false;

            // reset image transformations (zoom, rotation, translation)
            ResetImageTransformations();
        });

        WeakReferenceMessenger.Default.Register<RequestOptimalRouterMessage>(this, async (r, m) =>
        {
            try
            {
                var elements = _viewModel.GetMapElements().ToList();
                var surveys  = _viewModel.GetSurveyPoints().ToList();
                var strokes  = EditorCanvas.Strokes;

                // configure the simulation based on the selected network's band (2.4 / 5 / 6 GHz)
                string bandLabel = "2.4 GHz";
                _viewModel.StatusMessage = "Calculăm poziția optimă cu AI Multi-Criteria...";

                // 1. find the optimal position, with SSID included for consistency with real measurements
                //    networks with more dense points give a more precise position
                Point optimalPosition = await Task.Run(() =>
                {
                    var optimizer = new RouterOptimizationService();
                    bandLabel = optimizer.Configure(m.Channel);
                    return optimizer.FindOptimalPosition(
                        elements, surveys, strokes, m.Width, m.Height, m.Scale,
                        targetSSID: m.TargetSSID);
                });

                _viewModel.StatusMessage =
                    $"Poziție găsită: ({optimalPosition.X:F0}, {optimalPosition.Y:F0}). " +
                    $"Se generează simularea {bandLabel}...";

                // 2. calibrate TX power, generate the optimized heatmap and compute the statistics
                double calibratedTx = 20.0;
                CoverageStats? afterStats  = null;
                CoverageStats? beforeStats = null;
                var optimizedHeatmap = await Task.Run(() =>
                {
                    var optimizer = new RouterOptimizationService();
                    optimizer.Configure(m.Channel);

                    calibratedTx = optimizer.CalibrateTxPower(
                        optimalPosition, surveys, m.TargetSSID, m.Scale);

                    var bmp = optimizer.GenerateOptimizedHeatmap(
                        optimalPosition, elements, strokes,
                        (int)m.Width, (int)m.Height, m.Scale);

                    // statistics after optimization (physical model from the optimal position)
                    afterStats = optimizer.CalculateOptimizedStats(
                        optimalPosition, elements, strokes,
                        (int)m.Width, (int)m.Height, m.Scale);

                    // statistics before (IDW from real survey points)
                    var heatmapPoints = surveys.Select(p =>
                    {
                        var net = p.DetectedNetworks.FirstOrDefault(n => n.SSID == m.TargetSSID);
                        return new HeatmapPoint(p.X, p.Y, net?.SignalStrength ?? -100);
                    }).Where(hp => hp.SignalStrength > -100).ToList();

                    if (heatmapPoints.Count >= 2)
                        beforeStats = RouterOptimizationService.CalculateRealStats(
                            heatmapPoints, elements, strokes,
                            (int)m.Width, (int)m.Height, m.Scale);

                    return bmp;
                });

                // 3. update the ViewModel and switch to Optimized mode
                _viewModel.OptimizedHeatmapOverlay = optimizedHeatmap;
                _viewModel.IsOptimizationDone      = true;
                _viewModel.BeforeStats             = beforeStats;
                _viewModel.AfterStats              = afterStats;
                _viewModel.CentralViewMode         = CentralViewMode.Optimized;

                // 4. initialize pan/zoom transforms and set canvas size
                SetOptimizedMapSize();
                InitializeOptimizedViewTransforms();

                // 5. draw the router indicator only on the Optimized canvas
                DrawOptimalRouterOnOptimizedCanvas(optimalPosition);

                _viewModel.IsProcessing   = false;
                _viewModel.StatusMessage =
                    $"Optimizare completă [{bandLabel} · TX≈{calibratedTx:F1} dBm] · " +
                    $"Poziție optimă: ({optimalPosition.X:F0}, {optimalPosition.Y:F0}). " +
                    $"Comutați vizualizarea din meniul Vizualizare.";
            }
            catch (Exception ex)
            {
                _viewModel.IsProcessing   = false;
                _viewModel.StatusMessage = $"Eroare calcul AI: {ex.Message}";
                MessageBox.Show($"Eroare la calculul poziției optime:\n{ex.Message}",
                    "Eroare AI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        WeakReferenceMessenger.Default.Register<RequestHeatmapGenerationMessage>(this, async (r, m) =>
        {
            try
            {
                var allPoints = _viewModel.GetSurveyPoints();
                var obstacles = _viewModel.GetMapElements().ToList();
                var strokes = EditorCanvas.Strokes;

                var filteredPoints = allPoints.Select(p =>
                {
                    var net = p.DetectedNetworks.FirstOrDefault(n => n.SSID == m.TargetSSID);
                    return new HeatmapPoint(p.X, p.Y, net?.SignalStrength ?? -100);
                }).ToList();

                var heatmap = await Task.Run(() =>
                {
                    var service = new HeatmapService();
                    return service.GenerateHeatmap(filteredPoints, obstacles, strokes, m.Width, m.Height, m.Scale, m.Channel);
                });

                if (heatmap != null && heatmap.CanFreeze)
                    heatmap.Freeze();

                _viewModel.HeatmapOverlay = heatmap;
                _viewModel.IsProcessing   = false;

                // regenerate the dead zone overlay if active
                if (_viewModel.IsDeadZoneOverlayActive)
                    await RegenerateDeadZoneOverlayAsync(filteredPoints, obstacles, strokes, m.Width, m.Height, m.Scale, m.Channel);

                string calibrareInfo = _viewModel.IsCalibrated
                    ? $"calibrat: {_viewModel.PixelsPerMeter:F1} px/m"
                    : "necalibrat";

                _viewModel.StatusMessage =
                    $"Heatmap '{m.TargetSSID}' generat din {filteredPoints.Count} pct., " +
                    $"{obstacles.Count} obstacole ({calibrareInfo}).";
            }
            catch (Exception ex)
            {
                _viewModel.IsProcessing  = false;
                _viewModel.StatusMessage = $"Eroare heatmap: {ex.Message}";
            }
        });

        InitializeImageTransforms();

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.FloorPlan) && _viewModel.FloorPlan != null)
                ResetImageTransformations();

            if (e.PropertyName == nameof(_viewModel.AfterStats) && _viewModel.AfterStats != null)
                UpdateStatsBarWidths(_viewModel.AfterStats);

            if (e.PropertyName == nameof(_viewModel.CentralViewMode))
                AnimateViewTransition();
        };

        // inject access to coverage stats into DoctorPanel (for PDF export)
        DoctorPanel.GetCoverageStats = () => (_viewModel.BeforeStats, _viewModel.AfterStats);

        // give the terminal read-only access to the current visible networks
        SysadminTerminal.GetNetworks = () => _viewModel.VisibleNetworks;

        // hook into the InkCanvas stroke collection for undo/redo support
        Loaded += (_, _) =>
        {
            EditorCanvas.StrokeCollected += EditorCanvas_StrokeCollected;
        };

        // Ctrl+Z / Ctrl+Y — handled via PreviewKeyDown so InkCanvas doesn't intercept them
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewModel.UndoCommand.CanExecute(null))
                    _viewModel.UndoCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewModel.RedoCommand.CanExecute(null))
                    _viewModel.RedoCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void EditorCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        var stroke = e.Stroke;
        _viewModel.PushUndoAction(
            undo: () =>
            {
                EditorCanvas.Strokes.Remove(stroke);
                _viewModel.StatusMessage = "Undo: Perete desenat eliminat.";
            },
            redo: () =>
            {
                EditorCanvas.Strokes.Add(stroke);
                _viewModel.StatusMessage = "Redo: Perete desenat readăugat.";
            });
    }

    #region Image Transformations

    private void InitializeImageTransforms()
    {
        _scaleTransform = new ScaleTransform(1.0, 1.0);
        _rotateTransform = new RotateTransform(0);
        _translateTransform = new TranslateTransform(0, 0);

        _imageTransformGroup = new TransformGroup();
        _imageTransformGroup.Children.Add(_scaleTransform);
        _imageTransformGroup.Children.Add(_rotateTransform);
        _imageTransformGroup.Children.Add(_translateTransform);

        MapContent.RenderTransform = _imageTransformGroup;
        MapContent.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    private void ResetImageTransformations()
    {
        _currentZoom = 1.0;
        _currentRotation = 0.0;
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        _rotateTransform.Angle = 0;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;

        if (_viewModel.FloorPlan != null)
        {
            // device-independent size (accounts for DPI scaling)
            double dpiScaleX = _viewModel.FloorPlan.DpiX / 96.0;
            double dpiScaleY = _viewModel.FloorPlan.DpiY / 96.0;
            double w = _viewModel.FloorPlan.PixelWidth  / dpiScaleX;
            double h = _viewModel.FloorPlan.PixelHeight / dpiScaleY;

            MapCanvas.Width  = w;
            MapCanvas.Height = h;
            MapContent.Width  = w;
            MapContent.Height = h;
        }
    }

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed) return;

        const double zoomSpeed = 0.1;
        double zoomChange = e.Delta > 0 ? zoomSpeed : -zoomSpeed;

        _currentZoom += zoomChange;
        _currentZoom = Math.Clamp(_currentZoom, 0.1, 5.0);

        _scaleTransform.ScaleX = _currentZoom;
        _scaleTransform.ScaleY = _currentZoom;

        e.Handled = true;
    }

    #endregion

    #region Map Interaction

    private void MapContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed) return;

        if (_isCalibrating)
        {
            _startPoint = e.GetPosition(MapContent);
            _calibrationLine = new Line
            {
                X1 = _startPoint.X,
                Y1 = _startPoint.Y,
                X2 = _startPoint.X,
                Y2 = _startPoint.Y,
                Stroke = Brushes.Red,
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };

            // the calibration line is placed above all layers
            Panel.SetZIndex(_calibrationLine, 100);

            MapContent.Children.Add(_calibrationLine);
            MapContent.CaptureMouse();
            e.Handled = true;
            return;
        }

        var clickPos = e.GetPosition(MapContent);
        string tool = GetActiveTool();

        switch (tool)
        {
            case "SurveyPoint":
                int signal = _viewModel.VisibleNetworks.Count > 0
                    ? _viewModel.VisibleNetworks.Max(n => n.SignalStrength)
                    : -90;
                DrawSurveyPoint(clickPos, signal);
                _viewModel.RecordPointCommand.Execute(clickPos);
                e.Handled = true;
                break;

            case "Router":
                PlaceObstacleAtPoint(clickPos, ElementType.Router);
                e.Handled = true;
                break;

            case "ConcreteWall":
                PlaceObstacleAtPoint(clickPos, ElementType.ConcreteWall);
                e.Handled = true;
                break;

            case "BrickWall":
                PlaceObstacleAtPoint(clickPos, ElementType.BrickWall);
                e.Handled = true;
                break;

            case "WoodDoor":
                PlaceObstacleAtPoint(clickPos, ElementType.WoodDoor);
                e.Handled = true;
                break;

            case "Fridge":
                PlaceObstacleAtPoint(clickPos, ElementType.Fridge);
                e.Handled = true;
                break;

            case "TV":
                PlaceObstacleAtPoint(clickPos, ElementType.TV);
                e.Handled = true;
                break;

            case "Microwave":
                PlaceObstacleAtPoint(clickPos, ElementType.Microwave);
                e.Handled = true;
                break;

            default:
                _isDragging = true;
                _lastMousePosition = e.GetPosition(MapCanvas);
                MapContent.CaptureMouse();
                e.Handled = true;
                break;
        }
    }

    private void PlaceObstacleAtPoint(Point pos, ElementType type)
    {
        var element = CreateIconElement(type);

        Canvas.SetLeft(element, pos.X - 20);
        Canvas.SetTop(element, pos.Y - 20);

        ObjectsCanvas.Children.Add(element);

        _viewModel.RegisterMapElementCommand.Execute(new MapElement
        {
            Type = type,
            X = pos.X - 20,
            Y = pos.Y - 20
        });

        var (_, label, _) = GetDrawingMaterial(type.ToString());
        _viewModel.StatusMessage = $"{type} plasat la X={pos.X:F0}, Y={pos.Y:F0}";
    }

    private void MapContent_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed) return;

        if (_isCalibrating && _calibrationLine != null)
        {
            Point currentPos = e.GetPosition(MapContent);
            _calibrationLine.X2 = currentPos.X;
            _calibrationLine.Y2 = currentPos.Y;
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            Point currentPosition = e.GetPosition(MapCanvas);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;

            _translateTransform.X += deltaX;
            _translateTransform.Y += deltaY;

            _lastMousePosition = currentPosition;
            e.Handled = true;
        }
    }

    private void MapContent_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCalibrating && _calibrationLine != null)
        {
            Point endPoint = e.GetPosition(MapContent);

            MapContent.Children.Remove(_calibrationLine);
            _calibrationLine = null;
            MapContent.ReleaseMouseCapture();

            e.Handled = true;
            FinishCalibration(_startPoint, endPoint);
            return;
        }

        if (_isDragging)
        {
            _isDragging = false;
            MapContent.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void MapContent_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed)
            return;

        var clickPosition = e.GetPosition(MapContent);

        int strongestSignal = _viewModel.VisibleNetworks.Count > 0
            ? _viewModel.VisibleNetworks.Max(n => n.SignalStrength)
            : -90;

        DrawSurveyPoint(clickPosition, strongestSignal);
        _viewModel.RecordPointCommand.Execute(clickPosition);

        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.FloorPlan == null && GridBackground.Visibility == Visibility.Collapsed) return;

        if (e.Key == Key.R)
        {
            double rotationChange = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -15 : 15;
            _currentRotation += rotationChange;
            _rotateTransform.Angle = _currentRotation;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_isCalibrating)
            {
                _isCalibrating = false;
                Mouse.OverrideCursor = null;
                if (_calibrationLine != null)
                {
                    MapContent.Children.Remove(_calibrationLine);
                    _calibrationLine = null;
                }
                _viewModel.StatusMessage = "Calibrare anulata.";
            }
            else if (DrawingOverlayUI.Visibility == Visibility.Visible)
            {
                CancelSketch_Click(sender, new RoutedEventArgs());
            }
            else
            {
                ResetImageTransformations();
            }
            e.Handled = true;
        }
    }

    #endregion

    #region Optimized View Navigation

    private void InitializeOptimizedViewTransforms()
    {
        var group = new TransformGroup();
        group.Children.Add(_optScaleTransform);
        group.Children.Add(_optTranslateTransform);
        OptimizedMapContent.RenderTransform = group;
        OptimizedMapContent.RenderTransformOrigin = new Point(0, 0);
    }

    private void SetOptimizedMapSize()
    {
        if (_viewModel.FloorPlan == null) return;
        double dpiScaleX = _viewModel.FloorPlan.DpiX / 96.0;
        double dpiScaleY = _viewModel.FloorPlan.DpiY / 96.0;
        double w = _viewModel.FloorPlan.PixelWidth  / dpiScaleX;
        double h = _viewModel.FloorPlan.PixelHeight / dpiScaleY;
        OptimizedMapCanvas.Width  = w;
        OptimizedMapCanvas.Height = h;
        OptimizedMapContent.Width  = w;
        OptimizedMapContent.Height = h;
        // reset zoom/pan when a new optimization result arrives
        _optimizedZoom = 1.0;
        _optScaleTransform.ScaleX = 1;
        _optScaleTransform.ScaleY = 1;
        _optTranslateTransform.X  = 0;
        _optTranslateTransform.Y  = 0;
    }

    private void OptimizedMapContent_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isOptimizedPanning = true;
        _optimizedPanStart = e.GetPosition(OptimizedMapCanvas);
        OptimizedMapContent.CaptureMouse();
        OptimizedMapContent.Cursor = Cursors.Hand;
    }

    private void OptimizedMapContent_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOptimizedPanning) return;
        var current = e.GetPosition(OptimizedMapCanvas);
        _optTranslateTransform.X += current.X - _optimizedPanStart.X;
        _optTranslateTransform.Y += current.Y - _optimizedPanStart.Y;
        _optimizedPanStart = current;
    }

    private void OptimizedMapContent_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isOptimizedPanning = false;
        OptimizedMapContent.ReleaseMouseCapture();
        OptimizedMapContent.Cursor = Cursors.Arrow;
    }

    private void OptimizedMapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double zoomChange = e.Delta > 0 ? 0.1 : -0.1;
        _optimizedZoom = Math.Clamp(_optimizedZoom + zoomChange, 0.2, 5.0);
        _optScaleTransform.ScaleX = _optimizedZoom;
        _optScaleTransform.ScaleY = _optimizedZoom;
    }

    #endregion

    #region Survey Points

    private void DrawSurveyPoint(Point position, int signalStrength)
    {
        const double dotSize = 14;
        const double outlineSize = 18;

        Brush signalColor = GetSignalColor(signalStrength);

        var outline = new Ellipse
        {
            Width = outlineSize,
            Height = outlineSize,
            Fill = Brushes.White,
            Stroke = Brushes.DarkGray,
            StrokeThickness = 1.5
        };

        var dot = new Ellipse
        {
            Width = dotSize,
            Height = dotSize,
            Fill = signalColor,
            Stroke = GetSignalStrokeColor(signalStrength),
            StrokeThickness = 2
        };

        Canvas.SetLeft(outline, position.X - outlineSize / 2);
        Canvas.SetTop(outline, position.Y - outlineSize / 2);
        Canvas.SetLeft(dot, position.X - dotSize / 2);
        Canvas.SetTop(dot, position.Y - dotSize / 2);

        SurveyPointsCanvas.Children.Add(outline);
        SurveyPointsCanvas.Children.Add(dot);

        string signalQuality = signalStrength > -50 ? "Excelent" :
                               signalStrength > -70 ? "Mediu" : "Slab";
        dot.ToolTip = $"Punct de Masurare\n" +
                      $"Coordonate: ({position.X:F0}, {position.Y:F0})\n" +
                      $"Semnal: {signalStrength} dBm ({signalQuality})";
    }

    private void RedrawSurveyPoints(IReadOnlyList<SurveyPoint> surveyPoints)
    {
        SurveyPointsCanvas.Children.Clear(); // clear any leftovers
        foreach (var point in surveyPoints)
        {
            int strongestSignal = point.DetectedNetworks.Count > 0
                ? point.DetectedNetworks.Max(n => n.SignalStrength)
                : -90;

            var position = new Point(point.X, point.Y);
            DrawSurveyPoint(position, strongestSignal);
        }
    }

    private Brush GetSignalColor(int signalStrength)
    {
        return signalStrength switch
        {
            > -50 => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            > -70 => new SolidColorBrush(Color.FromRgb(255, 193, 7)),
            _ => new SolidColorBrush(Color.FromRgb(244, 67, 54))
        };
    }

    private Brush GetSignalStrokeColor(int signalStrength)
    {
        return signalStrength switch
        {
            > -50 => new SolidColorBrush(Color.FromRgb(56, 142, 60)),
            > -70 => new SolidColorBrush(Color.FromRgb(245, 124, 0)),
            _ => new SolidColorBrush(Color.FromRgb(198, 40, 40))
        };
    }

    private void ClearVisualSurveyPoints()
    {
        SurveyPointsCanvas.Children.Clear();

        var oldIndicators = ObjectsCanvas.Children
            .OfType<FrameworkElement>()
            .Where(e => e.Tag as string == "OptimalRouter")
            .ToList();

        foreach (var old in oldIndicators)
            ObjectsCanvas.Children.Remove(old);
    }

    #endregion

    #region Map Elements (Obstacles)

    private void RedrawMapElements(IReadOnlyList<MapElement> elements)
    {
        ObjectsCanvas.Children.Clear();

        foreach (var element in elements)
        {
            var visual = CreateIconElement(element.Type);
            Canvas.SetLeft(visual, element.X);
            Canvas.SetTop(visual, element.Y);
            ObjectsCanvas.Children.Add(visual);
        }
    }

    #endregion

    #region Drawing Mode

    private void StartDrawOverPlan_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlayUI.Visibility = Visibility.Visible;
        GridBackground.Visibility = Visibility.Collapsed;
        _viewModel.StatusMessage = "Mod desen: trasează linii drepte peste pereții din plan.";
        ActivateDefaultDrawTool();
        _viewModel.AdvanceWizardIfNeeded();
    }

    private void StartNewGridCanvas_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlayUI.Visibility = Visibility.Visible;
        EditorCanvas.Strokes.Clear();
        ObjectsCanvas.Children.Clear();
        _viewModel.FloorPlan = null;

        MapCanvas.Width = 2000;
        MapCanvas.Height = 1500;
        MapContent.Width = 2000;
        MapContent.Height = 1500;

        GridBackground.Visibility = Visibility.Visible;
        _viewModel.PixelsPerMeter = GRID_SIZE;
        _viewModel.IsCalibrated = true;
        _viewModel.IsGridCanvasActive = true;
        _viewModel.StatusMessage = "Plan grilă nou: 1 pătrățel = 1 metru. Desenează pereții de la zero.";
        ActivateDefaultDrawTool();
        _viewModel.AdvanceWizardIfNeeded();
    }

    private void ActivateDefaultDrawTool()
    {
        if (ToolDrawWall.IsChecked != true && ToolDrawLine.IsChecked != true && ToolEraser.IsChecked != true)
            ToolDrawWall.IsChecked = true;
        else
        {
            EditorCanvas.IsHitTestVisible = true;
            _isCalibrating = false;
        }
    }

    private void StartSketch_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.FloorPlan == null && GridBackground.Visibility != Visibility.Visible)
            StartNewGridCanvas_Click(sender, e);
        else
            StartDrawOverPlan_Click(sender, e);
    }

    private void CancelSketch_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlayUI.Visibility = Visibility.Collapsed;
        GridBackground.Visibility = Visibility.Collapsed;

        // reset the canvas drawing state
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = false;
        _isLineToolActive = false;

        // switch the interface back to Navigate / Move mode
        ToolPointer.IsChecked = true;

        _viewModel.StatusMessage = "Desenare anulată.";
    }

    private void SaveSketch_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlayUI.Visibility = Visibility.Collapsed;
        GridBackground.Visibility = Visibility.Collapsed;

        // stop drawing and release the mouse
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = false;
        _isLineToolActive = false;

        // switch back to Navigate for safety
        ToolPointer.IsChecked = true;

        var dpiInfo = VisualTreeHelper.GetDpi(MapContent);
        int width = (int)(MapContent.ActualWidth * dpiInfo.DpiScaleX);
        int height = (int)(MapContent.ActualHeight * dpiInfo.DpiScaleY);

        if (width == 0 || height == 0) { width = 800; height = 600; }

        var rtb = new RenderTargetBitmap(
            width, height,
            dpiInfo.PixelsPerInchX, dpiInfo.PixelsPerInchY,
            PixelFormats.Pbgra32);

        var bg = new DrawingVisual();
        using (var ctx = bg.RenderOpen())
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        rtb.Render(bg);
        rtb.Render(EditorCanvas);
        rtb.Render(ObjectsCanvas);
        rtb.Freeze();

        using (var stream = new System.IO.MemoryStream())
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            enc.Save(stream);
            stream.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            _viewModel.FloorPlan = bmp;
        }

        _viewModel.StatusMessage = "Schița setată ca plan!";

        var result = MessageBox.Show(
            "Schița a fost setată ca plan!\n\nVrei să salvezi proiectul complet acum?\n" +
            "(schiță + puncte + obstacole + calibrare)",
            "Salvare Proiect",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            CollectSketchAndSave();
    }

    private void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.FloorPlan == null)
        {
            MessageBox.Show("Incarca mai intai un plan!", "Atentie");
            return;
        }

        _isCalibrating = true;
        Mouse.OverrideCursor = Cursors.Cross;
        _viewModel.StatusMessage = "MOD CALIBRARE: Tine click stanga apasat pe harta si trage o linie.";
    }

    private void SetBasicMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsExpertMode = false;
        _viewModel.IsWizardMinimized = false; // reopens the wizard when returning to Basic
        _viewModel.StatusMessage = "Mod Basic activat.";
    }

    private void SetExpertMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsExpertMode = true;
        _viewModel.StatusMessage = "Mod Expert activat — toate functiile sunt disponibile.";
    }

    #region Toolbox Handlers

    private string GetActiveTool()
    {
        if (ToolSurveyPoint?.IsChecked == true) return "SurveyPoint";
        if (ToolPointer?.IsChecked == true) return "Pointer";
        if (ToolRouter?.IsChecked == true) return "Router";
        if (ToolWallConcrete?.IsChecked == true) return "ConcreteWall";
        if (ToolWallBrick?.IsChecked == true) return "BrickWall";
        if (ToolDoorWood?.IsChecked == true) return "WoodDoor";
        if (ToolFridge?.IsChecked == true) return "Fridge";
        if (ToolTV?.IsChecked == true) return "TV";
        if (ToolMicrowave?.IsChecked == true) return "Microwave";
        if (ToolDrawWall?.IsChecked == true) return "DrawWall";
        if (ToolDrawLine?.IsChecked == true) return "DrawLine";
        if (ToolEraser?.IsChecked == true) return "Eraser";
        return "SurveyPoint";
    }

    private void UpdateActiveToolLabel(string toolName)
    {
        if (ActiveToolLabel == null) return;
        ActiveToolLabel.Text = toolName switch
        {
            "SurveyPoint" => "Punct Masurare activ",
            "Pointer" => "Navigare / Mutare activ",
            "Router" => "Router Virtual activ",
            "ConcreteWall" => "Zid Beton activ  -12 dBm",
            "BrickWall" => "Caramida activa  -6 dBm",
            "WoodDoor" => "Usa Lemn activa  -3 dBm",
            "Fridge" => "Frigider activ  -15 dBm",
            "TV" => "Televizor activ  -5 dBm",
            "Microwave" => "Microunde activ  -8 dBm",
            "DrawWall" => "Desenare Perete activa",
            "DrawLine" => "Linie Dreapta activa",
            "Eraser" => "Radiera activa",
            _ => "Unealta activa"
        };
    }

    private void ToolSurveyPoint_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("SurveyPoint");

        _isLineToolActive = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = false; // release the map

        MapContent.Cursor = Cursors.Cross;
        _viewModel.StatusMessage = "Click stanga pe harta pentru a plasa un punct de masurare.";
    }

    private void ToolPointer_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("Pointer");

        _isLineToolActive = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = false; // release the map

        MapContent.Cursor = Cursors.Hand;
        _viewModel.StatusMessage = "Mod navigare: trage harta cu click stanga.";
    }

    private static (Color color, double thickness, string label) GetDrawingMaterial(string tag) => tag switch
    {
        "ConcreteWall" => (Color.FromRgb(47, 79, 79), 8, "Zid Beton  -12 dBm"),
        "BrickWall" => (Color.FromRgb(139, 69, 19), 5, "Caramida  -6 dBm"),
        "WoodDoor" => (Color.FromRgb(205, 133, 63), 3, "Usa Lemn  -3 dBm"),
        "Fridge" => (Color.FromRgb(0, 100, 200), 4, "Frigider  -15 dBm"),
        "Microwave" => (Color.FromRgb(200, 50, 50), 4, "Microunde  -8 dBm"),
        "TV" => (Color.FromRgb(150, 0, 200), 4, "Televizor  -5 dBm"),
        _ => (Colors.Black, 6, "Perete standard  -10 dBm")
    };

    private void ToolObstacle_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag?.ToString() ?? "";
        UpdateActiveToolLabel(tag);

        if (DrawingOverlayUI.Visibility == Visibility.Visible)
        {
            var (color, thickness, label) = GetDrawingMaterial(tag);
            SetPenAttributes(color, thickness);
            EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
            EditorCanvas.IsHitTestVisible = true;
            _isLineToolActive = false;
            _viewModel.StatusMessage = $"Material selectat: {label} · Deseneaza pe schita";
            return;
        }

        // normal mode: place stamp on the map
        _isLineToolActive = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = false; // release the map

        MapContent.Cursor = Cursors.Cross;
        string desc = tag switch
        {
            "ConcreteWall" => "Zid Beton (-12 dBm)",
            "BrickWall" => "Caramida (-6 dBm)",
            "WoodDoor" => "Usa Lemn (-3 dBm)",
            "Fridge" => "Frigider (-15 dBm)",
            "TV" => "Televizor (-5 dBm)",
            "Microwave" => "Microunde (-8 dBm)",
            "Router" => "Router Virtual",
            _ => tag
        };
        _viewModel.StatusMessage = $"Click stanga pe harta pentru a plasa: {desc}";
    }

    private void ToolDrawWall_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("DrawWall");

        if (DrawingOverlayUI.Visibility != Visibility.Visible)
            StartSketch_Click(null!, null!);

        _isLineToolActive = false;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        EditorCanvas.IsHitTestVisible = true;
        SetPenAttributes(Colors.Black, 6);
    }

    private void ToolDrawLine_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("DrawLine");

        if (DrawingOverlayUI.Visibility != Visibility.Visible)
            StartSketch_Click(null!, null!);

        _isLineToolActive = true;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = true;
    }

    private void ToolEraser_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("Eraser");

        if (DrawingOverlayUI.Visibility != Visibility.Visible)
            StartSketch_Click(null!, null!);

        _isLineToolActive = false;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
        EditorCanvas.IsHitTestVisible = true;
    }

    #endregion

    private void FinishCalibration(Point start, Point end)
    {
        _isCalibrating = false;
        Mouse.OverrideCursor = null;

        double pixelDistance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));

        if (pixelDistance < 50)
        {
            MessageBox.Show("Linia este prea scurta. Trage o linie mai lunga pentru o precizie mai buna.",
                "Calibrare esuata", MessageBoxButton.OK, MessageBoxImage.Warning);
            _viewModel.StatusMessage = "Calibrare anulata (linie prea scurta).";
            return;
        }

        var dialog = new CalibrationDialog();
        dialog.Owner = this;

        if (dialog.ShowDialog() == true)
        {
            double realMeters = dialog.ResultMeters;
            _viewModel.PixelsPerMeter = pixelDistance / realMeters;
            _viewModel.IsCalibrated = true;

            _viewModel.StatusMessage = $"Calibrare reusita! Scara setata la: {_viewModel.PixelsPerMeter:F1} pixeli = 1 metru.";
            MessageBox.Show(
                $"Calibrare completa!\n\nDistanta de {pixelDistance:F0} pixeli a fost mapata la {realMeters} metri.\n\n" +
                $"Aplicatia stie acum ca 1 metru = {_viewModel.PixelsPerMeter:F0} pixeli.",
                "Succes", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            _viewModel.StatusMessage = "Calibrare anulata de utilizator.";
        }
    }

    #region Export PNG

    private void ExportHeatmapPng_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title  = "Exportă Heatmap ca PNG",
                Filter = "Imagini PNG|*.png",
                FileName = $"heatmap_{_viewModel.SelectedNetwork?.SSID ?? "wifi"}_{DateTime.Now:yyyyMMdd_HHmm}.png"
            };

            if (dialog.ShowDialog() != true) return;

            // render MapContent (floor plan, heatmap, stamps) at actual size
            var target = MapContent;
            var renderBitmap = new RenderTargetBitmap(
                (int)target.ActualWidth, (int)target.ActualHeight,
                96, 96, PixelFormats.Pbgra32);

            target.Measure(target.RenderSize);
            target.Arrange(new Rect(target.RenderSize));
            renderBitmap.Render(target);

            using var stream = System.IO.File.Create(dialog.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));
            encoder.Save(stream);

            _viewModel.StatusMessage = $"Heatmap exportat: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Eroare la export:\n{ex.Message}", "Export PNG", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Dead Zone Overlay

    private async void ToggleDeadZoneOverlay_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsDeadZoneOverlayActive = !_viewModel.IsDeadZoneOverlayActive;

        if (!_viewModel.IsDeadZoneOverlayActive)
        {
            _viewModel.DeadZoneOverlay = null;
            return;
        }

        if (_viewModel.HeatmapOverlay == null) return;

        var allPoints = _viewModel.GetSurveyPoints();
        var obstacles = _viewModel.GetMapElements().ToList();
        var strokes   = EditorCanvas.Strokes;
        var ssid      = _viewModel.SelectedNetwork?.SSID;
        int w         = (int)FloorPlanImage.ActualWidth;
        int h         = (int)FloorPlanImage.ActualHeight;
        double scale  = _viewModel.PixelsPerMeter;

        var filteredPoints = allPoints.Select(p =>
        {
            var net = p.DetectedNetworks.FirstOrDefault(n => n.SSID == ssid);
            return new HeatmapPoint(p.X, p.Y, net?.SignalStrength ?? -100);
        }).Where(hp => hp.SignalStrength > -100).ToList();

        await RegenerateDeadZoneOverlayAsync(filteredPoints, obstacles, strokes, w, h, scale,
            _viewModel.SelectedNetwork?.Channel ?? 1);
    }

    private async Task RegenerateDeadZoneOverlayAsync(
        List<HeatmapPoint> points,
        List<MapElement> obstacles,
        System.Windows.Ink.StrokeCollection strokes,
        int w, int h, double scale, int channel = 1)
    {
        var overlay = await Task.Run(() =>
        {
            var svc = new HeatmapService();
            return svc.GenerateDeadZoneOverlay(
                points, obstacles, strokes, w, h, scale,
                _viewModel.DeadZoneThreshold, channel);
        });
        _viewModel.DeadZoneOverlay = overlay;
    }

    #endregion

    #region Options Menu (hover dropdown)

    private System.Windows.Threading.DispatcherTimer? _optionsMenuTimer;

    private void OptionsMenuTrigger_MouseEnter(object sender, MouseEventArgs e)
    {
        _optionsMenuTimer?.Stop();
        OptionsPopup.IsOpen = true;
    }

    private void OptionsMenuTrigger_MouseLeave(object sender, MouseEventArgs e)
    {
        StartOptionsMenuCloseTimer();
    }

    private void OptionsPopupContent_MouseEnter(object sender, MouseEventArgs e)
    {
        _optionsMenuTimer?.Stop();
    }

    private void OptionsPopupContent_MouseLeave(object sender, MouseEventArgs e)
    {
        StartOptionsMenuCloseTimer();
    }

    private void StartOptionsMenuCloseTimer()
    {
        _optionsMenuTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _optionsMenuTimer.Tick += (s, _) =>
        {
            _optionsMenuTimer?.Stop();
            OptionsPopup.IsOpen = false;
        };
        _optionsMenuTimer.Start();
    }

    private void OptionsMenuNormal_Click(object sender, RoutedEventArgs e)
    {
        OptionsPopup.IsOpen = false;
        _viewModel.SwitchToNormalViewCommand.Execute(null);
    }

    private void OptionsMenuOptimized_Click(object sender, RoutedEventArgs e)
    {
        OptionsPopup.IsOpen = false;
        _viewModel.SwitchToOptimizedViewCommand.Execute(null);
    }

    private void OptionsMenuDoctor_Click(object sender, RoutedEventArgs e)
    {
        OptionsPopup.IsOpen = false;
        _viewModel.SwitchToDoctorViewCommand.Execute(null);
    }

    private void OptionsMenuSysadmin_Click(object sender, RoutedEventArgs e)
    {
        OptionsPopup.IsOpen = false;
        _viewModel.SwitchToSysadminViewCommand.Execute(null);
    }

    #endregion

    private void SetPenAttributes(Color color, double thickness)
    {
        var attributes = new DrawingAttributes
        {
            Color = color,
            Width = thickness,
            Height = thickness,
            FitToCurve = false,
            IgnorePressure = true
        };
        EditorCanvas.DefaultDrawingAttributes = attributes;
    }

    private Point SnapToGrid(Point original)
    {
        double snappedX = Math.Round(original.X / GRID_SIZE) * GRID_SIZE;
        double snappedY = Math.Round(original.Y / GRID_SIZE) * GRID_SIZE;
        return new Point(snappedX, snappedY);
    }

    private void EditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isLineToolActive && !_isCalibrating)
            return;

        _isDrawingLine = true;

        Point rawPoint = e.GetPosition(EditorCanvas);
        _startPoint = _isCalibrating ? rawPoint : SnapToGrid(rawPoint);

        PreviewLine.X1 = _startPoint.X;
        PreviewLine.Y1 = _startPoint.Y;
        PreviewLine.X2 = _startPoint.X;
        PreviewLine.Y2 = _startPoint.Y;
        PreviewLine.Visibility = Visibility.Visible;

        EditorCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void EditorCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawingLine) return;

        Point currentRaw = e.GetPosition(EditorCanvas);
        Point target = _isCalibrating ? currentRaw : SnapToGrid(currentRaw);

        PreviewLine.X2 = target.X;
        PreviewLine.Y2 = target.Y;
    }

    private void EditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingLine) return;

        _isDrawingLine = false;
        PreviewLine.Visibility = Visibility.Collapsed;
        EditorCanvas.ReleaseMouseCapture();

        Point endPoint = new Point(PreviewLine.X2, PreviewLine.Y2);

        if (_startPoint == endPoint) return;

        if (_isCalibrating)
        {
            FinishCalibration(_startPoint, endPoint);
            return;
        }

        var stylusPointCollection = new StylusPointCollection();
        stylusPointCollection.Add(new StylusPoint(_startPoint.X, _startPoint.Y));
        stylusPointCollection.Add(new StylusPoint(endPoint.X, endPoint.Y));

        var stroke = new Stroke(stylusPointCollection)
        {
            DrawingAttributes = EditorCanvas.DefaultDrawingAttributes.Clone()
        };
        EditorCanvas.Strokes.Add(stroke);
    }

    #region Stamp Objects (Router, Fridge, etc.)

    /// <summary>
    /// maps ElementType to (icon kind, color) for PackIconMaterial.
    /// </summary>
    private static (PackIconMaterialKind kind, Color color) GetIconForType(ElementType type) => type switch
    {
        ElementType.Router => (PackIconMaterialKind.RouterWireless, Color.FromRgb(59, 130, 246)),
        ElementType.Fridge => (PackIconMaterialKind.Fridge, Color.FromRgb(0, 100, 200)),
        ElementType.Microwave => (PackIconMaterialKind.Microwave, Color.FromRgb(200, 50, 50)),
        ElementType.ConcreteWall => (PackIconMaterialKind.Wall, Color.FromRgb(120, 120, 120)),
        ElementType.BrickWall => (PackIconMaterialKind.Wall, Color.FromRgb(139, 69, 19)),
        ElementType.WoodDoor => (PackIconMaterialKind.DoorOpen, Color.FromRgb(205, 133, 63)),
        ElementType.TV => (PackIconMaterialKind.Television, Color.FromRgb(150, 0, 200)),
        _ => (PackIconMaterialKind.CubeOutline, Color.FromRgb(180, 180, 180))
    };

    private void AddRouter_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.Router);

    private void AddFridge_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.Fridge);

    private void AddMicrowave_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.Microwave);

    private void AddConcreteWall_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.ConcreteWall);

    private void AddElementToCanvas(ElementType type)
    {
        var element = CreateIconElement(type);
        Canvas.SetLeft(element, 200);
        Canvas.SetTop(element, 200);
        ObjectsCanvas.Children.Add(element);
    }

    /// <summary>
    /// creates the visual element with PackIconMaterial for an obstacle type.
    /// </summary>
    private Border CreateIconElement(ElementType type)
    {
        var (kind, color) = GetIconForType(type);

        var icon = new PackIconMaterial
        {
            Kind = kind,
            Width = 20,
            Height = 20,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Width = 40,
            Height = 40,
            Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 0, 122, 204)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Child = icon,
            Cursor = Cursors.Hand,
            Tag = type,
            ToolTip = $"{type}  (-{new MapElement { Type = type }.SignalPenalty} dBm)"
        };

        border.MouseLeftButtonDown += Element_MouseDown;
        border.MouseLeftButtonUp += Element_MouseUp;
        border.MouseMove += Element_MouseMove;

        return border;
    }

    private void Element_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        _draggedImage = fe;
        _isDraggingElement = true;
        _dragOffset = e.GetPosition(_draggedImage);
        _draggedImage.CaptureMouse();
        e.Handled = true;
    }

    private void Element_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingElement && _draggedImage != null)
        {
            Point currentPos = e.GetPosition(ObjectsCanvas);
            Canvas.SetLeft(_draggedImage, currentPos.X - _dragOffset.X);
            Canvas.SetTop(_draggedImage, currentPos.Y - _dragOffset.Y);
            e.Handled = true;
        }
    }

    private void Element_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingElement && _draggedImage != null)
        {
            _isDraggingElement = false;
            _draggedImage.ReleaseMouseCapture();

            double finalX = Canvas.GetLeft(_draggedImage);
            double finalY = Canvas.GetTop(_draggedImage);
            ElementType type = (ElementType)_draggedImage.Tag;

            _viewModel.RegisterMapElementCommand.Execute(new MapElement
            {
                Type = type,
                X = finalX,
                Y = finalY
            });

            e.Handled = true;
        }
    }

    #endregion

    /// <summary>
    /// draws the visual indicator (green circle, router icon) for the optimal position.
    /// </summary>
    private void DrawOptimalRouterIndicator(Point pos)
    {
        var oldIndicators = ObjectsCanvas.Children
            .OfType<FrameworkElement>()
            .Where(e => e.Tag as string == "OptimalRouter")
            .ToList();
        foreach (var old in oldIndicators)
            ObjectsCanvas.Children.Remove(old);

        var outerRing = new Ellipse
        {
            Width = 56,
            Height = 56,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 0, 220, 0)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Tag = "OptimalRouter"
        };

        var innerCircle = new Ellipse
        {
            Width = 40,
            Height = 40,
            Fill = new SolidColorBrush(Color.FromArgb(100, 0, 255, 0)),
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 3,
            Tag = "OptimalRouter"
        };

        var routerIcon = new PackIconMaterial
        {
            Kind = PackIconMaterialKind.RouterWireless,
            Width = 20,
            Height = 20,
            Foreground = Brushes.LimeGreen,
            Tag = "OptimalRouter"
        };

        var label = new TextBlock
        {
            Text = "OPTIM",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.DarkGreen,
            Tag = "OptimalRouter"
        };

        Canvas.SetLeft(outerRing, pos.X - 28);
        Canvas.SetTop(outerRing, pos.Y - 28);
        Canvas.SetLeft(innerCircle, pos.X - 20);
        Canvas.SetTop(innerCircle, pos.Y - 20);
        Canvas.SetLeft(routerIcon, pos.X - 10);
        Canvas.SetTop(routerIcon, pos.Y - 10);
        Canvas.SetLeft(label, pos.X - 13);
        Canvas.SetTop(label, pos.Y + 21);

        ObjectsCanvas.Children.Add(outerRing);
        ObjectsCanvas.Children.Add(innerCircle);
        ObjectsCanvas.Children.Add(routerIcon);
        ObjectsCanvas.Children.Add(label);
    }

    /// <summary>
    /// draws the optimal router indicator on the Optimized mode canvas as well.
    /// </summary>
    private void DrawOptimalRouterOnOptimizedCanvas(Point pos)
    {
        if (OptimizedObjectsCanvas == null) return;

        OptimizedObjectsCanvas.Children.Clear();

        var outerRing = new Ellipse
        {
            Width = 56, Height = 56,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 220, 0)),
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 4, 2 }
        };

        var innerCircle = new Ellipse
        {
            Width = 40, Height = 40,
            Fill = new SolidColorBrush(Color.FromArgb(120, 0, 255, 0)),
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 3
        };

        var routerIcon = new PackIconMaterial
        {
            Kind = PackIconMaterialKind.RouterWireless,
            Width = 22, Height = 22,
            Foreground = Brushes.LimeGreen
        };

        var label = new TextBlock
        {
            Text = "POZIȚIE OPTIMĂ",
            FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.LimeGreen),
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Padding = new Thickness(3, 1, 3, 1)
        };

        Canvas.SetLeft(outerRing, pos.X - 28);
        Canvas.SetTop(outerRing, pos.Y - 28);
        Canvas.SetLeft(innerCircle, pos.X - 20);
        Canvas.SetTop(innerCircle, pos.Y - 20);
        Canvas.SetLeft(routerIcon, pos.X - 11);
        Canvas.SetTop(routerIcon, pos.Y - 11);
        Canvas.SetLeft(label, pos.X - 40);
        Canvas.SetTop(label, pos.Y + 23);

        OptimizedObjectsCanvas.Children.Add(outerRing);
        OptimizedObjectsCanvas.Children.Add(innerCircle);
        OptimizedObjectsCanvas.Children.Add(routerIcon);
        OptimizedObjectsCanvas.Children.Add(label);
    }

    /// <summary>
    /// collects the current sketch (ISF and DPI-aware PNG) and calls SaveProjectWithSketch.
    /// </summary>
    private void CollectSketchAndSave()
    {
        System.Windows.Ink.StrokeCollection? strokes = null;
        BitmapSource? png = null;

        if (EditorCanvas.Strokes.Count > 0)
        {
            strokes = EditorCanvas.Strokes;

            // DPI-aware render
            var dpiInfo = VisualTreeHelper.GetDpi(MapContent);
            int w = (int)(MapContent.ActualWidth * dpiInfo.DpiScaleX);
            int h = (int)(MapContent.ActualHeight * dpiInfo.DpiScaleY);

            if (w > 0 && h > 0)
            {
                var rtb = new RenderTargetBitmap(
                    w, h,
                    dpiInfo.PixelsPerInchX, dpiInfo.PixelsPerInchY,
                    PixelFormats.Pbgra32);

                var bg = new DrawingVisual();
                using (var ctx = bg.RenderOpen())
                    ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

                rtb.Render(bg);
                rtb.Render(EditorCanvas);
                rtb.Render(ObjectsCanvas);
                rtb.Freeze();
                png = rtb;
            }
        }

        _viewModel.SaveProjectWithSketch(strokes, png);
    }


    #endregion


    /// <summary>
    /// updates the width of the signal distribution bars in the AI Analysis panel.
    /// the bars are Border elements with x:Name; width is calculated proportionally to the container.
    /// </summary>
    private void AnimateViewTransition()
    {
        FrameworkElement? panel = _viewModel.CentralViewMode switch
        {
            CentralViewMode.Normal    => NormalViewPanel,
            CentralViewMode.Optimized => OptimizedViewPanel,
            CentralViewMode.Doctor    => DoctorViewPanel,
            CentralViewMode.Sysadmin  => SysadminTerminal,
            _                         => null
        };
        if (panel == null) return;

        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
            new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        panel.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void UpdateStatsBarWidths(WifiSignalOptimizer.Models.CoverageStats stats)
    {
        // wait for layout to be ready, then set the widths
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            double maxW = BarExcellent.ActualWidth > 0
                ? (BarExcellent.Parent as Border)?.ActualWidth ?? 90
                : 90;
            // if the container is not visible yet, do nothing
            if (maxW <= 0) return;

            BarExcellent.Width   = maxW * stats.ExcellentPercent / 100.0;
            BarGood.Width        = maxW * stats.GoodPercent      / 100.0;
            BarFair.Width        = maxW * stats.FairPercent      / 100.0;
            BarPoor.Width        = maxW * stats.PoorPercent      / 100.0;
            BarDead.Width        = maxW * stats.DeadZonePercent  / 100.0;
            ScoreProgressBar.Width = (ScoreProgressBar.Parent as Grid)?.ActualWidth * stats.OverallScore / 100.0 ?? 0;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<ClearSurveyPointsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RedrawSurveyPointsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RedrawMapElementsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RestoreSketchMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RequestSaveWithSketchMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RequestOptimalRouterMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RequestHeatmapGenerationMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StartCalibrationFromWizardMessage>(this);
        WeakReferenceMessenger.Default.Unregister<ActivateDrawLineForCalibrationMessage>(this);
        base.OnClosed(e);
    }
}

// HeatmapPoint moved to Models/HeatmapPoint.cs
