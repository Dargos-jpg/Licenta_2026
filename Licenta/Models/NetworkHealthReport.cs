using System.Collections.Generic;

namespace WifiSignalOptimizer.Models;

/// <summary>
/// complete WiFi health report with advanced AI analysis
/// </summary>
public class NetworkHealthReport
{
    public string TargetSSID { get; set; } = string.Empty;
    public int CurrentChannel { get; set; }
    public int BestChannel { get; set; }
    public int ChannelSwitchBenefit { get; set; } // improvement percentage

    public int CurrentSignalStrength { get; set; }
    public int CurrentSignalQuality { get; set; }
    public int EstimatedSnr { get; set; }

    public double OverallHealthScore { get; set; }
    public string HealthClassification { get; set; } = "Acceptabil";

    public string FrequencyBand { get; set; } = "2.4 GHz";
    public string BandCharacteristics { get; set; } = string.Empty;

    public int TotalNeighbors { get; set; }
    public int CongestionIndex { get; set; } // 0-100

    public string TheoreticalMaxSpeed { get; set; } = "300 Mbps";
    public string EstimatedRealSpeed { get; set; } = "150 Mbps";
    public int SpeedEfficiency { get; set; } // percentage

    public List<ChannelAnalysis> ChannelDetails { get; set; } = [];
    public List<NeighborInfo> ProblematicNeighbors { get; set; } = [];
    public List<NetworkIssue> DetectedIssues { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
}

/// <summary>
/// information about a problematic neighbor
/// </summary>
public class NeighborInfo
{
    public string SSID { get; set; } = string.Empty;
    public int Channel { get; set; }
    public int SignalStrength { get; set; }
    public string ThreatLevel { get; set; } = "Scăzut"; // Ridicat/Mediu/Scăzut
}

/// <summary>
/// issue detected in the network
/// </summary>
public class NetworkIssue
{
    public string Severity { get; set; } = "Informativă"; // Ridicată/Medie/Informativă
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}