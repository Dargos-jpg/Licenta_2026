using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Views;

public partial class DoctorPanelView : UserControl
{
    public static readonly DependencyProperty ReportProperty =
        DependencyProperty.Register(
            nameof(Report),
            typeof(NetworkHealthReport),
            typeof(DoctorPanelView),
            new PropertyMetadata(null, OnReportChanged));

    public NetworkHealthReport? Report
    {
        get => (NetworkHealthReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    private static void OnReportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DoctorPanelView panel && e.NewValue is NetworkHealthReport report)
            panel.PopulateUI(report);
    }

    public DoctorPanelView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (Report != null) DrawInterferenceChart(Report);
        };
    }

    private void PopulateUI(NetworkHealthReport report)
    {
        TimestampText.Text = DateTime.Now.ToString("dd MMM yyyy · HH:mm");

        var (scoreColor, _) = GetScoreColor(report.OverallHealthScore);
        ScoreText.Text = report.OverallHealthScore.ToString("F0");
        ScoreTextBrush.Color = scoreColor;
        ScoreRingBrush.Color = scoreColor;
        ScoreGlow.Color = scoreColor;
        ClassBrush.Color = scoreColor;
        ClassificationText.Text = report.HealthClassification.ToUpper();

        SsidText.Text = string.IsNullOrEmpty(report.TargetSSID) ? "Rețea necunoscută" : report.TargetSSID;
        BandText.Text = report.FrequencyBand;
        BandCharText.Text = report.BandCharacteristics;

        SignalText.Text = report.CurrentSignalStrength.ToString();
        SignalBrush.Color = report.CurrentSignalStrength >= -60 ? scoreColor :
                            report.CurrentSignalStrength >= -75 ? Color.FromRgb(249, 115, 22) :
                                                                   Color.FromRgb(239, 68, 68);

        SpeedText.Text = report.EstimatedRealSpeed;
        SpeedMaxText.Text = report.TheoreticalMaxSpeed + " max";

        ChannelText.Text = report.CurrentChannel.ToString();
        ChannelOptimalText.Text = report.CurrentChannel == report.BestChannel
            ? "✓ optim" : $"→ CH{report.BestChannel} recomandat";

        CongestionText.Text = report.CongestionIndex.ToString();

        HealthPctText.Text = report.OverallHealthScore.ToString("F0") + "%";
        HealthBarGrad.Color = scoreColor;
        HealthBar.Width = 0;

        var channelsToShow = report.ChannelDetails
            .Where(c => c.InterferenceScore > 0 || c.Channel == report.CurrentChannel || c.Channel == report.BestChannel)
            .OrderBy(c => c.Channel)
            .ToList();
        ChannelsList.ItemsSource = channelsToShow;

        NeighborCountText.Text = $"{report.TotalNeighbors} rețele în zonă";
        NeighborsList.ItemsSource = report.ProblematicNeighbors.Take(6);

        var allInsights = new List<string>();
        allInsights.AddRange(report.Recommendations);
        InsightsList.ItemsSource = allInsights;

        IssueCountText.Text = $"{report.DetectedIssues.Count} probleme găsite";
        IssuesList.ItemsSource = report.DetectedIssues;

        EfficiencyText.Text = $"Eficiență rețea: {report.SpeedEfficiency}%";
        NeighborSummaryText.Text = $"{report.TotalNeighbors} vecini în zonă";
        CanalOptimText.Text = $"Canal optim: CH{report.BestChannel}";

        InterferenceChart.SizeChanged += (_, _) => DrawInterferenceChart(report);
        DrawInterferenceChart(report);

        // Animăm bara de sănătate după layout
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            double barWidth = InterferenceChart.ActualWidth;
            if (barWidth > 10)
                HealthBar.Width = barWidth * (report.OverallHealthScore / 100.0);
        });
    }

    private void DrawInterferenceChart(NetworkHealthReport report)
    {
        InterferenceChart.Children.Clear();

        double w = InterferenceChart.ActualWidth;
        double h = InterferenceChart.ActualHeight;
        if (w < 10 || h < 10) return;

        var channels = report.ChannelDetails
            .Where(c => c.InterferenceScore > 0 || c.Channel == report.CurrentChannel)
            .OrderBy(c => c.Channel)
            .ToList();

        if (channels.Count == 0) return;

        double maxScore = Math.Max(1, channels.Max(c => c.InterferenceScore));
        double barWidth = Math.Max(4, (w - 40) / channels.Count - 6);
        double chartH = h - 30;

        for (int i = 1; i <= 4; i++)
        {
            double lineY = chartH - (chartH * i / 4.0);
            InterferenceChart.Children.Add(new Line
            {
                X1 = 0, Y1 = lineY, X2 = w, Y2 = lineY,
                Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 4 }
            });
        }

        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            double ratio = ch.InterferenceScore / maxScore;
            double barH = Math.Max(2, chartH * ratio);
            double x = 20 + i * ((w - 40) / channels.Count);
            double y = chartH - barH;

            bool isCurrent = ch.Channel == report.CurrentChannel;
            bool isOptimal = ch.Channel == report.BestChannel;

            if (ratio > 0.1)
            {
                var glow = new Rectangle
                {
                    Width = barWidth, Height = barH + 8,
                    Fill = new LinearGradientBrush(
                        isCurrent ? Color.FromArgb(40, 59, 130, 246) :
                        isOptimal ? Color.FromArgb(40, 34, 197, 94) :
                                    Color.FromArgb(20, 180, 180, 255),
                        Colors.Transparent, 90),
                    RadiusX = 3, RadiusY = 3
                };
                Canvas.SetLeft(glow, x); Canvas.SetTop(glow, y - 8);
                InterferenceChart.Children.Add(glow);
            }

            Color barColor = isCurrent ? Color.FromRgb(59, 130, 246) :
                             isOptimal ? Color.FromRgb(34, 197, 94) :
                                          GetInterferenceColor(ratio);

            var rect = new Rectangle
            {
                Width = barWidth, Height = Math.Max(2, barH),
                RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(barColor),
                Opacity = isCurrent || isOptimal ? 1.0 : 0.75
            };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            InterferenceChart.Children.Add(rect);

            var labelTb = new TextBlock
            {
                Text = ch.Channel.ToString(), FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                Foreground = isCurrent ? new SolidColorBrush(Color.FromRgb(59, 130, 246)) :
                             isOptimal ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) :
                                          new SolidColorBrush(Color.FromArgb(120, 170, 170, 200)),
                Width = barWidth, TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(labelTb, x); Canvas.SetTop(labelTb, chartH + 4);
            InterferenceChart.Children.Add(labelTb);

            if (isCurrent || isOptimal)
            {
                var valLabel = new TextBlock
                {
                    Text = isCurrent ? "●" : "★", FontSize = 10,
                    Foreground = new SolidColorBrush(barColor),
                    Width = barWidth, TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(valLabel, x); Canvas.SetTop(valLabel, Math.Max(0, y - 16));
                InterferenceChart.Children.Add(valLabel);
            }
        }

        double barWidthMax = InterferenceChart.ActualWidth;
        if (barWidthMax > 10 && Report != null)
            HealthBar.Width = barWidthMax * (report.OverallHealthScore / 100.0);
    }

    private Color GetInterferenceColor(double ratio)
    {
        if (ratio < 0.3) return Color.FromRgb(34, 197, 94);
        if (ratio < 0.6) return Color.FromRgb(234, 179, 8);
        if (ratio < 0.85) return Color.FromRgb(249, 115, 22);
        return Color.FromRgb(239, 68, 68);
    }

    private (Color color, string hex) GetScoreColor(double score)
    {
        if (score >= 85) return (Color.FromRgb(34, 197, 94), "#22C55E");
        if (score >= 70) return (Color.FromRgb(163, 230, 53), "#A3E635");
        if (score >= 50) return (Color.FromRgb(234, 179, 8), "#EAB308");
        if (score >= 30) return (Color.FromRgb(249, 115, 22), "#F97316");
        return (Color.FromRgb(239, 68, 68), "#EF4444");
    }
}
