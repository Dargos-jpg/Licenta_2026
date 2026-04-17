namespace WifiSignalOptimizer.Models;

/// <summary>
/// Punct de măsurare semnal WiFi folosit la generarea heatmap-ului.
/// </summary>
public sealed record HeatmapPoint(double X, double Y, int SignalStrength);
