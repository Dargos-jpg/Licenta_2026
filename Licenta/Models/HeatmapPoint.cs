namespace WifiSignalOptimizer.Models;

/// <summary>
/// WiFi signal measurement point used when generating the heatmap.
/// </summary>
public sealed record HeatmapPoint(double X, double Y, int SignalStrength);
