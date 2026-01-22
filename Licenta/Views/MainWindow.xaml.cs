using CommunityToolkit.Mvvm.Messaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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

    public MainWindow()
    {
        InitializeComponent();

        // Dependency injection setup (in production, use DI container)
        var wifiScanner = new WifiScannerService();
        var mapService = new MapService();
        _viewModel = new MainViewModel(wifiScanner, mapService);

        DataContext = _viewModel;

        // Register for clear survey points message
        WeakReferenceMessenger.Default.Register<ClearSurveyPointsMessage>(this, (r, m) =>
        {
            ClearVisualSurveyPoints();
        });

        // Setup image transformations
        InitializeImageTransforms();

        // Subscribe to floor plan changes to reset transformations
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.FloorPlan) && _viewModel.FloorPlan != null)
            {
                ResetImageTransformations();
            }
        };
    }

    /// <summary>
    /// Initialize transform groups for image manipulation
    /// </summary>
    private void InitializeImageTransforms()
    {
        _scaleTransform = new ScaleTransform(1.0, 1.0);
        _rotateTransform = new RotateTransform(0);
        _translateTransform = new TranslateTransform(0, 0);

        _imageTransformGroup = new TransformGroup();
        _imageTransformGroup.Children.Add(_scaleTransform);
        _imageTransformGroup.Children.Add(_rotateTransform);
        _imageTransformGroup.Children.Add(_translateTransform);

        FloorPlanImage.RenderTransform = _imageTransformGroup;
        FloorPlanImage.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    /// <summary>
    /// Reset all image transformations when new image is loaded
    /// </summary>
    private void ResetImageTransformations()
    {
        _currentZoom = 1.0;
        _currentRotation = 0.0;
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        _rotateTransform.Angle = 0;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;

        // Resize canvas to image dimensions
        if (_viewModel.FloorPlan != null)
        {
            MapCanvas.Width = _viewModel.FloorPlan.PixelWidth;
            MapCanvas.Height = _viewModel.FloorPlan.PixelHeight;
        }
    }

    /// <summary>
    /// Handle mouse wheel for zooming
    /// </summary>
    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel.FloorPlan == null) return;

        const double zoomSpeed = 0.1;
        double zoomChange = e.Delta > 0 ? zoomSpeed : -zoomSpeed;

        _currentZoom += zoomChange;
        _currentZoom = Math.Clamp(_currentZoom, 0.1, 5.0); // Min 10%, Max 500%

        _scaleTransform.ScaleX = _currentZoom;
        _scaleTransform.ScaleY = _currentZoom;

        e.Handled = true;
    }

    /// <summary>
    /// Start dragging the image
    /// </summary>
    private void FloorPlanImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.FloorPlan == null) return;

        _isDragging = true;
        _lastMousePosition = e.GetPosition(MapCanvas);
        FloorPlanImage.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// Move the image while dragging
    /// </summary>
    private void FloorPlanImage_MouseMove(object sender, MouseEventArgs e)
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

    /// <summary>
    /// Stop dragging the image
    /// </summary>
    private void FloorPlanImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            FloorPlanImage.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Rotate image with keyboard (R key clockwise, Shift+R counter-clockwise)
    /// </summary>
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
            // Reset all transformations with ESC key
            ResetImageTransformations();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles map canvas clicks to record survey points
    /// </summary>
    private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.FloorPlan == null || _isDragging)
            return;

        var clickPosition = e.GetPosition(SurveyPointsCanvas);

        // Get strongest signal from current scan
        int strongestSignal = _viewModel.VisibleNetworks.Count > 0
            ? _viewModel.VisibleNetworks.Max(n => n.SignalStrength)
            : -90; // Default weak signal if no networks

        // Draw survey point with color based on signal strength
        DrawSurveyPoint(clickPosition, strongestSignal);

        // Record the survey point with current WiFi data
        _viewModel.RecordPointCommand.Execute(clickPosition);
    }

    /// <summary>
    /// Draws a survey point on the map with color based on signal strength
    /// </summary>
    /// <param name="position">Position on canvas</param>
    /// <param name="signalStrength">Signal strength in dBm</param>
    private void DrawSurveyPoint(Point position, int signalStrength)
    {
        const double dotSize = 14;
        const double outlineSize = 18;

        // Determine color based on signal strength
        Brush signalColor = GetSignalColor(signalStrength);

        // Outer white outline for visibility
        var outline = new Ellipse
        {
            Width = outlineSize,
            Height = outlineSize,
            Fill = Brushes.White,
            Stroke = Brushes.DarkGray,
            StrokeThickness = 1.5
        };

        // Inner colored dot based on signal
        var dot = new Ellipse
        {
            Width = dotSize,
            Height = dotSize,
            Fill = signalColor,
            Stroke = GetSignalStrokeColor(signalStrength),
            StrokeThickness = 2
        };

        // Position elements (centered on click point)
        Canvas.SetLeft(outline, position.X - outlineSize / 2);
        Canvas.SetTop(outline, position.Y - outlineSize / 2);
        Canvas.SetLeft(dot, position.X - dotSize / 2);
        Canvas.SetTop(dot, position.Y - dotSize / 2);

        // Add to canvas
        SurveyPointsCanvas.Children.Add(outline);
        SurveyPointsCanvas.Children.Add(dot);

        // Add tooltip showing signal info
        string signalQuality = signalStrength > -50 ? "Excelent" :
                              signalStrength > -70 ? "Mediu" : "Slab";
        dot.ToolTip = $"Punct de Măsurare\n" +
                     $"Coordonate: ({position.X:F0}, {position.Y:F0})\n" +
                     $"Semnal: {signalStrength} dBm ({signalQuality})";
    }

    /// <summary>
    /// Get color based on signal strength
    /// </summary>
    private Brush GetSignalColor(int signalStrength)
    {
        return signalStrength switch
        {
            > -50 => new SolidColorBrush(Color.FromRgb(76, 175, 80)),   // Green - Excellent
            > -70 => new SolidColorBrush(Color.FromRgb(255, 193, 7)),   // Yellow - Medium
            _ => new SolidColorBrush(Color.FromRgb(244, 67, 54))        // Red - Poor
        };
    }

    /// <summary>
    /// Get stroke color based on signal strength
    /// </summary>
    private Brush GetSignalStrokeColor(int signalStrength)
    {
        return signalStrength switch
        {
            > -50 => new SolidColorBrush(Color.FromRgb(56, 142, 60)),   // Dark Green
            > -70 => new SolidColorBrush(Color.FromRgb(245, 124, 0)),   // Dark Yellow/Orange
            _ => new SolidColorBrush(Color.FromRgb(198, 40, 40))        // Dark Red
        };
    }

    /// <summary>
    /// Clears all visual survey points from canvas
    /// </summary>
    private void ClearVisualSurveyPoints()
    {
        SurveyPointsCanvas.Children.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<ClearSurveyPointsMessage>(this);
        base.OnClosed(e);
    }
}