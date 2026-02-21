using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services
{
    public class HeatmapService
    {
        public BitmapSource GenerateHeatmap(
            List<SurveyPoint> points,
            List<MapElement> obstacles,
            int width, int height,
            double pixelsPerMeter)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            // IDW power — 2.5 oferă un blending mai fin decât 2.0 standard
            double power = 2.5;

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;
                    double closestSignal = -100;
                    bool exactHit = false;
                    bool hasSignal = false;

                    foreach (var p in points)
                    {
                        double dist = Math.Sqrt(
                            Math.Pow(x - p.X, 2) +
                            Math.Pow(y - p.Y, 2));

                        // Pixel exact pe punctul de măsurare
                        if (dist < 1.0)
                        {
                            closestSignal = p.SignalStrength;
                            exactHit = true;
                            hasSignal = true;
                            break;
                        }

                        // ✅ Fără radius cutoff — IDW global, fără tăieturi cu compasul
                        hasSignal = true;

                        // 1. Path loss prin aer (2.5 dBm/m)
                        double distMeters = dist / pixelsPerMeter;
                        double pathLoss = distMeters * 2.5;

                        // 2. Atenuare prin obstacole emoji (raycasting)
                        double obstaclePenalty = GetObstacleAttenuation(x, y, p.X, p.Y, obstacles);

                        // 3. Semnal atenuat final
                        double attenuatedSignal = p.SignalStrength - pathLoss - obstaclePenalty;

                        double weight = 1.0 / Math.Pow(dist, power);
                        numerator += weight * attenuatedSignal;
                        denominator += weight;
                    }

                    double estimatedSignal = -100;
                    if (hasSignal)
                    {
                        estimatedSignal = exactHit
                            ? closestSignal
                            : numerator / denominator;
                    }

                    Color color = GetColorFromSignal(estimatedSignal);

                    // Fade-out gradual la semnal slab — fără tăietură bruscă
                    byte alpha;
                    if (!hasSignal || estimatedSignal <= -90)
                        alpha = 0;
                    else if (estimatedSignal <= -80)
                        alpha = (byte)(160.0 * (estimatedSignal - (-90.0)) / 10.0);
                    else
                        alpha = 160;

                    // Scriem 2x2 pixeli (optimizare performanță)
                    for (int dy = 0; dy < 2 && y + dy < height; dy++)
                    {
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
            }

            var bitmap = BitmapSource.Create(
                width, height, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        // ══════════════════════════════════════════════
        // DETECȚIE OBSTACOLE — Raycasting Linie-Cerc
        // ══════════════════════════════════════════════

        private double GetObstacleAttenuation(
            double x1, double y1,
            double x2, double y2,
            List<MapElement> obstacles)
        {
            if (obstacles == null || obstacles.Count == 0) return 0;

            double totalPenalty = 0;
            double obstacleRadius = 20.0; // Emoji 40x40px → raza de impact = 20px

            foreach (var obs in obstacles)
            {
                double centerX = obs.X + obstacleRadius;
                double centerY = obs.Y + obstacleRadius;

                if (LineIntersectsCircle(x1, y1, x2, y2, centerX, centerY, obstacleRadius))
                    totalPenalty += obs.SignalPenalty;
            }

            return totalPenalty;
        }

        /// <summary>
        /// Intersecție Linie–Cerc prin proiecție ortogonală.
        /// </summary>
        private bool LineIntersectsCircle(
            double x1, double y1,
            double x2, double y2,
            double cx, double cy, double r)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double lenSq = dx * dx + dy * dy;

            if (lenSq == 0)
                return (Math.Pow(cx - x1, 2) + Math.Pow(cy - y1, 2)) <= r * r;

            double t = Math.Max(0, Math.Min(1,
                ((cx - x1) * dx + (cy - y1) * dy) / lenSq));

            double closestX = x1 + t * dx;
            double closestY = y1 + t * dy;
            double distSq = Math.Pow(cx - closestX, 2) + Math.Pow(cy - closestY, 2);

            return distSq <= r * r;
        }

        // ══════════════════════════════════════════════
        // GRADIENT: Roșu → Portocaliu → Galben → Verde
        // -85 dBm (slab/roșu) → -45 dBm (excelent/verde)
        // ══════════════════════════════════════════════

        private Color GetColorFromSignal(double signal)
        {
            if (signal <= -85) return Colors.Red;
            if (signal >= -45) return Colors.Lime;

            double t = (signal - (-85.0)) / (-45.0 - (-85.0)); // 0.0 → 1.0

            byte r, g, b = 0;

            if (t < 0.5)
            {
                // Roșu → Galben
                r = 255;
                g = (byte)(255 * (t * 2.0));
            }
            else
            {
                // Galben → Verde
                r = (byte)(255 * (1.0 - (t - 0.5) * 2.0));
                g = 255;
            }

            return Color.FromRgb(r, g, b);
        }
    }
}