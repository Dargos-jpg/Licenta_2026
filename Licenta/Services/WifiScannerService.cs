using ManagedNativeWifi;
using WifiSignalOptimizer.Models;
using System.Net.NetworkInformation;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// WiFi scanner implementation using ManagedNativeWifi for real network scanning.
/// Requires Windows with WiFi adapter.
/// </summary>
public sealed class WifiScannerService : IWifiScannerService
{
    private List<WifiNetwork> _lastScanResults = [];
    private readonly bool _useRealScanning;

    public WifiScannerService(bool useRealScanning = true)
    {
        _useRealScanning = useRealScanning;

        // Check if WiFi adapter is available
        if (useRealScanning)
        {
            try
            {
                var interfaces = NativeWifi.EnumerateInterfaces().ToList();
                if (interfaces.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("No WiFi adapter found. Falling back to simulation.");
                    _useRealScanning = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WiFi adapter check failed: {ex.Message}. Using simulation.");
                _useRealScanning = false;
            }
        }
    }

    public async Task<IReadOnlyList<WifiNetwork>> ScanNetworksAsync()
    {
        if (_useRealScanning)
        {
            return await ScanRealNetworksAsync();
        }
        else
        {
            return await ScanSimulatedNetworksAsync();
        }
    }

    /// <summary>
    /// Scans real WiFi networks using ManagedNativeWifi.
    /// Triggers an active scan first, then reads BSS cache.
    /// </summary>
    private async Task<IReadOnlyList<WifiNetwork>> ScanRealNetworksAsync()
    {
        try
        {
            // ── Pas 1 + 2: declanșăm scan activ și așteptăm finalizarea ────────
            // ScanNetworksAsync trimite wlan_scan pe toate interfețele și
            // blochează până când toate raportează un rezultat (max timeout).
            try
            {
                await NativeWifi.ScanNetworksAsync(TimeSpan.FromSeconds(4));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ScanNetworksAsync failed: {ex.Message}");
                // continuăm oricum — poate avem date în cache
            }

            // ── Pas 3: citim lista BSS actualizată ────────────────────────────
            var networks = new List<WifiNetwork>();

            await Task.Run(() =>
            {
                var bssNetworks = NativeWifi.EnumerateBssNetworks()
                    .Where(n => !string.IsNullOrWhiteSpace(n.Ssid.ToString()))
                    .ToList();

                foreach (var network in bssNetworks)
                {
                    try
                    {
                        networks.Add(new WifiNetwork
                        {
                            SSID            = network.Ssid.ToString(),
                            BSSID           = network.Bssid?.ToString() ?? "00:00:00:00:00:00",
                            SignalStrength   = network.SignalStrength,
                            Channel         = GetChannelFromFrequency(network.Frequency)
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error parsing BSS network: {ex.Message}");
                    }
                }
            });

            // Deduplicate: keep strongest signal per SSID+Band combination.
            // Same SSID on 2.4 GHz and 5 GHz are kept as separate entries so
            // Doctor WiFi can analyse each band independently.
            var deduped = networks
                .GroupBy(n => $"{n.SSID}|{(n.Channel > 177 ? "6GHz" : n.Channel > 14 ? "5GHz" : "2.4GHz")}")
                .Select(g => g.OrderByDescending(n => n.SignalStrength).First())
                .OrderByDescending(n => n.SignalStrength)
                .ToList();

            if (deduped.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "BSS list empty after active scan — adapter may be disabled or no networks nearby.");
                return await ScanSimulatedNetworksAsync();
            }

            _lastScanResults = deduped;
            return _lastScanResults.AsReadOnly();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Real scan failed: {ex.Message}. Using fallback.");
            return await ScanSimulatedNetworksAsync();
        }
    }

    /// <summary>
    /// Simulated WiFi scanner for development and testing (BACKUP).
    /// </summary>
    private async Task<IReadOnlyList<WifiNetwork>> ScanSimulatedNetworksAsync()
    {
        var random = new Random();

        // Simulate scan delay
        await Task.Delay(random.Next(200, 800));

        var simulatedSSIDs = new[]
        {
            "CorporateNetwork",
            "GuestWiFi",
            "SmartHome_5G",
            "NeighborNetwork",
            "IoT_Devices",
            "HomeOffice",
            "Building_Main",
            "Conference_Room_A",
            "Lab_Network",
            "Mobile_Hotspot"
        };

        var networkCount = random.Next(5, 11);
        var networks = new List<WifiNetwork>();

        for (int i = 0; i < networkCount; i++)
        {
            // Generate realistic channel
            var is5GHz = random.Next(0, 2) == 0;
            int channel;

            if (is5GHz)
            {
                var channels5G = new[] { 36, 40, 44, 48, 149, 153, 157, 161, 165 };
                channel = channels5G[random.Next(channels5G.Length)];
            }
            else
            {
                var channels24G = new[] { 1, 6, 11 };
                channel = channels24G[random.Next(channels24G.Length)];
            }

            // Generate realistic BSSID
            var bssid = string.Join(":",
                Enumerable.Range(0, 6)
                    .Select(_ => random.Next(0, 256).ToString("X2")));

            var network = new WifiNetwork
            {
                SSID = simulatedSSIDs[random.Next(simulatedSSIDs.Length)] +
                       (i > 7 ? $"_{i}" : ""),
                BSSID = bssid,
                SignalStrength = random.Next(-90, -30),
                Channel = channel
            };

            networks.Add(network);
        }

        _lastScanResults = [.. networks.OrderByDescending(n => n.SignalStrength)];

        return _lastScanResults.AsReadOnly();
    }

    public IReadOnlyList<WifiNetwork> GetLastScanResults() =>
        _lastScanResults.AsReadOnly();

    /// <summary>
    /// Format BSSID (PhysicalAddress) to readable MAC address string
    /// </summary>
    private static string FormatBssid(PhysicalAddress bssid)
    {
        if (bssid == null)
            return "00:00:00:00:00:00";

        return string.Join(":", bssid.GetAddressBytes().Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// Convert frequency (kHz) to channel number
    /// </summary>
    private static int GetChannelFromFrequency(int frequencyKhz)
    {
        // Convert kHz to MHz
        int frequencyMhz = frequencyKhz / 1000;

        // 2.4 GHz band (channels 1-14)
        if (frequencyMhz >= 2412 && frequencyMhz <= 2484)
        {
            if (frequencyMhz == 2484)
                return 14;
            return (frequencyMhz - 2412) / 5 + 1;
        }

        // 5 GHz band (channels 36-165)
        if (frequencyMhz >= 5170 && frequencyMhz <= 5825)
        {
            return (frequencyMhz - 5000) / 5;
        }

        // 6 GHz band (channels 1-233)
        if (frequencyMhz >= 5955 && frequencyMhz <= 7115)
        {
            return (frequencyMhz - 5955) / 5 + 1;
        }

        // Unknown frequency
        return 0;
    }
}