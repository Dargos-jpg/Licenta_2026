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
        /// Generează un heatmap WiFi folosind IDW cu raycasting real pe obstacole (emoji + pereți desenați).
        /// Nu folosește pathLoss artificial — semnalul se extinde fluid și e tăiat doar de obstacole.
        /// </summary>
        public BitmapSource GenerateHeatmap(
            List<SurveyPoint> points,
            List<MapElement> obstacles,
            StrokeCollection drawnWalls,   // Liniile din EditorCanvas (pereții schiței)
            int width, int height,
            double pixelsPerMeter)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            // IDW power 2.5 — echilibru bun între influență locală și blending global
            const double power = 2.5;

            // Extragem segmentele de linie din schiță o singură dată (performanță)
            var wallSegments = ExtractWallSegments(drawnWalls);

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;
                    double closestSignal = -100;
                    bool exactHit = false;

                    foreach (var p in points)
                    {
                        double dist = Math.Sqrt(
                            Math.Pow(x - p.X, 2) +
                            Math.Pow(y - p.Y, 2));

                        if (dist < 1.0)
                        {
                            closestSignal = p.SignalStrength;
                            exactHit = true;
                            break;
                        }

                        // Fără pathLoss artificial — semnalul e tăiat DOAR de obstacole reale
                        double obstaclePenalty = GetObstacleAttenuation(
                            x, y, p.X, p.Y, obstacles, wallSegments);

                        double attenuatedSignal = p.SignalStrength - obstaclePenalty;

                        double weight = 1.0 / Math.Pow(dist, power);
                        numerator += weight * attenuatedSignal;
                        denominator += weight;
                    }

                    double estimatedSignal = exactHit
                        ? closestSignal
                        : (denominator > 0 ? numerator / denominator : -100);

                    Color color = GetColorFromSignal(estimatedSignal);

                    // Fade-out gradual la semnal foarte slab
                    byte alpha = 180;
                    if (estimatedSignal < -85)
                        alpha = (byte)Math.Max(0, 180 - ((-85 - estimatedSignal) * 15));

                    // Scriem 2x2 pixeli
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

        // ══════════════════════════════════════════════════════
        // DETECȚIE OBSTACOLE — Raycasting pe emoji + linii
        // ══════════════════════════════════════════════════════

        private double GetObstacleAttenuation(
            double x1, double y1,
            double x2, double y2,
            List<MapElement> obstacles,
            List<(double X1, double Y1, double X2, double Y2)> wallSegments)
        {
            double totalPenalty = 0;

            // 1. Raycasting prin emoji-uri (cerc de 20px rază)
            if (obstacles != null)
            {
                const double obstacleRadius = 20.0;
                foreach (var obs in obstacles)
                {
                    double centerX = obs.X + obstacleRadius;
                    double centerY = obs.Y + obstacleRadius;
                    if (LineIntersectsCircle(x1, y1, x2, y2, centerX, centerY, obstacleRadius))
                        totalPenalty += obs.SignalPenalty;
                }
            }

            // 2. Raycasting prin pereții desenați (linii geometrice)
            if (wallSegments != null)
            {
                foreach (var wall in wallSegments)
                {
                    if (LinesIntersect(x1, y1, x2, y2, wall.X1, wall.Y1, wall.X2, wall.Y2))
                        totalPenalty += 10.0; // −10 dBm per perete desenat tăiat
                }
            }

            return totalPenalty;
        }

        // ══════════════════════════════════════════════════════
        // EXTRAGERE SEGMENTE DIN StrokeCollection
        // ══════════════════════════════════════════════════════

        private List<(double X1, double Y1, double X2, double Y2)> ExtractWallSegments(
            StrokeCollection strokes)
        {
            var segments = new List<(double, double, double, double)>();
            if (strokes == null) return segments;

            foreach (var stroke in strokes)
            {
                StylusPointCollection pts = stroke.StylusPoints;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    segments.Add((pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y));
                }
            }

            return segments;
        }

        // ══════════════════════════════════════════════════════
        // ALGORITMI GEOMETRICI
        // ══════════════════════════════════════════════════════

        /// <summary>Intersecție Linie–Linie (Cramer)</summary>
        private bool LinesIntersect(
            double x1, double y1, double x2, double y2,
            double x3, double y3, double x4, double y4)
        {
            double denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (denom == 0) return false; // paralele

            double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
            double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / denom;

            return t > 0 && t < 1 && u > 0 && u < 1;
        }

        /// <summary>Intersecție Linie–Cerc prin proiecție ortogonală</summary>
        private bool LineIntersectsCircle(
            double x1, double y1, double x2, double y2,
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

            return (Math.Pow(cx - closestX, 2) + Math.Pow(cy - closestY, 2)) <= r * r;
        }

        // ══════════════════════════════════════════════════════
        // GRADIENT: Roșu → Galben → Verde
        // -85 dBm (slab) → -45 dBm (excelent)
        // ══════════════════════════════════════════════════════

        private Color GetColorFromSignal(double signal)
        {
            if (signal <= -85) return Colors.Red;
            if (signal >= -45) return Colors.Lime;

            double t = (signal - (-85.0)) / (-45.0 - (-85.0));

            byte r, g, b = 0;
            if (t < 0.5)
            {
                r = 255;
                g = (byte)(255 * (t * 2.0));
            }
            else
            {
                r = (byte)(255 * (1.0 - (t - 0.5) * 2.0));
                g = 255;
            }

            return Color.FromRgb(r, g, b);
        }
    }
}