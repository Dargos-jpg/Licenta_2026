using System.Collections.Generic;
using Microsoft.ML.Data;

namespace WifiSignalOptimizer.Models;

/// <summary>
/// per-channel analysis with interference metrics
/// </summary>
public class ChannelAnalysis
{
    /// <summary>
    /// channel number (1-13 for 2.4GHz)
    /// </summary>
    public int Channel { get; set; }

    /// <summary>
    /// mathematical interference score (lower is better)
    /// </summary>
    public double InterferenceScore { get; set; }

    /// <summary>
    /// number of networks on the exact same channel (co-channel interference)
    /// </summary>
    public int CoChannelNeighbors { get; set; }

    /// <summary>
    /// number of networks on adjacent channels (adjacent channel interference)
    /// </summary>
    public int AdjacentNeighbors { get; set; }

    /// <summary>
    /// AI/ML classification status: "Optim", "Aglomerat", "Critic"
    /// </summary>
    public string Status { get; set; } = "Necunoscut";
}

/// <summary>
/// AI-powered WiFi network health report with ML.NET K-Means clustering analysis
/// </summary>
public class OptimizationReport
{
    /// <summary>
    /// target network SSID being analyzed
    /// </summary>
    public string TargetSSID { get; set; } = string.Empty;

    /// <summary>
    /// current channel the network is operating on
    /// </summary>
    public int CurrentChannel { get; set; }

    /// <summary>
    /// AI-recommended optimal channel with least interference
    /// </summary>
    public int BestChannel { get; set; }

    /// <summary>
    /// overall network health score (0-100, higher is better)
    /// </summary>
    public double OverallHealthScore { get; set; }

    /// <summary>
    /// detailed analysis for each channel (1-13)
    /// </summary>
    public List<ChannelAnalysis> ChannelDetails { get; set; } = [];

    /// <summary>
    /// human-readable actionable recommendations
    /// </summary>
    public List<string> ActionableInsights { get; set; } = [];
}

/// <summary>
/// ML.NET training data: channel features for K-Means clustering
/// </summary>
public class ChannelData
{
    /// <summary>
    /// channel number (1-13)
    /// </summary>
    public float Channel { get; set; }

    /// <summary>
    /// interference score (feature for ML)
    /// </summary>
    public float InterferenceScore { get; set; }

    /// <summary>
    /// co-channel neighbor count (feature for ML)
    /// </summary>
    public float CoChannelNeighbors { get; set; }

    /// <summary>
    /// adjacent channel neighbor count (feature for ML)
    /// </summary>
    public float AdjacentNeighbors { get; set; }
}

/// <summary>
/// ML.NET K-Means clustering prediction output
/// </summary>
public class ClusterPrediction
{
    /// <summary>
    /// predicted cluster ID (0, 1, or 2 for 3-cluster K-Means)
    /// </summary>
    [ColumnName("PredictedLabel")]
    public uint PredictedClusterId { get; set; }

    /// <summary>
    /// distance to each cluster centroid
    /// </summary>
    [ColumnName("Score")]
    public float[]? Distances { get; set; }
}
