
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Service contract for WiFi network scanning operations
/// </summary>
public interface IWifiScannerService
{
    /// <summary>
    /// Performs a WiFi scan and returns all detected networks
    /// </summary>
    /// <returns>Collection of detected WiFi networks</returns>
    Task<IReadOnlyList<WifiNetwork>> ScanNetworksAsync();

    /// <summary>
    /// Gets the last scan results without performing a new scan
    /// </summary>
    IReadOnlyList<WifiNetwork> GetLastScanResults();
}