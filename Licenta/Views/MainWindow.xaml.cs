using CommunityToolkit.Mvvm.Messaging;
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
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    // Image manipulation variables
    private Point _lastMousePosition;
    private bool _isDragging = false;
    private double _currentZoom = 1.0;
    private double _currentRotation = 0.0;
    private TransformGroup _imageTransformGroup;
    private ScaleTransform _scaleTransform;
    private RotateTransform _rotateTransform;
    private TranslateTransform _translateTransform;

    // Drawing mode variables
    private Point _startPoint;
    private bool _isDrawingLine = false;
    private bool _isLineToolActive = false;
    private bool _isCalibrating = false;
    private Line? _calibrationLine;
    private const int GRID_SIZE = 20;

    // Drag & Drop for stamps
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

        // Register for clear survey points message
        WeakReferenceMessenger.Default.Register<ClearSurveyPointsMessage>(this, (r, m) =>
        {
            ClearVisualSurveyPoints();
        });

        // Register for redraw survey points message
        WeakReferenceMessenger.Default.Register<RedrawSurveyPointsMessage>(this, (r, m) =>
        {
            RedrawSurveyPoints(m.SurveyPoints);
        });

        // ✅ NOU: Register for redraw map elements (obstacole) message
        WeakReferenceMessenger.Default.Register<RedrawMapElementsMessage>(this, (r, m) =>
        {
            RedrawMapElements(m.MapElements);
        });

        // ✅ NOU: Register pentru restaurarea schiței vectoriale după load proiect
        WeakReferenceMessenger.Default.Register<RestoreSketchMessage>(this, (r, m) =>
        {
            EditorCanvas.Strokes.Clear();
            foreach (var stroke in m.Strokes)
                EditorCanvas.Strokes.Add(stroke);

            // Afișăm panoul de desenare ca să fie vizibilă schița restaurată
            DrawingOverlay.Visibility = Visibility.Visible;
        });

        // ✅ NOU: Butonul "Salvare" din panou → colectăm schița și salvăm tot
        WeakReferenceMessenger.Default.Register<RequestSaveWithSketchMessage>(this, (r, m) =>
        {
            CollectSketchAndSave();
        });

        // ✅ NOU: Afișăm indicatorul vizual pentru poziția optimă a routerului
        WeakReferenceMessenger.Default.Register<ShowOptimalRouterMessage>(this, (r, m) =>
        {
            DrawOptimalRouterIndicator(m.OptimalPoint);
        });

        InitializeImageTransforms();

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.FloorPlan) && _viewModel.FloorPlan != null)
            {
                ResetImageTransformations();
            }
        };
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
            MapCanvas.Width = _viewModel.FloorPlan.PixelWidth;
            MapCanvas.Height = _viewModel.FloorPlan.PixelHeight;
            MapContent.Width = _viewModel.FloorPlan.PixelWidth;
            MapContent.Height = _viewModel.FloorPlan.PixelHeight;
        }
    }

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel.FloorPlan == null) return;

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
        if (_viewModel.FloorPlan == null) return;

        // Calibrare are prioritate absolută
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
                // Plasează punct de măsurare (fostul click dreapta)
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
                // Pointer / navigare → drag normal
                _isDragging = true;
                _lastMousePosition = e.GetPosition(MapCanvas);
                MapContent.CaptureMouse();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Plasează un element (obstacol/router) direct la coordonatele click-ului.
    /// Paradigma "Selectează unealtă → Click pe hartă".
    /// </summary>
    private void PlaceObstacleAtPoint(Point pos, ElementType type)
    {
        var element = CreateEmojiElement(type);

        // Centrăm elementul pe click
        Canvas.SetLeft(element, pos.X - 20);
        Canvas.SetTop(element, pos.Y - 20);

        ObjectsCanvas.Children.Add(element);

        _viewModel.RegisterMapElementCommand.Execute(new MapElement
        {
            Type = type,
            X = pos.X - 20,
            Y = pos.Y - 20
        });

        _viewModel.StatusMessage = $"{GetEmoji(type)}  {type} plasat la X={pos.X:F0}, Y={pos.Y:F0}";
    }

    private void MapContent_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel.FloorPlan == null) return;

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
        if (_viewModel.FloorPlan == null)
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
        if (_viewModel.FloorPlan == null) return;

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
                _viewModel.StatusMessage = "Calibrare anulată.";
            }
            else
            {
                ResetImageTransformations();
            }
            e.Handled = true;
        }
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
        dot.ToolTip = $"Punct de Măsurare\n" +
                     $"Coordonate: ({position.X:F0}, {position.Y:F0})\n" +
                     $"Semnal: {signalStrength} dBm ({signalQuality})";
    }

    private void RedrawSurveyPoints(IReadOnlyList<SurveyPoint> surveyPoints)
    {
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
    }

    #endregion

    #region Map Elements (Obstacole)

    /// <summary>
    /// ✅ NOU: Redesenează obstacolele pe ObjectsCanvas după încărcarea unui proiect
    /// </summary>
    private void RedrawMapElements(IReadOnlyList<MapElement> elements)
    {
        ObjectsCanvas.Children.Clear();

        foreach (var element in elements)
        {
            var visual = CreateEmojiElement(element.Type);
            Canvas.SetLeft(visual, element.X);
            Canvas.SetTop(visual, element.Y);
            ObjectsCanvas.Children.Add(visual);
        }
    }

    #endregion

    #region Drawing Mode

    private void StartSketch_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlay.Visibility = Visibility.Visible;
        EditorCanvas.Strokes.Clear();
        ObjectsCanvas.Children.Clear();
        SetPenAttributes(Colors.Black, 6);
        _isLineToolActive = false;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        _viewModel.StatusMessage = "Mod Desenare activat. Desenați planul apartamentului.";
    }

    private void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.FloorPlan == null)
        {
            MessageBox.Show("Încarcă mai întâi un plan!", "Atenție");
            return;
        }

        _isCalibrating = true;
        Mouse.OverrideCursor = Cursors.Cross;
        _viewModel.StatusMessage = "MOD CALIBRARE: Ține click stânga apăsat pe hartă și trage o linie.";
    }

    private void SetBasicMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsExpertMode = false;
        _viewModel.StatusMessage = "Mod Basic activat.";
    }

    private void SetExpertMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsExpertMode = true;
        _viewModel.StatusMessage = "Mod Expert activat — toate funcțiile sunt disponibile.";
    }

    #region Toolbox Handlers — unealta activă determină acțiunea click-ului pe hartă

    /// <summary>
    /// Returnează ce unealtă e selectată curent în Toolbox.
    /// Folosit în MapContent_MouseLeftButtonDown pentru a decide acțiunea.
    /// </summary>
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
        return "SurveyPoint"; // fallback
    }

    private void UpdateActiveToolLabel(string toolName)
    {
        if (ActiveToolLabel == null) return;
        ActiveToolLabel.Text = toolName switch
        {
            "SurveyPoint" => "● Punct Măsurare activ",
            "Pointer" => "● Navigare / Mutare activ",
            "Router" => "● Router Virtual activ",
            "ConcreteWall" => "● Zid Beton activ  −12 dBm",
            "BrickWall" => "● Cărămidă activă  −6 dBm",
            "WoodDoor" => "● Ușă Lemn activă  −3 dBm",
            "Fridge" => "● Frigider activ  −15 dBm",
            "TV" => "● Televizor activ  −5 dBm",
            "Microwave" => "● Microunde activ  −8 dBm",
            "DrawWall" => "● Desenare Perete activă",
            "DrawLine" => "● Linie Dreaptă activă",
            "Eraser" => "● Radieră activă",
            _ => "● Unealtă activă"
        };
    }

    private void ToolSurveyPoint_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("SurveyPoint");
        MapContent.Cursor = Cursors.Cross;
        _viewModel.StatusMessage = "Click stânga pe hartă pentru a plasa un punct de măsurare.";
    }

    private void ToolPointer_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("Pointer");
        MapContent.Cursor = Cursors.Hand;
        _viewModel.StatusMessage = "Mod navigare: trage harta cu click stânga.";
    }

    private void ToolObstacle_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag?.ToString() ?? "";
        UpdateActiveToolLabel(tag);
        MapContent.Cursor = Cursors.Cross;

        string desc = tag switch
        {
            "ConcreteWall" => "Zid Beton (−12 dBm)",
            "BrickWall" => "Cărămidă (−6 dBm)",
            "WoodDoor" => "Ușă Lemn (−3 dBm)",
            "Fridge" => "Frigider (−15 dBm)",
            "TV" => "Televizor (−5 dBm)",
            "Microwave" => "Microunde (−8 dBm)",
            "Router" => "Router Virtual",
            _ => tag
        };
        _viewModel.StatusMessage = $"Click stânga pe hartă pentru a plasa: {desc}";
    }

    private void ToolDrawWall_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("DrawWall");
        Tool_Wall_Click(sender, e);
    }

    private void ToolDrawLine_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("DrawLine");
        Tool_Line_Click(sender, e);
    }

    private void ToolEraser_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        UpdateActiveToolLabel("Eraser");
        Tool_Eraser_Click(sender, e);
    }

    #endregion

    private void FinishCalibration(Point start, Point end)
    {
        _isCalibrating = false;
        Mouse.OverrideCursor = null;

        DrawingOverlay.Visibility = Visibility.Collapsed;

        double pixelDistance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));

        if (pixelDistance < 50)
        {
            MessageBox.Show("Linia este prea scurtă. Trage o linie mai lungă pentru o precizie mai bună.",
                "Calibrare eșuată", MessageBoxButton.OK, MessageBoxImage.Warning);
            _viewModel.StatusMessage = "Calibrare anulată (linie prea scurtă).";
            return;
        }

        var dialog = new CalibrationDialog();
        dialog.Owner = this;

        if (dialog.ShowDialog() == true)
        {
            double realMeters = dialog.ResultMeters;
            _viewModel.PixelsPerMeter = pixelDistance / realMeters;
            _viewModel.IsCalibrated = true;

            _viewModel.StatusMessage = $"✅ Calibrare reușită! Scara setată la: {_viewModel.PixelsPerMeter:F1} pixeli = 1 metru.";
            MessageBox.Show(
                $"Calibrare completă!\n\nDistanța de {pixelDistance:F0} pixeli a fost mapată la {realMeters} metri.\n\n" +
                $"Aplicația știe acum că 1 metru = {_viewModel.PixelsPerMeter:F0} pixeli.",
                "Succes", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            _viewModel.StatusMessage = "Calibrare anulată de utilizator.";
        }
    }

    private void Tool_Wall_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = false;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        SetPenAttributes(Colors.Black, 6);
    }

    private void Tool_Detail_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = false;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        SetPenAttributes(Colors.Gray, 2);
    }

    private void Tool_Eraser_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = false;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
    }

    private void Tool_Line_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = true;
        _isCalibrating = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.None;
        EditorCanvas.IsHitTestVisible = true;
    }

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

        // Adăugăm linia (peretele) pe canvas
        var stylusPointCollection = new StylusPointCollection();
        stylusPointCollection.Add(new StylusPoint(_startPoint.X, _startPoint.Y));
        stylusPointCollection.Add(new StylusPoint(endPoint.X, endPoint.Y));

        var stroke = new Stroke(stylusPointCollection)
        {
            DrawingAttributes = EditorCanvas.DefaultDrawingAttributes.Clone()
        };
        EditorCanvas.Strokes.Add(stroke);

        // ✅ MODIFICAT: Propunem calibrarea după FIECARE linie dreaptă (≥ 50px)
        double pixelDistance = Math.Sqrt(
            Math.Pow(endPoint.X - _startPoint.X, 2) +
            Math.Pow(endPoint.Y - _startPoint.Y, 2));

        if (pixelDistance >= 50)
        {
            // Mesaj diferit dacă e deja calibrat (permite recalibrare cu linia curentă)
            string mesaj = _viewModel.IsCalibrated
                ? $"Vrei să recalibrezi scara folosind acest perete?\n\n" +
                  $"Scară curentă: {_viewModel.PixelsPerMeter:F1} px/m\n" +
                  $"Lungime linie: {pixelDistance:F0} px"
                : "Vrei să calibrezi scara hărții folosind acest perete?\n\n" +
                  "Dacă știi lungimea reală a acestui perete, poți face calibrarea acum.";

            var result = MessageBox.Show(
                mesaj,
                "Calibrare Rapidă",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var dialog = new CalibrationDialog();
                dialog.Owner = this;

                if (dialog.ShowDialog() == true)
                {
                    _viewModel.PixelsPerMeter = pixelDistance / dialog.ResultMeters;
                    _viewModel.IsCalibrated = true;
                    _viewModel.StatusMessage = $"✅ Scară setată: {_viewModel.PixelsPerMeter:F1} px/m " +
                                               $"({pixelDistance:F0} px = {dialog.ResultMeters} m)";
                }
            }
        }
    }

    private void SaveSketch_Click(object sender, RoutedEventArgs e)
    {
        int width = (int)EditorCanvas.ActualWidth;
        int height = (int)EditorCanvas.ActualHeight;
        if (width == 0 || height == 0) { width = 800; height = 600; }

        // Renderăm PNG pentru FloorPlan
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96d, 96d, PixelFormats.Pbgra32);

        var bg = new DrawingVisual();
        using (var ctx = bg.RenderOpen())
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        rtb.Render(bg);
        rtb.Render(EditorCanvas);
        rtb.Render(ObjectsCanvas);
        rtb.Freeze();

        // Setăm PNG-ul ca FloorPlan activ
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

        DrawingOverlay.Visibility = Visibility.Collapsed;
        _viewModel.StatusMessage = "Schița setată ca plan!";

        // ✅ Întrebăm dacă vrea să salveze proiectul complet
        var result = MessageBox.Show(
            "Schița a fost setată ca plan!\n\nVrei să salvezi proiectul complet acum?\n" +
            "(schiță + puncte + obstacole + calibrare)",
            "Salvare Proiect",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            CollectSketchAndSave();
    }

    private void CancelSketch_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlay.Visibility = Visibility.Collapsed;
        _viewModel.StatusMessage = "Desenare anulată.";
    }

    #endregion

    #region Stamp Objects (Router, Fridge, etc.)

    // Mapare ElementType → emoji
    private static string GetEmoji(ElementType type) => type switch
    {
        ElementType.Router => "📡",
        ElementType.Fridge => "❄️",
        ElementType.Microwave => "📻",
        ElementType.ConcreteWall => "🧱",
        ElementType.BrickWall => "🧱",
        ElementType.WoodDoor => "🚪",
        ElementType.TV => "📺",
        _ => "📦"
    };

    private void AddRouter_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.Router);

    private void AddFridge_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.Fridge);

    private void AddMicrowave_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.Microwave);

    private void AddConcreteWall_Click(object sender, RoutedEventArgs e)
        => AddElementToCanvas(ElementType.ConcreteWall);

    /// <summary>
    /// Creează un element vizual cu emoji și îl plasează la centrul canvas-ului.
    /// Folosit de butoanele din Drawing Overlay toolbar.
    /// </summary>
    private void AddElementToCanvas(ElementType type)
    {
        var element = CreateEmojiElement(type);
        Canvas.SetLeft(element, 200);
        Canvas.SetTop(element, 200);
        ObjectsCanvas.Children.Add(element);
    }

    /// <summary>
    /// Creează elementul vizual emoji (Border + TextBlock) pentru un tip de obstacol.
    /// </summary>
    private Border CreateEmojiElement(ElementType type)
    {
        var tb = new TextBlock
        {
            Text = GetEmoji(type),
            FontSize = 24,
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
            Child = tb,
            Cursor = Cursors.Hand,
            Tag = type,
            ToolTip = $"{GetEmoji(type)}  {type}  (−{new MapElement { Type = type }.SignalPenalty} dBm)"
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
    /// Desenează indicatorul vizual (cerc verde + stea) pentru poziția optimă a routerului.
    /// </summary>
    private void DrawOptimalRouterIndicator(Point pos)
    {
        // Ștergem indicatorul vechi dacă există
        var oldIndicators = ObjectsCanvas.Children
            .OfType<FrameworkElement>()
            .Where(e => e.Tag as string == "OptimalRouter")
            .ToList();
        foreach (var old in oldIndicators)
            ObjectsCanvas.Children.Remove(old);

        // Cerc exterior pulsant (verde transparent)
        var outerRing = new System.Windows.Shapes.Ellipse
        {
            Width = 56,
            Height = 56,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 0, 220, 0)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Tag = "OptimalRouter"
        };

        // Cerc interior plin (verde semitransparent)
        var innerCircle = new System.Windows.Shapes.Ellipse
        {
            Width = 40,
            Height = 40,
            Fill = new SolidColorBrush(Color.FromArgb(100, 0, 255, 0)),
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 3,
            Tag = "OptimalRouter"
        };

        // Steluță centrală
        var star = new TextBlock
        {
            Text = "⭐",
            FontSize = 18,
            Tag = "OptimalRouter"
        };

        // Etichetă "OPTIM"
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

        Canvas.SetLeft(star, pos.X - 11);
        Canvas.SetTop(star, pos.Y - 13);

        Canvas.SetLeft(label, pos.X - 13);
        Canvas.SetTop(label, pos.Y + 21);

        ObjectsCanvas.Children.Add(outerRing);
        ObjectsCanvas.Children.Add(innerCircle);
        ObjectsCanvas.Children.Add(star);
        ObjectsCanvas.Children.Add(label);

        MessageBox.Show(
            $"Am marcat cu ⭐ locul optim pentru router!\n\n" +
            $"Coordonate: X={pos.X:F0}, Y={pos.Y:F0}\n\n" +
            $"Algoritmul Min-Max a scanat întreg planul și a ales poziția care\n" +
            $"garantează că cel mai izolat punct din casă primește semnal maxim,\n" +
            $"luând în calcul toți pereții și obstacolele desenate.",
            "Poziție Optimă Găsită",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Colectează schița curentă din EditorCanvas (ISF + PNG render),
    /// apoi apelează SaveProjectWithSketch pe ViewModel.
    /// Apelat atât din butonul "Salvare" cât și din "Salvează Schița".
    /// </summary>
    private void CollectSketchAndSave()
    {
        System.Windows.Ink.StrokeCollection? strokes = null;
        System.Windows.Media.Imaging.BitmapSource? png = null;

        // Colectăm schița vectorială dacă există linii desenate
        if (EditorCanvas.Strokes.Count > 0)
        {
            strokes = EditorCanvas.Strokes;

            // Renderăm PNG-ul schiței (fundal alb + linii + obiecte)
            int w = (int)EditorCanvas.ActualWidth;
            int h = (int)EditorCanvas.ActualHeight;
            if (w > 0 && h > 0)
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, PixelFormats.Pbgra32);

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

    protected override void OnClosed(EventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<ClearSurveyPointsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RedrawSurveyPointsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RedrawMapElementsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RestoreSketchMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RequestSaveWithSketchMessage>(this);
        WeakReferenceMessenger.Default.Unregister<ShowOptimalRouterMessage>(this);
        base.OnClosed(e);
    }
}