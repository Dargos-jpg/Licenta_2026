namespace WifiSignalOptimizer.Models;

/// <summary>
/// complete WiFi coverage statistics.
/// used for the before/after optimization comparison and the AI analysis panel.
/// </summary>
public record CoverageStats(
    // signal tiers (% of area)
    double ExcellentPercent,  // > -55 dBm, 4K streaming, gaming
    double GoodPercent,       // -55 / -70, browsing, HD video
    double FairPercent,       // -70 / -80, email, basic web
    double PoorPercent,       // -80 / -90, minimal connectivity
    double DeadZonePercent,   // < -90 dBm, no signal

    // average signal
    double AverageSignal,     // dBm

    // derived metrics
    double UniformityScore,   // 0-100: how uniform the signal is (100 = perfectly uniform)
    double AvgWallCount,      // average number of walls to cross (0 = no data)
    int    TotalSamples       // number of samples used in the calculation
)
{
    // usable coverage (Excellent + Good + Fair)
    public double CoveragePercent => ExcellentPercent + GoodPercent + FairPercent;

    // estimated throughput (empirical, from the 802.11ac/ax table)
    public double EstimatedThroughputMbps => AverageSignal switch
    {
        > -50 => 300,
        > -55 => 200,
        > -60 => 130,
        > -65 => 80,
        > -70 => 40,
        > -75 => 15,
        > -80 => 5,
        _     => 1
    };

    // overall score 0-100
    // formula has 2 components:
    // 1) signal quality within the covered area (normalized to the non-dead pixels)
    // 2) the total coverage percentage (how much area has signal)
    // this avoids penalizing empty space outside the house.
    public double OverallScore
    {
        get
        {
            double covered = ExcellentPercent + GoodPercent + FairPercent + PoorPercent;
            if (covered < 0.1) return 0;

            // quality within the covered area (0-100)
            double qualityScore = (ExcellentPercent * 1.00 +
                                   GoodPercent      * 0.80 +
                                   FairPercent      * 0.50 +
                                   PoorPercent      * 0.10)
                                  / covered * 100.0;

            // final score: 65% quality + 35% coverage
            return Math.Clamp(qualityScore * 0.65 + covered * 0.35, 0, 100);
        }
    }

    // letter grade
    public string ScoreGrade => OverallScore switch
    {
        >= 85 => "A",
        >= 70 => "B",
        >= 55 => "C",
        >= 40 => "D",
        _     => "F"
    };

    public string AverageSignalFormatted => $"{AverageSignal:F1} dBm";
}
