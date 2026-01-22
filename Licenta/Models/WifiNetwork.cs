namespace WifiSignalOptimizer.Models;

/// <summary>
/// Represents a WiFi network detected during scanning
/// </summary>
public sealed class WifiNetwork
{
    /// <summary>
    /// Service Set Identifier - Network name
    /// </summary>
    public required string SSID { get; init; }

    /// <summary>
    /// Basic Service Set Identifier - MAC address of the access point
    /// </summary>
    public required string BSSID { get; init; }

    /// <summary>
    /// Signal strength in dBm (typically -30 to -90)
    /// </summary>
    public required int SignalStrength { get; init; }

    /// <summary>
    /// WiFi channel number
    /// </summary>
    public required int Channel { get; init; }

    /// <summary>
    /// Signal quality percentage for UI display
    /// </summary>
    public int SignalQuality => CalculateSignalQuality(SignalStrength);

    private static int CalculateSignalQuality(int dbm)
    {
        // Convert dBm to percentage (0-100)
        // -30 dBm = 100%, -90 dBm = 0%
        return Math.Clamp((int)((dbm + 90) * 100.0 / 60.0), 0, 100);
    }

    public override string ToString() =>
        $"{SSID} ({BSSID}) - {SignalStrength} dBm (Ch {Channel})";
}