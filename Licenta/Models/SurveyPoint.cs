using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Models;

/// <summary>
/// Represents a measurement point on the floor plan with WiFi data.
/// Critical for AI-powered heat map generation and signal optimization.
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
    /// Timestamp when this measurement was taken
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// All WiFi networks detected at this location
    /// </summary>
    public required List<WifiNetwork> DetectedNetworks { get; init; }

    /// <summary>
    /// Optional user notes for this measurement point
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Gets the strongest signal at this point
    /// </summary>
    public int? StrongestSignal =>
        DetectedNetworks.Count > 0
            ? DetectedNetworks.Max(n => n.SignalStrength)
            : null;

    /// <summary>
    /// Gets the weakest signal at this point
    /// </summary>
    public int? WeakestSignal =>
        DetectedNetworks.Count > 0
            ? DetectedNetworks.Min(n => n.SignalStrength)
            : null;

    public override string ToString() =>
        $"Point ({X:F1}, {Y:F1}) - {DetectedNetworks.Count} networks @ {Timestamp:HH:mm:ss}";
}