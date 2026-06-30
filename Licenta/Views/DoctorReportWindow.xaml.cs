using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Views
{
    public partial class DoctorReportWindow : Window
    {
        private NetworkHealthReport _report;

        public DoctorReportWindow(NetworkHealthReport report)
        {
            InitializeComponent();
            _report = report;

            Loaded += (_, _) => PopulateUI();
        }

        private void PopulateUI()
        {
            // timestamp
            TimestampText.Text = DateTime.Now.ToString("dd MMM yyyy · HH:mm");

            // score and color
            var (scoreColor, scoreHex) = GetScoreColor(_report.OverallHealthScore);
            ScoreText.Text = _report.OverallHealthScore.ToString("F0");
            ScoreTextBrush.Color = scoreColor;
            ScoreRingBrush.Color = scoreColor;
            ScoreGlow.Color = scoreColor;
            ClassBrush.Color = scoreColor;
            ClassificationText.Text = _report.HealthClassification.ToUpper();

            // SSID and band
            SsidText.Text = string.IsNullOrEmpty(_report.TargetSSID) ? "Rețea necunoscută" : _report.TargetSSID;
            BandText.Text = _report.FrequencyBand;
            BandCharText.Text = _report.BandCharacteristics;

            // signal
            SignalText.Text = _report.CurrentSignalStrength.ToString();
            SignalBrush.Color = _report.CurrentSignalStrength >= -60 ? scoreColor :
                                _report.CurrentSignalStrength >= -75 ? Color.FromRgb(249, 115, 22) :
                                                                        Color.FromRgb(239, 68, 68);

            // speed
            SpeedText.Text = _report.EstimatedRealSpeed;
            SpeedMaxText.Text = _report.TheoreticalMaxSpeed + " max";

            // channel
            ChannelText.Text = _report.CurrentChannel.ToString();
            ChannelOptimalText.Text = _report.CurrentChannel == _report.BestChannel
                ? "✓ optim" : $"→ CH{_report.BestChannel} recomandat";

            // congestion
            CongestionText.Text = _report.CongestionIndex.ToString();

            // health bar
            HealthPctText.Text = _report.OverallHealthScore.ToString("F0") + "%";
            HealthBarGrad.Color = scoreColor;

            // animate the bar once layout is computed
            InterferenceChart.Loaded += (_, _) =>
            {
                double barWidth = InterferenceChart.ActualWidth;
                if (barWidth > 10)
                    HealthBar.Width = barWidth * (_report.OverallHealthScore / 100.0);
            };
            HealthBar.Width = 0;

            // K-Means channels
            var channelsToShow = _report.ChannelDetails
                .Where(c => c.InterferenceScore > 0 || c.Channel == _report.CurrentChannel || c.Channel == _report.BestChannel)
                .OrderBy(c => c.Channel)
                .ToList();
            ChannelsList.ItemsSource = channelsToShow;

            // neighbors
            NeighborCountText.Text = $"{_report.TotalNeighbors} rețele în zonă";
            NeighborsList.ItemsSource = _report.ProblematicNeighbors.Take(6);

            // recommendations
            var allInsights = new List<string>();
            allInsights.AddRange(_report.Recommendations);
            InsightsList.ItemsSource = allInsights;

            // issues
            IssueCountText.Text = $"{_report.DetectedIssues.Count} probleme găsite";
            IssuesList.ItemsSource = _report.DetectedIssues;

            // footer
            EfficiencyText.Text = $"Eficiență rețea: {_report.SpeedEfficiency}%";
            NeighborSummaryText.Text = $"{_report.TotalNeighbors} vecini în zonă";
            CanalOptimText.Text = $"Canal optim: CH{_report.BestChannel}";

            // interference chart
            InterferenceChart.SizeChanged += (_, _) => DrawInterferenceChart();
            DrawInterferenceChart();
        }

        private void DrawInterferenceChart()
        {
            InterferenceChart.Children.Clear();

            double w = InterferenceChart.ActualWidth;
            double h = InterferenceChart.ActualHeight;
            if (w < 10 || h < 10) return;

            var channels = _report.ChannelDetails
                .Where(c => c.InterferenceScore > 0 || c.Channel == _report.CurrentChannel)
                .OrderBy(c => c.Channel)
                .ToList();

            if (channels.Count == 0) return;

            double maxScore = Math.Max(1, channels.Max(c => c.InterferenceScore));
            double barWidth = Math.Max(4, (w - 40) / channels.Count - 6);
            double chartH = h - 30; // reserve space at the bottom for labels

            // horizontal grid lines (4 lines)
            for (int i = 1; i <= 4; i++)
            {
                double lineY = chartH - (chartH * i / 4.0);
                var line = new Line
                {
                    X1 = 0,
                    Y1 = lineY,
                    X2 = w,
                    Y2 = lineY,
                    Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                };
                InterferenceChart.Children.Add(line);
            }

            for (int i = 0; i < channels.Count; i++)
            {
                var ch = channels[i];
                double ratio = ch.InterferenceScore / maxScore;
                double barH = Math.Max(2, chartH * ratio);
                double x = 20 + i * ((w - 40) / channels.Count);
                double y = chartH - barH;

                bool isCurrent = ch.Channel == _report.CurrentChannel;
                bool isOptimal = ch.Channel == _report.BestChannel;

                // glow below the bar
                if (ratio > 0.1)
                {
                    var glow = new Rectangle
                    {
                        Width = barWidth,
                        Height = barH + 8,
                        Fill = new LinearGradientBrush(
                            isCurrent ? Color.FromArgb(40, 59, 130, 246) :
                            isOptimal ? Color.FromArgb(40, 34, 197, 94) :
                                         Color.FromArgb(20, 180, 180, 255),
                            Colors.Transparent, 90),
                        RadiusX = 3,
                        RadiusY = 3
                    };
                    Canvas.SetLeft(glow, x);
                    Canvas.SetTop(glow, y - 8);
                    InterferenceChart.Children.Add(glow);
                }

                // main bar
                Color barColor = isCurrent ? Color.FromRgb(59, 130, 246) :
                                 isOptimal ? Color.FromRgb(34, 197, 94) :
                                              GetInterferenceColor(ratio);

                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = Math.Max(2, barH),
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = new SolidColorBrush(barColor),
                    Opacity = isCurrent || isOptimal ? 1.0 : 0.75
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                InterferenceChart.Children.Add(rect);

                // channel label
                var label = new TextBlock
                {
                    Text = ch.Channel.ToString(),
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = isCurrent
                        ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                        : isOptimal
                            ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                            : new SolidColorBrush(Color.FromArgb(120, 170, 170, 200)),
                    Width = barWidth,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, chartH + 4);
                InterferenceChart.Children.Add(label);

                // value above the bar (if current or optimal)
                if (isCurrent || isOptimal)
                {
                    var valLabel = new TextBlock
                    {
                        Text = isCurrent ? "●" : "★",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(barColor),
                        Width = barWidth,
                        TextAlignment = TextAlignment.Center
                    };
                    Canvas.SetLeft(valLabel, x);
                    Canvas.SetTop(valLabel, Math.Max(0, y - 16));
                    InterferenceChart.Children.Add(valLabel);
                }
            }

            // health bar at the end
            double barWidthMax = InterferenceChart.ActualWidth;
            if (barWidthMax > 10)
                HealthBar.Width = barWidthMax * (_report.OverallHealthScore / 100.0);
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

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}