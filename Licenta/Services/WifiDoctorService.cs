using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

public interface IWifiDoctorService
{
    NetworkHealthReport AnalyzeNetwork(WifiNetwork targetNetwork, IReadOnlyList<WifiNetwork> allNetworks);
}

public class WifiDoctorService : IWifiDoctorService
{
    public NetworkHealthReport AnalyzeNetwork(WifiNetwork targetNetwork, IReadOnlyList<WifiNetwork> allNetworks)
    {
        var report = new NetworkHealthReport
        {
            TargetSSID = targetNetwork.SSID,
            CurrentChannel = targetNetwork.Channel,
            CurrentSignalStrength = targetNetwork.SignalStrength,
            CurrentSignalQuality  = targetNetwork.SignalQuality,
            EstimatedSnr          = targetNetwork.EstimatedSnr
        };

        // 1. automatic band detection (2.4 GHz vs 5 GHz)
        bool is5Ghz = targetNetwork.Channel > 14;
        report.FrequencyBand = is5Ghz ? "5 GHz" : "2.4 GHz";
        report.BandCharacteristics = is5Ghz
            ? "Viteză mare, rază scurtă, interferență redusă"
            : "Viteză moderată, rază mare, interferență ridicată";

        // filter the relevant networks (same band only)
        var relevantNetworks = allNetworks.Where(n =>
            is5Ghz ? n.Channel > 14 : n.Channel <= 14).ToList();

        // 2. channel initialization (different for 2.4 GHz vs 5 GHz)
        int[] standardChannels24 = { 1, 6, 11 };
        int[] standardChannels5 = { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 136, 140, 149, 153, 157, 161, 165 };

        var validChannels = is5Ghz ? standardChannels5 : Enumerable.Range(1, 13).ToArray();
        foreach (int ch in validChannels)
            report.ChannelDetails.Add(new ChannelAnalysis { Channel = ch });

        // 3. neighborhood analysis
        AnalyzeNeighbors(report, targetNetwork, relevantNetworks);

        // 4. interference score calculation
        foreach (var net in relevantNetworks)
        {
            if (net.SSID == targetNetwork.SSID && net.BSSID == targetNetwork.BSSID) continue;

            // logarithmic interference model (IEEE 802.11 compliant)
            double interferenceWeight = Math.Pow(10, (net.SignalStrength + 100) / 20.0);

            var coChannel = report.ChannelDetails.FirstOrDefault(c => c.Channel == net.Channel);
            if (coChannel != null)
            {
                coChannel.InterferenceScore += interferenceWeight;
                coChannel.CoChannelNeighbors++;
            }

            // adjacent channel interference (2.4 GHz only - 5 GHz channels are non-overlapping)
            if (!is5Ghz)
            {
                AddAdjacentInterference(report.ChannelDetails, net.Channel - 1, interferenceWeight * 0.7);
                AddAdjacentInterference(report.ChannelDetails, net.Channel + 1, interferenceWeight * 0.7);
                AddAdjacentInterference(report.ChannelDetails, net.Channel - 2, interferenceWeight * 0.3);
                AddAdjacentInterference(report.ChannelDetails, net.Channel + 2, interferenceWeight * 0.3);
            }
        }

        // 5. machine learning: K-Means clustering
        ApplyKMeansClustering(report);

        // 6. optimal channel determination
        var recommendedChannels = is5Ghz ? standardChannels5 : standardChannels24;
        var bestChannelData = report.ChannelDetails
            .Where(c => recommendedChannels.Contains(c.Channel))
            .OrderBy(c => c.InterferenceScore)
            .FirstOrDefault();

        report.BestChannel = bestChannelData?.Channel ?? targetNetwork.Channel;
        report.ChannelSwitchBenefit = CalculateChannelSwitchBenefit(report, targetNetwork);

        // 7. composite health score calculation
        CalculateHealthScore(report, targetNetwork, is5Ghz);

        // 8. theoretical vs real speed analysis
        AnalyzeSpeedPotential(report, targetNetwork, relevantNetworks);

        // 9. specific issue detection
        DetectSpecificIssues(report, targetNetwork, relevantNetworks, is5Ghz);

        // 10. smart recommendation generation
        GenerateSmartRecommendations(report, targetNetwork, is5Ghz);

        return report;
    }

    /// <summary>
    /// analyzes neighbors and detects problematic networks
    /// </summary>
    private void AnalyzeNeighbors(NetworkHealthReport report, WifiNetwork target, List<WifiNetwork> neighbors)
    {
        report.TotalNeighbors = neighbors.Count;

        // same-channel neighbors (the most dangerous)
        var sameChannelNeighbors = neighbors
            .Where(n => n.Channel == target.Channel && n.SSID != target.SSID)
            .OrderByDescending(n => n.SignalStrength)
            .Take(5)
            .ToList();

        foreach (var neighbor in sameChannelNeighbors)
        {
            report.ProblematicNeighbors.Add(new NeighborInfo
            {
                SSID = neighbor.SSID,
                Channel = neighbor.Channel,
                SignalStrength = neighbor.SignalStrength,
                ThreatLevel = neighbor.SignalStrength > -60 ? "Ridicat" :
                             neighbor.SignalStrength > -75 ? "Mediu" : "Scăzut"
            });
        }

        // congestion index (0-100, higher = worse)
        int strongNeighbors = neighbors.Count(n => n.SignalStrength > -70);
        report.CongestionIndex = Math.Min(100, strongNeighbors * 10);
    }

    /// <summary>
    /// calculates the composite health score (0-100)
    /// </summary>
    private void CalculateHealthScore(NetworkHealthReport report, WifiNetwork target, bool is5Ghz)
    {
        double score = 100.0;

        // 1. penalty for weak signal
        if (target.SignalStrength < -85) score -= 40;
        else if (target.SignalStrength < -75) score -= 25;
        else if (target.SignalStrength < -65) score -= 15;

        // 2. penalty for channel interference
        var currentChannel = report.ChannelDetails.FirstOrDefault(c => c.Channel == target.Channel);
        if (currentChannel != null)
        {
            double maxExpectedInterference = is5Ghz ? 200.0 : 500.0;
            double interferenceRatio = currentChannel.InterferenceScore / maxExpectedInterference;
            score -= Math.Min(35, interferenceRatio * 50);
        }

        // 3. penalty for congestion
        score -= report.CongestionIndex * 0.15;

        // 4. bonus for the 5 GHz band (more stable)
        if (is5Ghz) score += 5;

        report.OverallHealthScore = Math.Clamp(score, 0, 100);

        // classification
        report.HealthClassification = report.OverallHealthScore switch
        {
            >= 85 => "Excelent",
            >= 70 => "Bun",
            >= 50 => "Acceptabil",
            >= 30 => "Slab",
            _ => "Critic"
        };
    }

    /// <summary>
    /// analyzes the speed potential vs reality
    /// </summary>
    private void AnalyzeSpeedPotential(NetworkHealthReport report, WifiNetwork target, List<WifiNetwork> neighbors)
    {
        // Shannon capacity: C = B * log2(1 + SNR_linear)
        // bandwidth per band: 2.4 GHz=20 MHz, 5 GHz=80 MHz (802.11ac/ax), 6 GHz=160 MHz (WiFi 6E)
        double bandwidthMHz = target.Channel > 177 ? 160.0 : target.Channel > 14 ? 80.0 : 20.0;
        double snrLinear    = Math.Pow(10.0, target.EstimatedSnr / 10.0);
        double shannonMbps  = bandwidthMHz * Math.Log2(1.0 + snrLinear);

        // protocol efficiency: ~65% for 5/6 GHz 802.11ax, ~55% for 2.4 GHz 802.11n/ac
        double efficiency     = target.Channel > 14 ? 0.65 : 0.55;
        int theoreticalSpeed  = (int)(shannonMbps * efficiency);

        double congestionFactor = 1.0 - (report.CongestionIndex / 150.0);
        int estimatedRealSpeed  = (int)(theoreticalSpeed * congestionFactor);

        report.TheoreticalMaxSpeed = $"{theoreticalSpeed} Mbps";
        report.EstimatedRealSpeed  = $"{estimatedRealSpeed} Mbps";
        report.SpeedEfficiency     = theoreticalSpeed > 0
            ? (int)((estimatedRealSpeed / (double)theoreticalSpeed) * 100)
            : 0;
    }

    /// <summary>
    /// detects specific issues (hidden SSIDs, channel width issues, etc.)
    /// </summary>
    private void DetectSpecificIssues(NetworkHealthReport report, WifiNetwork target, List<WifiNetwork> neighbors, bool is5Ghz)
    {
        // 1. detect "overcrowded spectrum"
        if (neighbors.Count(n => n.SignalStrength > -70) > 10)
        {
            report.DetectedIssues.Add(new NetworkIssue
            {
                Severity = "Ridicată",
                Title = "Spectru WiFi supraaglomerat",
                Description = $"Există {neighbors.Count} rețele în zonă, dintre care {neighbors.Count(n => n.SignalStrength > -70)} au semnal puternic.",
                Recommendation = "Consideră upgrade la WiFi 6 (802.11ax) care gestionează mai bine congestia."
            });
        }

        // 2. detect "hidden SSID" (affects performance)
        if (string.IsNullOrEmpty(target.SSID))
        {
            report.DetectedIssues.Add(new NetworkIssue
            {
                Severity = "Medie",
                Title = "SSID ascuns detectat",
                Description = "Rețeaua folosește SSID ascuns, ceea ce crește overhead-ul de beacon.",
                Recommendation = "Dezactivează opțiunea 'Hide SSID' din setările routerului pentru performanță mai bună."
            });
        }

        // 3. detect roaming issues (only if there are 2+ APs with the same SSID)
        var sameSSIDCount = neighbors.Count(n => n.SSID == target.SSID);
        if (sameSSIDCount > 1)
        {
            report.DetectedIssues.Add(new NetworkIssue
            {
                Severity = "Informativă",
                Title = "Sistem Multi-AP detectat",
                Description = $"Există {sameSSIDCount + 1} acces points cu SSID-ul '{target.SSID}'.",
                Recommendation = "Asigură-te că au setări de roaming identice (802.11k/v/r) pentru tranziții fluide."
            });
        }

        // 4. marginal signal for streaming/gaming
        if (target.SignalStrength < -70 && target.SignalStrength > -80)
        {
            report.DetectedIssues.Add(new NetworkIssue
            {
                Severity = "Medie",
                Title = "Semnal marginal pentru aplicații exigente",
                Description = $"Cu {target.SignalStrength} dBm, streaming 4K și gaming pot avea întreruperi.",
                Recommendation = "Mută routerul mai aproape sau adaugă un range extender."
            });
        }
    }

    /// <summary>
    /// generates smart recommendations based on the analysis
    /// </summary>
    private void GenerateSmartRecommendations(NetworkHealthReport report, WifiNetwork target, bool is5Ghz)
    {
        // band recommendation
        report.Recommendations.Add($"📻 Banda: {report.FrequencyBand} — {report.BandCharacteristics}");

        // channel recommendation
        if (report.CurrentChannel != report.BestChannel)
        {
            var improvement = report.ChannelSwitchBenefit;
            report.Recommendations.Add(
                $"🎯 Schimbă pe Canalul {report.BestChannel} pentru îmbunătățire de ~{improvement}% în performanță!");
        }
        else
        {
            report.Recommendations.Add($"✅ Ești deja pe canalul optim ({report.CurrentChannel})!");
        }

        // positioning recommendation
        if (target.SignalStrength < -75)
        {
            report.Recommendations.Add("📍 Routerul este prea departe sau blocat. Mută-l într-o poziție mai centrală, la înălțime.");
        }

        // hardware upgrade recommendation
        if (!is5Ghz && report.CongestionIndex > 60)
        {
            report.Recommendations.Add("🚀 Banda 2.4 GHz este supraaglomerată. Consideră upgrade la router dual-band și folosește 5 GHz.");
        }

        // usage schedule recommendation
        if (report.ProblematicNeighbors.Count > 3)
        {
            report.Recommendations.Add($"⏰ Există {report.ProblematicNeighbors.Count} vecini puternici. Evită streaming/gaming între 18:00-23:00 (ore de vârf).");
        }
    }

    private int CalculateChannelSwitchBenefit(NetworkHealthReport report, WifiNetwork target)
    {
        var currentCh = report.ChannelDetails.FirstOrDefault(c => c.Channel == target.Channel);
        var bestCh = report.ChannelDetails.FirstOrDefault(c => c.Channel == report.BestChannel);

        if (currentCh == null || bestCh == null) return 0;

        double improvement = ((currentCh.InterferenceScore - bestCh.InterferenceScore) /
                             Math.Max(1, currentCh.InterferenceScore)) * 100;

        return (int)Math.Clamp(improvement, 0, 90);
    }

    private void ApplyKMeansClustering(NetworkHealthReport report)
    {
        try
        {
            var mlContext = new MLContext(seed: 0);

            // normalize features to [0,1] so K-Means is not dominated by InterferenceScore
            // (which can be 0-1000+, vs CoChannelNeighbors which is 0-20)
            float maxInterf = Math.Max(1f, (float)report.ChannelDetails.Max(c => c.InterferenceScore));
            float maxCoCh   = Math.Max(1f, (float)report.ChannelDetails.Max(c => c.CoChannelNeighbors));
            float maxAdj    = Math.Max(1f, (float)report.ChannelDetails.Max(c => c.AdjacentNeighbors));

            var channelDataList = report.ChannelDetails.Select(c => new ChannelData
            {
                Channel            = c.Channel,
                InterferenceScore  = (float)c.InterferenceScore  / maxInterf,
                CoChannelNeighbors = (float)c.CoChannelNeighbors / maxCoCh,
                AdjacentNeighbors  = (float)c.AdjacentNeighbors  / maxAdj
            }).ToList();

            var trainingData = mlContext.Data.LoadFromEnumerable(channelDataList);

            var pipeline = mlContext.Transforms.Concatenate("Features",
                nameof(ChannelData.InterferenceScore),
                nameof(ChannelData.CoChannelNeighbors),
                nameof(ChannelData.AdjacentNeighbors))
                .Append(mlContext.Clustering.Trainers.KMeans("Features", numberOfClusters: 3));

            var model = pipeline.Fit(trainingData);
            var predictor = mlContext.Model.CreatePredictionEngine<ChannelData, ClusterPrediction>(model);

            var clusterGroups = new Dictionary<uint, List<ChannelAnalysis>>();

            foreach (var chData in channelDataList)
            {
                var prediction = predictor.Predict(chData);
                var actualChannel = report.ChannelDetails.First(c => c.Channel == chData.Channel);

                if (!clusterGroups.ContainsKey(prediction.PredictedClusterId))
                    clusterGroups[prediction.PredictedClusterId] = new List<ChannelAnalysis>();

                clusterGroups[prediction.PredictedClusterId].Add(actualChannel);
            }

            var orderedClusters = clusterGroups
                .OrderBy(g => g.Value.Average(c => c.InterferenceScore))
                .ToList();

            if (orderedClusters.Count > 0)
                foreach (var c in orderedClusters[0].Value) c.Status = "🟢 Optim";
            if (orderedClusters.Count > 1)
                foreach (var c in orderedClusters[1].Value) c.Status = "🟡 Aglomerat";
            if (orderedClusters.Count > 2)
                foreach (var c in orderedClusters[2].Value) c.Status = "🔴 Critic";
        }
        catch
        {
            foreach (var c in report.ChannelDetails) c.Status = "⚪ Necunoscut";
        }
    }

    private void AddAdjacentInterference(List<ChannelAnalysis> channels, int targetChannel, double weight)
    {
        var ch = channels.FirstOrDefault(c => c.Channel == targetChannel);
        if (ch != null)
        {
            ch.InterferenceScore += weight;
            ch.AdjacentNeighbors++;
        }
    }
}