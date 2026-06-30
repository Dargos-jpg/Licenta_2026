
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// service contract for WiFi network scanning operations
/// </summary>
public interface IWifiScannerService
{
    /// <summary>
    /// performs a WiFi scan and returns all detected networks
    /// </summary>
    /// <returns>collection of detected WiFi networks</returns>
    Task<IReadOnlyList<WifiNetwork>> ScanNetworksAsync();

    /// <summary>
    /// gets the last scan results without performing a new scan
    /// </summary>
    IReadOnlyList<WifiNetwork> GetLastScanResults();
}