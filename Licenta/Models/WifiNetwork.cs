namespace WifiSignalOptimizer.Models;

/// <summary>
/// represents a WiFi network detected during scanning
/// </summary>
public sealed class WifiNetwork
{
    /// <summary>
    /// service Set Identifier - network name
    /// </summary>
    public required string SSID { get; init; }

    /// <summary>
    /// basic Service Set Identifier - MAC address of the access point
    /// </summary>
    public required string BSSID { get; init; }

    /// <summary>
    /// signal strength in dBm (typically -30 to -90)
    /// </summary>
    public required int SignalStrength { get; init; }

    /// <summary>
    /// WiFi channel number
    /// </summary>
    public required int Channel { get; init; }

    /// <summary>
    /// signal quality percentage for UI display
    /// </summary>
    public int SignalQuality => CalculateSignalQuality(SignalStrength);

    /// <summary>
    /// SNR estimated from RSSI + typical noise floor per band (IEEE 802.11-2020).
    /// 2.4 GHz: noise floor ~ -95 dBm, 5 GHz: noise floor ~ -98 dBm
    /// </summary>
    public int EstimatedSnr => Math.Max(0, Channel > 14
        ? SignalStrength + 98   // 5 GHz
        : SignalStrength + 95); // 2.4 GHz

    public string BandLabel  => Channel > 177 ? "6G" : Channel > 14 ? "5G" : "2.4G";

    /// <summary>last 8 characters of the BSSID (e.g. "DD:EE:FF"), enough to distinguish APs.</summary>
    public string ShortBssid => BSSID?.Length >= 8 ? BSSID[^8..] : BSSID ?? "—";

    public string SnrQuality => EstimatedSnr switch
    {
        >= 40 => "Excelent",
        >= 25 => "Bun",
        >= 15 => "Acceptabil",
        >= 10 => "Slab",
        _     => "Critic"
    };

    private static int CalculateSignalQuality(int dbm)
    {
        // convert dBm to percentage (0-100)
        // -30 dBm = 100%, -90 dBm = 0%
        return Math.Clamp((int)((dbm + 90) * 100.0 / 60.0), 0, 100);
    }

    public override string ToString() =>
        $"{SSID} ({BSSID}) - {SignalStrength} dBm (Ch {Channel})";
}