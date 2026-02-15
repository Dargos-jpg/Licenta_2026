using CommunityToolkit.Mvvm.Messaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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

    // ✅ Drawing mode variables
    private Point _startPoint;
    private bool _isDrawingLine = false;
    private bool _isLineToolActive = false;
    private const int GRID_SIZE = 20; // Grid snapping size

    // ✅ Drag & Drop for stamps
    private Image? _draggedImage;
    private Point _dragOffset;
    private bool _isDraggingElement = false;

    public MainWindow()
    {
        InitializeComponent();

        // Dependency injection setup
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

        // Setup image transformations
        InitializeImageTransforms();

        // Subscribe to floor plan changes
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

        _isDragging = true;
        _lastMousePosition = e.GetPosition(MapCanvas);
        MapContent.CaptureMouse();
        e.Handled = true;
    }

    private void MapContent_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _viewModel.FloorPlan == null) return;

        Point currentPosition = e.GetPosition(MapCanvas);
        double deltaX = currentPosition.X - _lastMousePosition.X;
        double deltaY = currentPosition.Y - _lastMousePosition.Y;

        _translateTransform.X += deltaX;
        _translateTransform.Y += deltaY;

        _lastMousePosition = currentPosition;
        e.Handled = true;
    }

    private void MapContent_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
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
            ResetImageTransformations();
            e.Handled = true;
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
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        _viewModel.StatusMessage = "Mod Desenare activat. Desenați planul apartamentului.";
    }

    private void Tool_Wall_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        SetPenAttributes(Colors.Black, 6);
    }

    private void Tool_Detail_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.Ink;
        SetPenAttributes(Colors.Gray, 2);
    }

    private void Tool_Eraser_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = false;
        EditorCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
    }

    private void Tool_Line_Click(object sender, RoutedEventArgs e)
    {
        _isLineToolActive = true;
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

    /// <summary>
    /// ✅ CRITICAL: Mouse down logic for both free drawing and rubber band line
    /// </summary>
    private void EditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // ✅ Dacă NU suntem pe modul Linie Dreaptă (suntem pe Perete/Creion/Radieră)
        if (!_isLineToolActive)
        {
            // ✅ IMPORTANT: Nu setăm e.Handled = true aici!
            // Lăsăm evenimentul să "curgă" către InkCanvas pentru desenare liberă
            return;
        }

        // ✅ Doar dacă suntem în modul Linie Dreaptă, procesăm evenimentul manual
        _isDrawingLine = true;
        Point rawPoint = e.GetPosition(EditorCanvas);
        _startPoint = SnapToGrid(rawPoint);

        PreviewLine.X1 = _startPoint.X;
        PreviewLine.Y1 = _startPoint.Y;
        PreviewLine.X2 = _startPoint.X;
        PreviewLine.Y2 = _startPoint.Y;
        PreviewLine.Visibility = Visibility.Visible;

        EditorCanvas.CaptureMouse();
        e.Handled = true; // ✅ Doar aici marcăm evenimentul ca procesat
    }

    private void EditorCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        // Doar funcționează pentru Rubber Band Line
        if (!_isDrawingLine) return;

        Point currentRaw = e.GetPosition(EditorCanvas);
        Point snapped = SnapToGrid(currentRaw);

        PreviewLine.X2 = snapped.X;
        PreviewLine.Y2 = snapped.Y;
    }

    private void EditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Doar funcționează pentru Rubber Band Line
        if (!_isDrawingLine) return;

        _isDrawingLine = false;
        PreviewLine.Visibility = Visibility.Collapsed;
        EditorCanvas.ReleaseMouseCapture();

        Point endPoint = new Point(PreviewLine.X2, PreviewLine.Y2);

        // Nu desenăm dacă a fost doar un click fără drag
        if (_startPoint == endPoint) return;

        // Convert visual line to ink stroke
        var stylusPointCollection = new StylusPointCollection();
        stylusPointCollection.Add(new StylusPoint(_startPoint.X, _startPoint.Y));
        stylusPointCollection.Add(new StylusPoint(endPoint.X, endPoint.Y));

        var stroke = new Stroke(stylusPointCollection)
        {
            DrawingAttributes = EditorCanvas.DefaultDrawingAttributes.Clone()
        };

        EditorCanvas.Strokes.Add(stroke);
    }

    private Point SnapToGrid(Point original)
    {
        double snappedX = Math.Round(original.X / GRID_SIZE) * GRID_SIZE;
        double snappedY = Math.Round(original.Y / GRID_SIZE) * GRID_SIZE;
        return new Point(snappedX, snappedY);
    }

    private void SaveSketch_Click(object sender, RoutedEventArgs e)
    {
        int width = (int)EditorCanvas.ActualWidth;
        int height = (int)EditorCanvas.ActualHeight;

        if (width == 0 || height == 0)
        {
            width = 800;
            height = 600;
        }

        RenderTargetBitmap rtb = new RenderTargetBitmap(
            width, height, 96d, 96d, PixelFormats.Pbgra32);

        // White background
        DrawingVisual backgroundVisual = new DrawingVisual();
        using (DrawingContext ctx = backgroundVisual.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        }

        rtb.Render(backgroundVisual);
        rtb.Render(EditorCanvas);
        rtb.Render(ObjectsCanvas); // Include stamps

        // Convert to BitmapImage
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using (var stream = new System.IO.MemoryStream())
        {
            encoder.Save(stream);
            stream.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = stream;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            _viewModel.FloorPlan = bitmapImage;
        }

        _viewModel.ClearSurveyPointsCommand.Execute(null);
        DrawingOverlay.Visibility = Visibility.Collapsed;

        _viewModel.StatusMessage = "Schița a fost salvată și setată ca plan!";
        MessageBox.Show("Schița a fost salvată și setată ca plan!", "Succes",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CancelSketch_Click(object sender, RoutedEventArgs e)
    {
        DrawingOverlay.Visibility = Visibility.Collapsed;
        _viewModel.StatusMessage = "Desenare anulată.";
    }

    #endregion

    #region Stamp Objects (Router, Fridge, etc.)

    private void AddRouter_Click(object sender, RoutedEventArgs e)
    {
        AddElementToCanvas("pack://application:,,,/Images/router_icon.png", ElementType.Router);
    }

    private void AddFridge_Click(object sender, RoutedEventArgs e)
    {
        AddElementToCanvas("pack://application:,,,/Images/fridge_icon.png", ElementType.Fridge);
    }

    private void AddMicrowave_Click(object sender, RoutedEventArgs e)
    {
        AddElementToCanvas("pack://application:,,,/Images/microwave_icon.png", ElementType.Microwave);
    }

    private void AddConcreteWall_Click(object sender, RoutedEventArgs e)
    {
        AddElementToCanvas("pack://application:,,,/Images/wall_icon.png", ElementType.ConcreteWall);
    }

    private void AddElementToCanvas(string iconPath, ElementType type)
    {
        try
        {
            Image icon = new Image
            {
                Source = new BitmapImage(new Uri(iconPath)),
                Width = 40,
                Height = 40,
                Cursor = Cursors.Hand,
                Tag = type
            };

            Canvas.SetLeft(icon, 200);
            Canvas.SetTop(icon, 200);

            // ✅ Attach mouse events DOAR la imagine, nu la InkCanvas
            icon.MouseLeftButtonDown += Element_MouseDown;
            icon.MouseLeftButtonUp += Element_MouseUp;
            icon.MouseMove += Element_MouseMove;

            ObjectsCanvas.Children.Add(icon);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nu s-a putut încărca iconița: {ex.Message}", "Eroare",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ✅ Drag & Drop Logic - DOAR pentru stampile
    private void Element_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggedImage = sender as Image;
        _isDraggingElement = true;
        _dragOffset = e.GetPosition(_draggedImage);
        _draggedImage?.CaptureMouse();
        e.Handled = true; // ✅ Oprește propagarea la InkCanvas
    }

    private void Element_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingElement && _draggedImage != null)
        {
            Point currentPos = e.GetPosition(ObjectsCanvas);
            Canvas.SetLeft(_draggedImage, currentPos.X - _dragOffset.X);
            Canvas.SetTop(_draggedImage, currentPos.Y - _dragOffset.Y);
            e.Handled = true; // ✅ Oprește propagarea
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

            e.Handled = true; // ✅ Oprește propagarea
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<ClearSurveyPointsMessage>(this);
        WeakReferenceMessenger.Default.Unregister<RedrawSurveyPointsMessage>(this);
        base.OnClosed(e);
    }
}