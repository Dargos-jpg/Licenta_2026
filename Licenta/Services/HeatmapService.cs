using System;
using System.Collections.Generic;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WifiSignalOptimizer.Models;
using WifiSignalOptimizer.Views;

namespace WifiSignalOptimizer.Services
{
    public class HeatmapService
    {
        /// <summary>
        /// Generează heatmap WiFi cu IDW + path loss + raycasting pe obstacole și pereți desenați.
        /// Acceptă HeatmapPoint (record simplu X/Y/SignalStrength) filtrat per SSID.
        /// </summary>
        public BitmapSource GenerateHeatmap(
            List<HeatmapPoint> points,
            List<MapElement> obstacles,
            StrokeCollection drawnWalls,
            int width, int height,
            double pixelsPerMeter)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            const double idwPower = 2.0;
            var wallSegments = ExtractWallSegments(drawnWalls);
            double ppm = pixelsPerMeter > 0 ? pixelsPerMeter : 20.0;

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;
                    double closestDist = double.MaxValue;
                    int closestIdx = 0;

                    // ── Pasul 1: IDW de bază (fără obstacole)
                    for (int i = 0; i < points.Count; i++)
                    {
                        var p = points[i];
                        double dist = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));
                        if (dist < 1.0) dist = 1.0;

                        if (dist < closestDist) { closestDist = dist; closestIdx = i; }

                        double weight = 1.0 / Math.Pow(dist, idwPower);
                        numerator += weight * p.SignalStrength;
                        denominator += weight;
                    }

                    double estimatedSignal = denominator > 0 ? numerator / denominator : -100;

                    // ── Pasul 2: Path loss natural în aer (0.5 dBm/m după primii 2m)
                    double distMeters = closestDist / ppm;
                    double freeSpaceLoss = distMeters > 2.0 ? (distMeters - 2.0) * 0.5 : 0.0;
                    estimatedSignal -= freeSpaceLoss;

                    // ── Pasul 3: Raycasting spre cel mai apropiat punct
                    var closest = points[closestIdx];
                    estimatedSignal -= GetObstacleAttenuation(
                        x, y, closest.X, closest.Y, obstacles, wallSegments);

                    // ── Pasul 4: Culoare + fade-out lin
                    Color color = GetColorFromSignal(estimatedSignal);
                    byte alpha = 200;
                    if (estimatedSignal < -75)
                    {
                        double fadeRatio = Math.Min(1.0, (-75 - estimatedSignal) / 15.0);
                        alpha = (byte)(200 * (1.0 - fadeRatio));
                    }

                    for (int dy = 0; dy < 2 && y + dy < height; dy++)
                        for (int dx = 0; dx < 2 && x + dx < width; dx++)
                        {
                            int idx = ((y + dy) * stride) + ((x + dx) * 4);
                            pixels[idx] = color.B;
                            pixels[idx + 1] = color.G;
                            pixels[idx + 2] = color.R;
                            pixels[idx + 3] = alpha;
                        }
                }
            }

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        // ══════════════════════════════════════════════════
        // RAYCASTING
        // ══════════════════════════════════════════════════

        private double GetObstacleAttenuation(
            double x1, double y1, double x2, double y2,
            List<MapElement> obstacles,
            List<(double X1, double Y1, double X2, double Y2)> wallSegments)
        {
            double total = 0;

            if (obstacles != null)
            {
                const double r = 20.0;
                foreach (var obs in obstacles)
                    if (LineIntersectsCircle(x1, y1, x2, y2, obs.X + r, obs.Y + r, r))
                        total += obs.SignalPenalty;
            }

            if (wallSegments != null)
                foreach (var w in wallSegments)
                    if (LinesIntersect(x1, y1, x2, y2, w.X1, w.Y1, w.X2, w.Y2))
                        total += 10.0;

            return total;
        }

        private List<(double X1, double Y1, double X2, double Y2)> ExtractWallSegments(
            StrokeCollection strokes)
        {
            var list = new List<(double, double, double, double)>();
            if (strokes == null) return list;
            foreach (var stroke in strokes)
            {
                var pts = stroke.StylusPoints;
                for (int i = 0; i < pts.Count - 1; i++)
                    list.Add((pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y));
            }
            return list;
        }

        private bool LinesIntersect(
            double x1, double y1, double x2, double y2,
            double x3, double y3, double x4, double y4)
        {
            double d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (d == 0) return false;
            double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d;
            double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / d;
            return t > 0 && t < 1 && u > 0 && u < 1;
        }

        private bool LineIntersectsCircle(
            double x1, double y1, double x2, double y2,
            double cx, double cy, double r)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double lenSq = dx * dx + dy * dy;
            if (lenSq == 0)
                return Math.Pow(cx - x1, 2) + Math.Pow(cy - y1, 2) <= r * r;
            double t = Math.Max(0, Math.Min(1,
                ((cx - x1) * dx + (cy - y1) * dy) / lenSq));
            return Math.Pow(cx - (x1 + t * dx), 2) +
                   Math.Pow(cy - (y1 + t * dy), 2) <= r * r;
        }

        // ══════════════════════════════════════════════════
        // GRADIENT PRO: Verde smarald → Lime → Galben → Roșu
        // ══════════════════════════════════════════════════

        private Color GetColorFromSignal(double signal)
        {
            if (signal >= -40) return Color.FromRgb(0, 180, 0);
            if (signal <= -85) return Color.FromRgb(220, 20, 20);

            if (signal > -55)
            {
                double t = (signal - (-55.0)) / 15.0;
                return Color.FromRgb((byte)(t * 50), (byte)(180 + t * 75), 0);
            }
            else if (signal > -70)
            {
                double t = (signal - (-70.0)) / 15.0;
                return Color.FromRgb((byte)(255 - t * 205), 255, 0);
            }
            else
            {
                double t = (signal - (-85.0)) / 15.0;
                return Color.FromRgb(255, (byte)(255 * t), 0);
            }
        }
    }
}