namespace WifiSignalOptimizer.Models;

/// <summary>
/// Statistici complete de acoperire WiFi.
/// Folosite pentru comparația înainte/după optimizare și panoul de analiză AI.
/// </summary>
public record CoverageStats(
    // ── Trepte semnal (% suprafață) ────────────────────────────────────
    double ExcellentPercent,  // > -55 dBm  — streaming 4K, gaming
    double GoodPercent,       // -55 / -70  — browsing, HD video
    double FairPercent,       // -70 / -80  — e-mail, web de bază
    double PoorPercent,       // -80 / -90  — conectivitate minimă
    double DeadZonePercent,   // < -90 dBm  — fără semnal

    // ── Semnal mediu ────────────────────────────────────────────────────
    double AverageSignal,     // dBm

    // ── Metrici derivate ────────────────────────────────────────────────
    double UniformityScore,   // 0–100: cât de uniform e semnalul (100 = perfect uniform)
    double AvgWallCount,      // nr. mediu de pereți ce trebuie traversați (0 = fără date)
    int    TotalSamples       // nr. eșantioane folosite la calcul
)
{
    // ── Acoperire utilizabilă (Excelent + Bun + Mediu) ─────────────────
    public double CoveragePercent => ExcellentPercent + GoodPercent + FairPercent;

    // ── Viteză estimată (empiric din tabelul 802.11ac/ax) ───────────────
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

    // ── Scor general 0–100 ──────────────────────────────────────────────
    // Formulă în 2 componente:
    // 1) Calitate semnal ÎN zona acoperită (normalizat la pixelii non-dead)
    // 2) Procentul de acoperire totală (câtă suprafață are semnal)
    // Asta evită penalizarea spațiului gol din afara casei.
    public double OverallScore
    {
        get
        {
            double covered = ExcellentPercent + GoodPercent + FairPercent + PoorPercent;
            if (covered < 0.1) return 0;

            // Calitate în zona acoperită (0–100)
            double qualityScore = (ExcellentPercent * 1.00 +
                                   GoodPercent      * 0.80 +
                                   FairPercent      * 0.50 +
                                   PoorPercent      * 0.10)
                                  / covered * 100.0;

            // Scor final: 65% calitate + 35% acoperire
            return Math.Clamp(qualityScore * 0.65 + covered * 0.35, 0, 100);
        }
    }

    // ── Literă de scor ─────────────────────────────────────────────────
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
