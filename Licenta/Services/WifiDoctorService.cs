using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

public interface IWifiDoctorService
{
    OptimizationReport AnalyzeNetwork(WifiNetwork targetNetwork, IReadOnlyList<WifiNetwork> allNetworks);
}

public class WifiDoctorService : IWifiDoctorService
{
    public OptimizationReport AnalyzeNetwork(WifiNetwork targetNetwork, IReadOnlyList<WifiNetwork> allNetworks)
    {
        var report = new OptimizationReport
        {
            TargetSSID = targetNetwork.SSID,
            CurrentChannel = targetNetwork.Channel
        };

        // Extragem rețelele de 2.4 GHz
        var networks24Ghz = allNetworks.Where(n => n.Channel >= 1 && n.Channel <= 13).ToList();

        // Inițializăm analiza pentru canale
        for (int i = 1; i <= 13; i++) report.ChannelDetails.Add(new ChannelAnalysis { Channel = i });

        // Calculăm interferențele matematice brute (Feature Extraction)
        foreach (var net in networks24Ghz)
        {
            if (net.SSID == targetNetwork.SSID && net.BSSID == targetNetwork.BSSID) continue;

            double interferenceWeight = Math.Pow(10, (net.SignalStrength + 100) / 20.0);

            var coChannel = report.ChannelDetails.First(c => c.Channel == net.Channel);
            coChannel.InterferenceScore += interferenceWeight;
            coChannel.CoChannelNeighbors++;

            AddAdjacentInterference(report.ChannelDetails, net.Channel - 1, interferenceWeight * 0.7);
            AddAdjacentInterference(report.ChannelDetails, net.Channel + 1, interferenceWeight * 0.7);
            AddAdjacentInterference(report.ChannelDetails, net.Channel - 2, interferenceWeight * 0.3);
            AddAdjacentInterference(report.ChannelDetails, net.Channel + 2, interferenceWeight * 0.3);
        }

        // ============================================================
        // 🧠 MACHINE LEARNING: K-MEANS CLUSTERING (SĂPTĂMÂNA 8)
        // ============================================================
        ApplyKMeansClustering(report);
        // ============================================================

        // Determinăm cel mai bun canal din cele standard (1, 6, 11)
        int[] standardChannels = { 1, 6, 11 };
        var bestChannelData = report.ChannelDetails
                                    .Where(c => standardChannels.Contains(c.Channel))
                                    .OrderBy(c => c.InterferenceScore)
                                    .First();

        report.BestChannel = bestChannelData.Channel;

        // Calculăm scorul de sănătate (0-100)
        var currentChannelData = report.ChannelDetails.First(c => c.Channel == targetNetwork.Channel);
        double maxExpectedInterference = 500.0;
        double penalty = (currentChannelData.InterferenceScore / maxExpectedInterference) * 100;

        if (targetNetwork.SignalStrength < -75) penalty += 20;
        if (targetNetwork.SignalStrength < -85) penalty += 30;

        report.OverallHealthScore = Math.Clamp(100 - penalty, 0, 100);

        GenerateInsights(report, targetNetwork, currentChannelData);

        return report;
    }

    private void ApplyKMeansClustering(OptimizationReport report)
    {
        try
        {
            // 1. Inițializăm ML.NET
            var mlContext = new MLContext(seed: 0);

            // 2. Pregătim datele pentru antrenament
            var channelDataList = report.ChannelDetails.Select(c => new ChannelData
            {
                Channel = c.Channel,
                InterferenceScore = (float)c.InterferenceScore,
                CoChannelNeighbors = (float)c.CoChannelNeighbors,
                AdjacentNeighbors = (float)c.AdjacentNeighbors
            }).ToList();

            var trainingData = mlContext.Data.LoadFromEnumerable(channelDataList);

            // 3. Definim Pipeline-ul K-Means (3 Clustere)
            var pipeline = mlContext.Transforms.Concatenate("Features",
                nameof(ChannelData.InterferenceScore),
                nameof(ChannelData.CoChannelNeighbors),
                nameof(ChannelData.AdjacentNeighbors))
                .Append(mlContext.Clustering.Trainers.KMeans("Features", numberOfClusters: 3));

            // 4. Antrenăm modelul pe loc (învață mediul tău WiFi)
            var model = pipeline.Fit(trainingData);

            // 5. Facem predicții
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

            // 6. Sortăm clusterele ca să știm care e Optim, Aglomerat, Critic
            // (Clusterul cu media de interferență cea mai mică este "Optim")
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
        catch (Exception ex)
        {
            // Fallback în caz că datele sunt prea puține pentru clustering
            System.Diagnostics.Debug.WriteLine($"Eroare ML.NET: {ex.Message}");
            foreach (var c in report.ChannelDetails) c.Status = "Necunoscut";
        }
    }

    private void AddAdjacentInterference(List<ChannelAnalysis> channels, int targetChannel, double weight)
    {
        if (targetChannel >= 1 && targetChannel <= 13)
        {
            var ch = channels.First(c => c.Channel == targetChannel);
            ch.InterferenceScore += weight;
            ch.AdjacentNeighbors++;
        }
    }

    private void GenerateInsights(OptimizationReport report, WifiNetwork myNet, ChannelAnalysis currentCh)
    {
        if (myNet.SignalStrength < -80)
            report.ActionableInsights.Add("Semnalul routerului este critic. Recomandăm mutarea lui într-o zonă mai centrală.");

        if (currentCh.Status.Contains("Critic") || currentCh.Status.Contains("Aglomerat"))
            report.ActionableInsights.Add($"Canalul tău actual ({myNet.Channel}) a fost clasificat de AI ca fiind '{currentCh.Status}'. Ai {currentCh.CoChannelNeighbors} vecini direcți.");

        if (report.CurrentChannel != report.BestChannel)
            report.ActionableInsights.Add($"Optimizare: Mută routerul pe Canalul {report.BestChannel}. Este cel mai liber canal.");
        else
            report.ActionableInsights.Add("Ești deja pe cel mai bun canal disponibil. Configurația radio este optimă.");
    }
}