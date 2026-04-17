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

        // === 1. DETECȚIE BANDĂ AUTOMATĂ (2.4 GHz vs 5 GHz) ===
        bool is5Ghz = targetNetwork.Channel > 14;
        report.FrequencyBand = is5Ghz ? "5 GHz" : "2.4 GHz";
        report.BandCharacteristics = is5Ghz
            ? "Viteză mare, rază scurtă, interferență redusă"
            : "Viteză moderată, rază mare, interferență ridicată";

        // Filtrăm rețelele relevante (doar din aceeași bandă)
        var relevantNetworks = allNetworks.Where(n =>
            is5Ghz ? n.Channel > 14 : n.Channel <= 14).ToList();

        // === 2. INIȚIALIZARE CANALE (Diferit pentru 2.4 GHz vs 5 GHz) ===
        int[] standardChannels24 = { 1, 6, 11 };
        int[] standardChannels5 = { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 136, 140, 149, 153, 157, 161, 165 };

        var validChannels = is5Ghz ? standardChannels5 : Enumerable.Range(1, 13).ToArray();
        foreach (int ch in validChannels)
            report.ChannelDetails.Add(new ChannelAnalysis { Channel = ch });

        // === 3. ANALIZA VECINILOR (Neighborhood Analysis) ===
        AnalyzeNeighbors(report, targetNetwork, relevantNetworks);

        // === 4. CALCULUL INTERFERENȚELOR MATEMATICE ===
        foreach (var net in relevantNetworks)
        {
            if (net.SSID == targetNetwork.SSID && net.BSSID == targetNetwork.BSSID) continue;

            // Model logaritmic de interferență (IEEE 802.11 compliant)
            double interferenceWeight = Math.Pow(10, (net.SignalStrength + 100) / 20.0);

            var coChannel = report.ChannelDetails.FirstOrDefault(c => c.Channel == net.Channel);
            if (coChannel != null)
            {
                coChannel.InterferenceScore += interferenceWeight;
                coChannel.CoChannelNeighbors++;
            }

            // Interferență adiacentă (doar la 2.4 GHz - la 5 GHz canalele sunt non-overlapping)
            if (!is5Ghz)
            {
                AddAdjacentInterference(report.ChannelDetails, net.Channel - 1, interferenceWeight * 0.7);
                AddAdjacentInterference(report.ChannelDetails, net.Channel + 1, interferenceWeight * 0.7);
                AddAdjacentInterference(report.ChannelDetails, net.Channel - 2, interferenceWeight * 0.3);
                AddAdjacentInterference(report.ChannelDetails, net.Channel + 2, interferenceWeight * 0.3);
            }
        }

        // === 5. MACHINE LEARNING: K-MEANS CLUSTERING ===
        ApplyKMeansClustering(report);

        // === 6. DETERMINARE CANAL OPTIM ===
        var recommendedChannels = is5Ghz ? standardChannels5 : standardChannels24;
        var bestChannelData = report.ChannelDetails
            .Where(c => recommendedChannels.Contains(c.Channel))
            .OrderBy(c => c.InterferenceScore)
            .FirstOrDefault();

        report.BestChannel = bestChannelData?.Channel ?? targetNetwork.Channel;
        report.ChannelSwitchBenefit = CalculateChannelSwitchBenefit(report, targetNetwork);

        // === 7. CALCUL SCOR DE SĂNĂTATE COMPLEX ===
        CalculateHealthScore(report, targetNetwork, is5Ghz);

        // === 8. ANALIZĂ VITEZĂ TEORETICĂ vs REALĂ ===
        AnalyzeSpeedPotential(report, targetNetwork, relevantNetworks);

        // === 9. DETECTARE PROBLEME SPECIFICE ===
        DetectSpecificIssues(report, targetNetwork, relevantNetworks, is5Ghz);

        // === 10. GENERARE RECOMANDĂRI INTELIGENTE ===
        GenerateSmartRecommendations(report, targetNetwork, is5Ghz);

        return report;
    }

    /// <summary>
    /// Analizează vecinii și detectează rețele problematice
    /// </summary>
    private void AnalyzeNeighbors(NetworkHealthReport report, WifiNetwork target, List<WifiNetwork> neighbors)
    {
        report.TotalNeighbors = neighbors.Count;

        // Vecini pe același canal (cei mai periculoși)
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

        // Congestion Index (0-100, mai mare = mai rău)
        int strongNeighbors = neighbors.Count(n => n.SignalStrength > -70);
        report.CongestionIndex = Math.Min(100, strongNeighbors * 10);
    }

    /// <summary>
    /// Calculează scorul complex de sănătate (0-100)
    /// </summary>
    private void CalculateHealthScore(NetworkHealthReport report, WifiNetwork target, bool is5Ghz)
    {
        double score = 100.0;

        // 1. Penalty pentru semnal slab
        if (target.SignalStrength < -85) score -= 40;
        else if (target.SignalStrength < -75) score -= 25;
        else if (target.SignalStrength < -65) score -= 15;

        // 2. Penalty pentru interferență pe canal
        var currentChannel = report.ChannelDetails.FirstOrDefault(c => c.Channel == target.Channel);
        if (currentChannel != null)
        {
            double maxExpectedInterference = is5Ghz ? 200.0 : 500.0;
            double interferenceRatio = currentChannel.InterferenceScore / maxExpectedInterference;
            score -= Math.Min(35, interferenceRatio * 50);
        }

        // 3. Penalty pentru congestion
        score -= report.CongestionIndex * 0.15;

        // 4. Bonus pentru bandă 5 GHz (mai stabilă)
        if (is5Ghz) score += 5;

        report.OverallHealthScore = Math.Clamp(score, 0, 100);

        // Clasificare
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
    /// Analizează potențialul de viteză vs realitatea
    /// </summary>
    private void AnalyzeSpeedPotential(NetworkHealthReport report, WifiNetwork target, List<WifiNetwork> neighbors)
    {
        // Viteză teoretică maximă bazată pe standard WiFi
        int theoreticalSpeed = target.Channel > 14 ? 866 : 300; // Mbps (estimat 802.11ac vs 802.11n)

        // Factori de degradare
        double signalFactor = Math.Clamp((target.SignalStrength + 100) / 100.0, 0, 1);
        double congestionFactor = 1.0 - (report.CongestionIndex / 150.0);

        int estimatedRealSpeed = (int)(theoreticalSpeed * signalFactor * congestionFactor);

        report.TheoreticalMaxSpeed = $"{theoreticalSpeed} Mbps";
        report.EstimatedRealSpeed = $"{estimatedRealSpeed} Mbps";
        report.SpeedEfficiency = (int)((estimatedRealSpeed / (double)theoreticalSpeed) * 100);
    }

    /// <summary>
    /// Detectează probleme specifice (hidden SSIDs, channel width issues, etc.)
    /// </summary>
    private void DetectSpecificIssues(NetworkHealthReport report, WifiNetwork target, List<WifiNetwork> neighbors, bool is5Ghz)
    {
        // 1. Detectare "Spectru supraaglomerat"
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

        // 2. Detectare "Hidden SSID" (afectează performance)
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

        // 3. Detectare probleme roaming (doar dacă există 2+ AP-uri cu același SSID)
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

        // 4. Semnal marginal pentru streaming/gaming
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
    /// Generează recomandări inteligente bazate pe analiză
    /// </summary>
    private void GenerateSmartRecommendations(NetworkHealthReport report, WifiNetwork target, bool is5Ghz)
    {
        // Recomandare bandă
        report.Recommendations.Add($"📻 Banda: {report.FrequencyBand} — {report.BandCharacteristics}");

        // Recomandare canal
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

        // Recomandare poziționare
        if (target.SignalStrength < -75)
        {
            report.Recommendations.Add("📍 Routerul este prea departe sau blocat. Mută-l într-o poziție mai centrală, la înălțime.");
        }

        // Recomandare upgrade hardware
        if (!is5Ghz && report.CongestionIndex > 60)
        {
            report.Recommendations.Add("🚀 Banda 2.4 GHz este supraaglomerată. Consideră upgrade la router dual-band și folosește 5 GHz.");
        }

        // Recomandare orare de utilizare
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
            var channelDataList = report.ChannelDetails.Select(c => new ChannelData
            {
                Channel = c.Channel,
                InterferenceScore = (float)c.InterferenceScore,
                CoChannelNeighbors = (float)c.CoChannelNeighbors,
                AdjacentNeighbors = (float)c.AdjacentNeighbors
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