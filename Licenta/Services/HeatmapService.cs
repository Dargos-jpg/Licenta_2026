using System;
using System.Collections.Generic;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services
{
    public class HeatmapService
    {
        /// <summary>
        /// generates a WiFi heatmap using IDW + path loss + raycasting against obstacles and drawn walls.
        /// accepts HeatmapPoint (a simple X/Y/SignalStrength record) filtered per SSID.
        /// </summary>
        public BitmapSource GenerateHeatmap(
            List<HeatmapPoint> points,
            List<MapElement> obstacles,
            StrokeCollection drawnWalls,
            int width, int height,
            double pixelsPerMeter,
            int channel = 1)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null!;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            // IDW power 3.0, clearer contours, less of a "balloon" effect
            const double idwPower = 3.0;
            var wallSegments = ExtractWallSegments(drawnWalls);
            double ppm = pixelsPerMeter > 0 ? pixelsPerMeter : 20.0;
            // per-band free-space air loss: higher frequencies attenuate faster
            double airLossPerMeter = channel > 177 ? 5.5 : channel > 14 ? 4.0 : 2.5;

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;

                    foreach (var p in points)
                    {
                        double distPixels = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));
                        if (distPixels < 1.0) distPixels = 1.0;

                        double distMeters = distPixels / ppm;

                        // 1. raycasting for every point (not just the closest one)
                        double wallPenalty = GetObstacleAttenuation(
                            x, y, p.X, p.Y, obstacles, wallSegments);

                        // 2. per-band Free Space Path Loss
                        double airLoss = distMeters * airLossPerMeter;

                        double effectiveSignal = p.SignalStrength - wallPenalty - airLoss;

                        // physics-informed weight: walls reduce both signal AND interpolation influence.
                        // skip exp() when no attenuation — the common case, saves millions of calls.
                        double wallFactor = wallPenalty > 0.0 ? Math.Exp(wallPenalty / 30.0) : 1.0;
                        double weight = 1.0 / (Math.Pow(distPixels, idwPower) * wallFactor);
                        numerator += weight * effectiveSignal;
                        denominator += weight;
                    }

                    double estimatedSignal = denominator > 0 ? numerator / denominator : -100;

                    Color color = GetColorFromSignal(estimatedSignal);

                    // smooth fade-out at the edges of the network
                    byte alpha = 210;
                    if (estimatedSignal < -80)
                    {
                        double fadeRatio = Math.Min(1.0, (-80 - estimatedSignal) / 10.0);
                        alpha = (byte)(210 * (1.0 - fadeRatio));
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

        // dead zone overlay

        /// <summary>
        /// generates an overlay that marks below-threshold signal areas with a semi-transparent red mask.
        /// layered over the normal heatmap, it visually highlights coverage "holes".
        /// </summary>
        public BitmapSource GenerateDeadZoneOverlay(
            List<HeatmapPoint> points,
            List<MapElement> obstacles,
            StrokeCollection drawnWalls,
            int width, int height,
            double pixelsPerMeter,
            double thresholdDbm = -75.0,
            int channel = 1)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null!;

            const double idwPower = 3.0;
            var wallSegments = ExtractWallSegments(drawnWalls);
            double ppm = pixelsPerMeter > 0 ? pixelsPerMeter : 20.0;
            double airLossPerMeter = channel > 177 ? 5.5 : channel > 14 ? 4.0 : 2.5;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                double numerator = 0, denominator = 0;
                foreach (var p in points)
                {
                    double distPx = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));
                    if (distPx < 1.0) distPx = 1.0;
                    double wallPenalty = GetObstacleAttenuation(x, y, p.X, p.Y, obstacles, wallSegments);
                    double airLoss = (distPx / ppm) * airLossPerMeter;
                    double effectiveSignal = p.SignalStrength - wallPenalty - airLoss;
                    double wallFactor = wallPenalty > 0.0 ? Math.Exp(wallPenalty / 30.0) : 1.0;
                    double weight = 1.0 / (Math.Pow(distPx, idwPower) * wallFactor);
                    numerator  += weight * effectiveSignal;
                    denominator += weight;
                }
                double signal = denominator > 0 ? numerator / denominator : -100;

                if (signal < thresholdDbm)
                {
                    // intensity correlates with how far below the threshold the signal is: redder = weaker
                    double intensity = Math.Min(1.0, (thresholdDbm - signal) / 20.0);
                    byte alpha = (byte)(160 * intensity);

                    for (int dy = 0; dy < 2 && y + dy < height; dy++)
                    for (int dx = 0; dx < 2 && x + dx < width; dx++)
                    {
                        int idx = (y + dy) * stride + (x + dx) * 4;
                        pixels[idx]     = 0;    // B
                        pixels[idx + 1] = 0;    // G
                        pixels[idx + 2] = 200;  // R
                        pixels[idx + 3] = alpha;
                    }
                }
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        // raycasting

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

        // color gradient: emerald green -> lime -> yellow -> red

        private Color GetColorFromSignal(double signal)
        {
            // standard WiFi palette (Ekahau/NetSpot style)
            // -40 (green) -> -55 (yellow-green) -> -70 (yellow) -> -80 (orange) -> -90 (red)

            if (signal >= -40) return Color.FromRgb(0, 200, 0);
            if (signal <= -90) return Color.FromRgb(255, 0, 0);

            if (signal >= -55)
            {
                // green -> yellow-green (-40 ... -55)
                double t = (signal - (-55.0)) / 15.0;
                return Color.FromRgb((byte)(173 - (int)(173 * t)), (byte)(255 - (int)(55 * t)), 0);
            }
            if (signal >= -70)
            {
                // yellow-green -> yellow (-55 ... -70)
                double t = (signal - (-70.0)) / 15.0;
                return Color.FromRgb((byte)(255 - (int)(82 * t)), 255, 0);
            }
            if (signal >= -80)
            {
                // yellow -> orange (-70 ... -80)
                double t = (signal - (-80.0)) / 10.0;
                return Color.FromRgb(255, (byte)(165 + (int)(90 * t)), 0);
            }

            // orange -> red (-80 ... -90)
            double t2 = (signal - (-90.0)) / 10.0;
            return Color.FromRgb(255, (byte)(165 * t2), 0);
        }
    }
}
