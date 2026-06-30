using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

public static class ReportExportService
{
    static ReportExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void ExportDoctorReport(
        NetworkHealthReport report,
        CoverageStats? beforeStats = null,
        CoverageStats? afterStats  = null)
    {
        var dialog = new SaveFileDialog
        {
            Title    = "Exportă Raport PDF",
            Filter   = "Raport PDF (*.pdf)|*.pdf",
            FileName = $"WiFi_Raport_{report.TargetSSID}_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10).FontColor("#E0E0E8"));
                    page.Background().Background("#0D0D0F");

                    page.Header().Element(ComposeHeader);
                    page.Content().Element(c => ComposeContent(c, report, beforeStats, afterStats));
                    page.Footer().Element(ComposeFooter);
                });
            }).GeneratePdf(dialog.FileName);

            MessageBox.Show(
                $"Raport exportat cu succes!\n{dialog.FileName}",
                "Export PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Eroare la export PDF:\n{ex.Message}",
                "Eroare", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // header
    private static void ComposeHeader(IContainer c)
    {
        c.Background("#101014").Padding(16).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("WIFI SIGNAL OPTIMIZER")
                    .FontSize(14).Bold().FontColor("#3B82F6").FontFamily("Courier New");
                col.Item().Text("Raport Diagnostic Avansat · Doctor Wi-Fi AI")
                    .FontSize(9).FontColor("#5A5A72").FontFamily("Courier New");
            });
            row.AutoItem().AlignRight().AlignMiddle().Column(col =>
            {
                col.Item().AlignRight().Text(DateTime.Now.ToString("dd MMM yyyy · HH:mm"))
                    .FontSize(9).FontColor("#5A5A72").FontFamily("Courier New");
                col.Item().AlignRight().PaddingTop(2).Text("Generat automat de aplicatie")
                    .FontSize(7).FontColor("#3A3A50").FontFamily("Courier New");
            });
        });
    }

    // footer
    private static void ComposeFooter(IContainer c)
    {
        c.Background("#101014").Padding(10).Row(row =>
        {
            row.RelativeItem().Text("WiFi Signal Optimizer · Proiect Licenta")
                .FontSize(8).FontColor("#3A3A50").FontFamily("Courier New");
            row.AutoItem().AlignRight().Text(x =>
            {
                x.Span("Pagina ").FontSize(8).FontColor("#3A3A50").FontFamily("Courier New");
                x.CurrentPageNumber().FontSize(8).FontColor("#3A3A50").FontFamily("Courier New");
                x.Span(" / ").FontSize(8).FontColor("#3A3A50").FontFamily("Courier New");
                x.TotalPages().FontSize(8).FontColor("#3A3A50").FontFamily("Courier New");
            });
        });
    }

    // main content
    private static void ComposeContent(
        IContainer c,
        NetworkHealthReport report,
        CoverageStats? before,
        CoverageStats? after)
    {
        c.PaddingTop(12).Column(col =>
        {
            col.Spacing(14);

            // 1. network info + score
            col.Item().Element(e => NetworkInfoSection(e, report));

            // 2. plain-language summary
            col.Item().Element(e => ExecutiveSummarySection(e, report));

            // 3. signal metrics (pills)
            col.Item().Element(e => SignalMetricsSection(e, report));

            // 4. K-Means channel analysis
            col.Item().Element(e => ChannelAnalysisSection(e, report));

            // 5. problematic neighbors
            if (report.ProblematicNeighbors.Count > 0)
                col.Item().Element(e => NeighborsSection(e, report));

            // 6. detected issues
            if (report.DetectedIssues.Count > 0)
                col.Item().Element(e => IssuesSection(e, report));

            // 7. AI recommendations
            col.Item().Element(e => RecommendationsSection(e, report));

            // 8. coverage statistics (if heatmap + optimization are available)
            if (before != null && after != null)
                col.Item().Element(e => CoverageCompareSection(e, before, after));

            // 9. scale legend
            col.Item().Element(e => LegendSection(e));
        });
    }

    // section 1: network info + large score
    private static void NetworkInfoSection(IContainer c, NetworkHealthReport report)
    {
        var (scoreHex, _) = ScoreColor(report.OverallHealthScore);

        c.Background("#151518").Border(1).BorderColor("#2A2A35").CornerRadius(6).Padding(14).Row(row =>
        {
            row.AutoItem().Width(80).AlignMiddle().Column(col =>
            {
                col.Item().AlignCenter().Text(report.OverallHealthScore.ToString("F0"))
                    .FontSize(36).Bold().FontColor(scoreHex).FontFamily("Courier New");
                col.Item().AlignCenter().Text("/ 100")
                    .FontSize(9).FontColor("#5A5A72").FontFamily("Courier New");
                col.Item().AlignCenter().Text(report.HealthClassification.ToUpper())
                    .FontSize(9).Bold().FontColor(scoreHex).FontFamily("Courier New");
            });

            row.AutoItem().Width(24).AlignCenter().AlignMiddle()
                .Element(e => e.Width(1).Background("#2A2A35"));

            row.RelativeItem().Column(col =>
            {
                col.Item().Text(string.IsNullOrEmpty(report.TargetSSID) ? "Retea necunoscuta" : report.TargetSSID)
                    .FontSize(18).Bold().FontColor("#F0F0F5");
                col.Item().PaddingTop(4).Row(r2 =>
                {
                    Pill(r2.AutoItem(), report.FrequencyBand, "#3B82F6", "#3B82F620");
                    r2.AutoItem().Width(8);
                    Pill(r2.AutoItem(), $"Canal {report.CurrentChannel}", "#8888AA", "#1A1A1F");
                    if (report.CurrentChannel != report.BestChannel)
                    {
                        r2.AutoItem().Width(8);
                        Pill(r2.AutoItem(), $"-> CH{report.BestChannel} recomandat", "#22C55E", "#22C55E20");
                    }
                });
                col.Item().PaddingTop(4).Text(report.BandCharacteristics)
                    .FontSize(9).FontColor("#8888AA");
            });
        });
    }

    // section 2: plain-language executive summary
    private static void ExecutiveSummarySection(IContainer c, NetworkHealthReport report)
    {
        var (scoreHex, _) = ScoreColor(report.OverallHealthScore);
        string summaryText = BuildSummaryText(report);
        string actionText  = BuildActionText(report);

        c.Background("#0E1420").Border(1).BorderColor("#1E3A5F").CornerRadius(6).Padding(14).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.AutoItem().Width(3).Background("#3B82F6").CornerRadius(1);
                row.AutoItem().Width(8);
                row.RelativeItem().AlignMiddle().Text("CE INSEAMNA ACEST RAPORT?")
                    .FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
            });
            col.Item().PaddingTop(8).Text(summaryText)
                .FontSize(10).FontColor("#C0C0D0").LineHeight(1.5f);
            col.Item().PaddingTop(6).Background("#0A1A0A").CornerRadius(4).Padding(8).Row(row =>
            {
                row.AutoItem().AlignMiddle().Text("Actiune recomandata: ")
                    .FontSize(9).Bold().FontColor("#22C55E");
                row.RelativeItem().AlignMiddle().Text(actionText)
                    .FontSize(9).FontColor("#A0C0A0");
            });
        });
    }

    private static string BuildSummaryText(NetworkHealthReport report)
    {
        string scoreDesc = report.OverallHealthScore switch
        {
            >= 85 => "excelenta",
            >= 70 => "buna",
            >= 50 => "acceptabila, dar cu probleme",
            >= 30 => "slaba, necesita atentie",
            _     => "critica, necesita interventie urgenta"
        };

        string issuesText = report.DetectedIssues.Count == 0
            ? "Nu au fost detectate probleme."
            : report.DetectedIssues.Count == 1
                ? "A fost detectata 1 problema."
                : $"Au fost detectate {report.DetectedIssues.Count} probleme.";

        string channelText = report.CurrentChannel == report.BestChannel
            ? $"Canalul {report.CurrentChannel} este optim pentru zona ta."
            : $"Canalul {report.CurrentChannel} este aglomerat — canalul {report.BestChannel} ar oferi performanta mai buna.";

        string neighborText = report.ProblematicNeighbors.Count == 0
            ? "Nu exista retele vecine care sa cauzeze interferente semnificative."
            : $"{report.ProblematicNeighbors.Count} retele vecine pot cauza interferente.";

        return $"Reteaua ta WiFi \"{report.TargetSSID}\" are o calitate {scoreDesc} (scor {report.OverallHealthScore:F0}/100). " +
               $"{issuesText} {channelText} {neighborText}";
    }

    private static string BuildActionText(NetworkHealthReport report)
    {
        if (report.OverallHealthScore >= 85)
            return "Nicio actiune necesara — reteaua functioneaza optim.";

        if (report.CurrentChannel != report.BestChannel)
            return $"Schimba canalul routerului din {report.CurrentChannel} in {report.BestChannel} din interfata de administrare a routerului (192.168.1.1).";

        var highIssue = report.DetectedIssues.FirstOrDefault(i => i.Severity == "Ridicata");
        if (highIssue != null)
            return highIssue.Recommendation;

        return report.Recommendations.Count > 0
            ? System.Text.RegularExpressions.Regex.Replace(report.Recommendations[0], @"[^\u0000-\u007F\u00C0-\u024F]", "").Trim()
            : "Urmeaza recomandarile din sectiunea de mai jos.";
    }

    // section 3: signal metrics with quality labels
    private static void SignalMetricsSection(IContainer c, NetworkHealthReport report)
    {
        string rssiLabel = report.CurrentSignalStrength switch
        {
            > -55 => "Excelent",
            > -65 => "Bun",
            > -75 => "Acceptabil",
            > -85 => "Slab",
            _     => "Critic"
        };
        string snrLabel = report.EstimatedSnr switch
        {
            >= 40 => "Excelent",
            >= 25 => "Bun",
            >= 15 => "Acceptabil",
            >= 10 => "Slab",
            _     => "Critic"
        };
        string congLabel = report.CongestionIndex switch
        {
            < 30 => "Scazut",
            < 60 => "Mediu",
            _    => "Ridicat"
        };

        c.Column(col =>
        {
            col.Item().Element(e => SectionTitle(e, "METRICI SEMNAL"));
            col.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Element(e => MetricCard(e, "SEMNAL RSSI",
                    $"{report.CurrentSignalStrength} dBm", rssiLabel,
                    SignalColor(report.CurrentSignalStrength)));
                row.AutoItem().Width(8);
                row.RelativeItem().Element(e => MetricCard(e, "SNR ESTIMAT",
                    $"{report.EstimatedSnr} dB", snrLabel,
                    SnrColor(report.EstimatedSnr)));
                row.AutoItem().Width(8);
                row.RelativeItem().Element(e => MetricCard(e, "VITEZA EST.",
                    report.EstimatedRealSpeed, "download/upload",
                    "#22C55E"));
                row.AutoItem().Width(8);
                row.RelativeItem().Element(e => MetricCard(e, "CONGESTIE",
                    $"{report.CongestionIndex}/100", congLabel,
                    CongestionColor(report.CongestionIndex)));
                row.AutoItem().Width(8);
                row.RelativeItem().Element(e => MetricCard(e, "EFICIENTA",
                    $"{report.SpeedEfficiency}%", "din viteza maxima",
                    "#A3E635"));
            });
            // metric explanations
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text("RSSI = puterea semnalului receptionat (mai aproape de 0 = mai bun)")
                    .FontSize(7).FontColor("#3A3A50").FontFamily("Courier New");
                row.AutoItem().Width(12);
                row.RelativeItem().Text("SNR = raport semnal/zgomot (mai mare = mai curat)")
                    .FontSize(7).FontColor("#3A3A50").FontFamily("Courier New");
            });
        });
    }

    // section 4: K-Means channel table
    private static void ChannelAnalysisSection(IContainer c, NetworkHealthReport report)
    {
        var channelsToShow = report.ChannelDetails
            .Where(ch => ch.InterferenceScore > 0 || ch.Channel == report.CurrentChannel || ch.Channel == report.BestChannel)
            .OrderBy(ch => ch.Channel)
            .ToList();

        c.Column(col =>
        {
            col.Item().Element(e => SectionTitle(e, "ANALIZA INTERFERENTE · K-MEANS CLUSTERING (ML.NET)"));
            col.Item().PaddingTop(3).Text("Algoritmul K-Means grupeaza canalele in 3 categorii: Optim (verde), Aglomerat (galben), Critic (rosu).")
                .FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            if (channelsToShow.Count == 0) return;
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(60);
                    cols.RelativeColumn();
                    cols.ConstantColumn(90);
                    cols.ConstantColumn(80);
                    cols.ConstantColumn(80);
                });

                table.Header(h =>
                {
                    h.Cell().Background("#1A1A1F").Padding(6).Text("CANAL").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("STATUS AI").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("INTERFERENTA").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("CO-CANAL").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("ADIACENT").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                });

                foreach (var ch in channelsToShow)
                {
                    bool isCurrent = ch.Channel == report.CurrentChannel;
                    bool isBest    = ch.Channel == report.BestChannel;
                    string rowBg   = isCurrent ? "#1E3A5F" : isBest ? "#1A3A2A" : "#151518";
                    string statusColor = ch.Status.Contains("Optim") ? "#22C55E" :
                                         ch.Status.Contains("Aglomerat") ? "#EAB308" : "#EF4444";
                    if (ch.Status.Contains("Necunoscut")) statusColor = "#5A5A72";

                    var chCell = table.Cell().Background(rowBg).BorderBottom(1).BorderColor("#2A2A35").Padding(6)
                        .Text($"CH {ch.Channel}{(isCurrent ? " *" : isBest ? " +" : "")}");
                    chCell.FontSize(9).FontColor(isCurrent ? "#3B82F6" : isBest ? "#22C55E" : "#C0C0D0").FontFamily("Courier New");
                    if (isCurrent || isBest) chCell.Bold();

                    table.Cell().Background(rowBg).BorderBottom(1).BorderColor("#2A2A35").Padding(6)
                        .Text(ch.Status.Replace("🟢 ", "").Replace("🟡 ", "").Replace("🔴 ", "").Replace("⚪ ", ""))
                        .FontSize(9).FontColor(statusColor).FontFamily("Courier New");

                    table.Cell().Background(rowBg).BorderBottom(1).BorderColor("#2A2A35").Padding(6)
                        .Text(ch.InterferenceScore.ToString("F0")).FontSize(9).FontColor("#C0C0D0").FontFamily("Courier New");

                    table.Cell().Background(rowBg).BorderBottom(1).BorderColor("#2A2A35").Padding(6)
                        .Text(ch.CoChannelNeighbors.ToString()).FontSize(9).FontColor("#C0C0D0").FontFamily("Courier New");

                    table.Cell().Background(rowBg).BorderBottom(1).BorderColor("#2A2A35").Padding(6)
                        .Text(ch.AdjacentNeighbors.ToString()).FontSize(9).FontColor("#C0C0D0").FontFamily("Courier New");
                }
            });

            col.Item().PaddingTop(4)
                .Text("Canal actual (*) Canal recomandat (+) | Co-canal = retele pe acelasi canal | Adiacent = retele pe canale vecine")
                .FontSize(7).FontColor("#3A3A50").FontFamily("Courier New");
        });
    }

    // section 5: problematic neighbors
    private static void NeighborsSection(IContainer c, NetworkHealthReport report)
    {
        c.Column(col =>
        {
            col.Item().Element(e => SectionTitle(e, $"RETELE VECINE CARE CAUZEAZA INTERFERENTE ({report.ProblematicNeighbors.Count})"));
            col.Item().PaddingTop(3).Text("Aceste retele WiFi din apropiere pot incetini conexiunea ta prin interferente radio.")
                .FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn();
                    cols.ConstantColumn(60);
                    cols.ConstantColumn(80);
                    cols.ConstantColumn(80);
                });

                table.Header(h =>
                {
                    h.Cell().Background("#1A1A1F").Padding(5).Text("SSID (Nume retea)").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(5).Text("CANAL").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(5).Text("SEMNAL").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(5).Text("AMENINTARE").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                });

                foreach (var n in report.ProblematicNeighbors.Take(8))
                {
                    string threatColor = n.ThreatLevel == "Ridicat" ? "#EF4444" :
                                         n.ThreatLevel == "Mediu"   ? "#EAB308" : "#22C55E";

                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(5)
                        .Text(n.SSID.Length > 20 ? n.SSID[..20] + "…" : n.SSID).FontSize(9).FontColor("#C0C0D0");
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(5)
                        .Text($"CH {n.Channel}").FontSize(9).FontColor("#C0C0D0").FontFamily("Courier New");
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(5)
                        .Text($"{n.SignalStrength} dBm").FontSize(9).FontColor(SignalColor(n.SignalStrength)).FontFamily("Courier New");
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(5)
                        .Text(n.ThreatLevel).FontSize(9).FontColor(threatColor).FontFamily("Courier New");
                }
            });
        });
    }

    // section 6: detected issues
    private static void IssuesSection(IContainer c, NetworkHealthReport report)
    {
        c.Column(outerCol =>
        {
            outerCol.Item().Element(e => SectionTitle(e, $"PROBLEME DETECTATE ({report.DetectedIssues.Count})"));
            outerCol.Item().PaddingTop(3).Text("Fiecare problema include o descriere si o recomandare concreta de rezolvare.")
                .FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            outerCol.Item().PaddingTop(6).Column(col =>
            {
                col.Spacing(6);
                foreach (var issue in report.DetectedIssues)
                {
                    string sevColor = issue.Severity == "Ridicata" ? "#EF4444" :
                                      issue.Severity == "Medie"    ? "#EAB308" : "#3B82F6";

                    col.Item().Background("#151518").Border(1).BorderColor("#2A2A35").CornerRadius(4).Padding(10).Column(inner =>
                    {
                        inner.Item().Row(r =>
                        {
                            Pill(r.AutoItem(), issue.Severity, sevColor, sevColor + "20");
                            r.AutoItem().Width(8);
                            r.RelativeItem().AlignMiddle().Text(issue.Title).FontSize(10).Bold().FontColor("#F0F0F5");
                        });
                        inner.Item().PaddingTop(4).Text(issue.Description).FontSize(9).FontColor("#8888AA");
                        inner.Item().PaddingTop(3).Row(r =>
                        {
                            r.AutoItem().Text("-> ").FontSize(9).FontColor("#22C55E");
                            r.RelativeItem().Text(issue.Recommendation).FontSize(9).FontColor("#22C55E");
                        });
                    });
                }
            });
        });
    }

    // section 7: AI recommendations
    private static void RecommendationsSection(IContainer c, NetworkHealthReport report)
    {
        c.Column(col =>
        {
            col.Item().Element(e => SectionTitle(e, "RECOMANDARI AI"));
            col.Item().PaddingTop(3).Text("Recomandarile urmatoare sunt generate de algoritmul AI pe baza analizei semnalului tau WiFi.")
                .FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            col.Item().PaddingTop(6).Background("#0A0A14").Border(1).BorderColor("#1E3A5F").CornerRadius(4).Padding(12).Column(inner =>
            {
                inner.Spacing(5);
                foreach (var rec in report.Recommendations)
                {
                    string cleanRec = System.Text.RegularExpressions.Regex.Replace(rec, @"[^\u0000-\u007F\u00C0-\u024F\u0370-\u03FF]", "");
                    inner.Item().Row(r =>
                    {
                        r.AutoItem().Width(12).Text("*").FontSize(10).FontColor("#3B82F6");
                        r.RelativeItem().Text(cleanRec.Trim()).FontSize(9).FontColor("#C0C0D0");
                    });
                }
            });
        });
    }

    // section 8: coverage statistics
    private static void CoverageCompareSection(IContainer c, CoverageStats before, CoverageStats after)
    {
        c.Column(col =>
        {
            col.Item().Element(e => SectionTitle(e, "STATISTICI ACOPERIRE WiFi · INAINTE vs DUPA OPTIMIZARE AI"));
            col.Item().PaddingTop(3).Text("Comparatie intre acoperirea WiFi inainte si dupa aplicarea recomandarilor AI de pozitionare a routerului.")
                .FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn();
                    cols.ConstantColumn(95);
                    cols.ConstantColumn(95);
                    cols.ConstantColumn(55);
                });

                table.Header(h =>
                {
                    h.Cell().Background("#1A1A1F").Padding(6).Text("METRIC").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("INAINTE").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("DUPA").FontSize(8).Bold().FontColor("#22C55E").FontFamily("Courier New");
                    h.Cell().Background("#1A1A1F").Padding(6).Text("DELTA").FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
                });

                void Row(string label, string bVal, string aVal, string delta, string deltaColor)
                {
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(6).Text(label).FontSize(9).FontColor("#C0C0D0");
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(6).Text(bVal).FontSize(9).FontColor("#8888AA").FontFamily("Courier New");
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(6).Text(aVal).FontSize(9).Bold().FontColor("#22C55E").FontFamily("Courier New");
                    table.Cell().Background("#151518").BorderBottom(1).BorderColor("#2A2A35").Padding(6).Text(delta).FontSize(9).FontColor(deltaColor).FontFamily("Courier New");
                }

                double covDelta = after.CoveragePercent - before.CoveragePercent;
                double sigDelta = after.AverageSignal   - before.AverageSignal;
                double excDelta = after.ExcellentPercent - before.ExcellentPercent;
                double dedDelta = after.DeadZonePercent  - before.DeadZonePercent;
                double thrDelta = after.EstimatedThroughputMbps - before.EstimatedThroughputMbps;

                Row("Acoperire utilizabila (semnal OK)",
                    $"{before.CoveragePercent:F0}%", $"{after.CoveragePercent:F0}%",
                    $"{covDelta:+0;-0;0}%", covDelta >= 0 ? "#22C55E" : "#EF4444");

                Row("Semnal mediu in casa",
                    $"{before.AverageSignal:F1} dBm", $"{after.AverageSignal:F1} dBm",
                    $"{sigDelta:+0.0;-0.0;0}", sigDelta >= 0 ? "#22C55E" : "#EF4444");

                Row("Zona excelenta (>-55 dBm)",
                    $"{before.ExcellentPercent:F0}%", $"{after.ExcellentPercent:F0}%",
                    $"{excDelta:+0;-0;0}%", excDelta >= 0 ? "#22C55E" : "#EF4444");

                Row("Zone moarte fara semnal",
                    $"{before.DeadZonePercent:F0}%", $"{after.DeadZonePercent:F0}%",
                    $"{dedDelta:+0;-0;0}%", dedDelta <= 0 ? "#22C55E" : "#EF4444");

                Row("Viteza estimata",
                    $"{before.EstimatedThroughputMbps:F0} Mbps", $"{after.EstimatedThroughputMbps:F0} Mbps",
                    $"{thrDelta:+0;-0;0} Mbps", thrDelta >= 0 ? "#22C55E" : "#EF4444");

                Row("Scor general",
                    $"{before.OverallScore:F0}/100 ({before.ScoreGrade})",
                    $"{after.OverallScore:F0}/100 ({after.ScoreGrade})",
                    $"{after.OverallScore - before.OverallScore:+0;-0;0}",
                    after.OverallScore >= before.OverallScore ? "#22C55E" : "#EF4444");
            });
            col.Item().PaddingTop(4)
                .Text($"Model: Log-Distance Path Loss + Raycasting · {after.TotalSamples:N0} esantioane evaluate")
                .FontSize(7).FontColor("#3A3A50").FontFamily("Courier New");
        });
    }

    // section 9: scale legend, helps non-technical users
    private static void LegendSection(IContainer c)
    {
        c.Background("#101014").Border(1).BorderColor("#2A2A35").CornerRadius(6).Padding(12).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.AutoItem().Width(3).Background("#5A5A72").CornerRadius(1);
                row.AutoItem().Width(8);
                row.RelativeItem().AlignMiddle().Text("GHID DE INTERPRETARE · CE INSEAMNA VALORILE")
                    .FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
            });
            col.Item().PaddingTop(8).Row(row =>
            {
                // column 1: RSSI
                row.RelativeItem().Column(sub =>
                {
                    sub.Item().Text("SEMNAL RSSI (dBm)").FontSize(8).Bold().FontColor("#8888AA").FontFamily("Courier New");
                    sub.Item().PaddingTop(3).Element(e => LegendRow(e, "-30 pana la -55", "Excelent", "#22C55E"));
                    sub.Item().Element(e => LegendRow(e, "-55 pana la -65", "Bun", "#A3E635"));
                    sub.Item().Element(e => LegendRow(e, "-65 pana la -75", "Acceptabil", "#EAB308"));
                    sub.Item().Element(e => LegendRow(e, "-75 pana la -85", "Slab", "#F97316"));
                    sub.Item().Element(e => LegendRow(e, "sub -85", "Critic", "#EF4444"));
                });
                row.AutoItem().Width(16);
                // column 2: SNR
                row.RelativeItem().Column(sub =>
                {
                    sub.Item().Text("SNR — Raport Semnal/Zgomot (dB)").FontSize(8).Bold().FontColor("#8888AA").FontFamily("Courier New");
                    sub.Item().PaddingTop(3).Element(e => LegendRow(e, "peste 40 dB", "Excelent — conexiune stabila", "#22C55E"));
                    sub.Item().Element(e => LegendRow(e, "25 - 40 dB", "Bun — navigare normala", "#A3E635"));
                    sub.Item().Element(e => LegendRow(e, "15 - 25 dB", "Acceptabil — posibile intreruperi", "#EAB308"));
                    sub.Item().Element(e => LegendRow(e, "sub 15 dB", "Slab — deconectari frecvente", "#EF4444"));
                });
                row.AutoItem().Width(16);
                // column 3: congestion + score
                row.RelativeItem().Column(sub =>
                {
                    sub.Item().Text("CONGESTIE CANAL (0-100)").FontSize(8).Bold().FontColor("#8888AA").FontFamily("Courier New");
                    sub.Item().PaddingTop(3).Element(e => LegendRow(e, "0 - 30", "Scazut — canal liber", "#22C55E"));
                    sub.Item().Element(e => LegendRow(e, "30 - 60", "Mediu — canal partial ocupat", "#EAB308"));
                    sub.Item().Element(e => LegendRow(e, "peste 60", "Ridicat — canal aglomerat", "#EF4444"));
                    sub.Item().PaddingTop(6).Text("SCOR GENERAL (0-100)").FontSize(8).Bold().FontColor("#8888AA").FontFamily("Courier New");
                    sub.Item().PaddingTop(3).Element(e => LegendRow(e, "85 - 100", "Excelent (A)", "#22C55E"));
                    sub.Item().Element(e => LegendRow(e, "70 - 84", "Bun (B)", "#A3E635"));
                    sub.Item().Element(e => LegendRow(e, "50 - 69", "Acceptabil (C)", "#EAB308"));
                    sub.Item().Element(e => LegendRow(e, "sub 50", "Slab / Critic (D-F)", "#EF4444"));
                });
            });
        });
    }

    private static void LegendRow(IContainer c, string range, string label, string color)
    {
        c.PaddingVertical(1).Row(row =>
        {
            row.AutoItem().Width(6).AlignMiddle()
                .Element(e => e.Width(6).Height(6).Background(color).CornerRadius(1));
            row.AutoItem().Width(5);
            row.AutoItem().Width(90).Text(range).FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            row.RelativeItem().Text(label).FontSize(7).FontColor(color).FontFamily("Courier New");
        });
    }

    // helpers
    private static void SectionTitle(IContainer c, string text)
    {
        c.Row(row =>
        {
            row.AutoItem().Width(3).Background("#3B82F6").CornerRadius(1);
            row.AutoItem().Width(8);
            row.RelativeItem().AlignMiddle().Text(text)
                .FontSize(8).Bold().FontColor("#5A5A72").FontFamily("Courier New");
        });
    }

    private static void MetricCard(IContainer c, string label, string value, string qualityLabel, string color)
    {
        c.Background("#151518").Border(1).BorderColor("#2A2A35").CornerRadius(5).Padding(10).Column(col =>
        {
            col.Item().AlignCenter().Text(label).FontSize(7).FontColor("#5A5A72").FontFamily("Courier New");
            col.Item().AlignCenter().PaddingTop(2).Text(value).FontSize(13).Bold().FontColor(color).FontFamily("Courier New");
            col.Item().AlignCenter().PaddingTop(2).Text(qualityLabel).FontSize(7).FontColor(color).FontFamily("Courier New");
        });
    }

    private static void Pill(IContainer c, string text, string textColor, string bgColor)
    {
        c.Background(bgColor).CornerRadius(4).PaddingHorizontal(4).PaddingVertical(2).Text(text)
            .FontSize(8).Bold().FontColor(textColor).FontFamily("Courier New");
    }

    private static (string hex, string _) ScoreColor(double score) => score switch
    {
        >= 85 => ("#22C55E", ""),
        >= 70 => ("#A3E635", ""),
        >= 50 => ("#EAB308", ""),
        >= 30 => ("#F97316", ""),
        _     => ("#EF4444", "")
    };

    private static string SignalColor(int dbm) => dbm switch
    {
        > -55 => "#22C55E",
        > -65 => "#A3E635",
        > -75 => "#EAB308",
        > -85 => "#F97316",
        _     => "#EF4444"
    };

    private static string SnrColor(int snr) => snr switch
    {
        >= 40 => "#22C55E",
        >= 25 => "#A3E635",
        >= 15 => "#EAB308",
        _     => "#EF4444"
    };

    private static string CongestionColor(int cong) => cong switch
    {
        < 30  => "#22C55E",
        < 60  => "#EAB308",
        _     => "#EF4444"
    };
}
