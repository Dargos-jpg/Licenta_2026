using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Models;

/// <summary>
/// represents a measurement point on the floor plan with WiFi data.
/// used for heatmap generation and signal optimization.
/// </summary>
public sealed class SurveyPoint
{
    /// <summary>
    /// X coordinate on the floor plan canvas
    /// </summary>
    public required double X { get; init; }

    /// <summary>
    /// Y coordinate on the floor plan canvas
    /// </summary>
    public required double Y { get; init; }

    /// <summary>
    /// timestamp when this measurement was taken
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// all WiFi networks detected at this location
    /// </summary>
    public required List<WifiNetwork> DetectedNetworks { get; init; }

    /// <summary>
    /// optional user notes for this measurement point
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// gets the strongest signal at this point
    /// </summary>
    public int? StrongestSignal =>
        DetectedNetworks.Count > 0
            ? DetectedNetworks.Max(n => n.SignalStrength)
            : null;

    /// <summary>
    /// gets the weakest signal at this point
    /// </summary>
    public int? WeakestSignal =>
        DetectedNetworks.Count > 0
            ? DetectedNetworks.Min(n => n.SignalStrength)
            : null;

    /// <summary>
    /// facade property required by HeatmapService.
    /// returns the strongest signal (coverage map) or -100 if empty.
    /// </summary>
    public int SignalStrength => StrongestSignal ?? -100;

    public override string ToString() =>
        $"Point ({X:F1}, {Y:F1}) - {DetectedNetworks.Count} networks @ {Timestamp:HH:mm:ss}";
}