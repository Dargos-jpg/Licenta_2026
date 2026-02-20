using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services
{
    public class HeatmapService
    {
        // Am adăugat parametrul List<MapElement> obstacles
        public BitmapSource GenerateHeatmap(List<SurveyPoint> points, List<MapElement> obstacles, int width, int height, double pixelsPerMeter)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            double power = 2.0;
            double maxDistanceMeters = 8.0;
            double radius = maxDistanceMeters * pixelsPerMeter;

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;
                    double minDistance = double.MaxValue;
                    double closestSignal = -100;
                    bool hasSignal = false;

                    foreach (var p in points)
                    {
                        double dist = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));

                        if (dist < 1.0)
                        {
                            closestSignal = p.SignalStrength;
                            minDistance = 0;
                            hasSignal = true;
                            break;
                        }

                        if (dist > radius) continue;
                        hasSignal = true;

                        // 1. Atenuarea prin aer (Path Loss)
                        double distMeters = dist / pixelsPerMeter;
                        double pathLoss = distMeters * 4.0;

                        // 2. Atenuarea prin obstacole (Raycasting)
                        double obstaclePenalty = GetObstacleAttenuation(x, y, p.X, p.Y, obstacles);

                        // 3. Semnalul final estimat în acest pixel
                        double attenuatedSignal = p.SignalStrength - pathLoss - obstaclePenalty;

                        double weight = 1.0 / Math.Pow(dist, power);
                        numerator += weight * attenuatedSignal;
                        denominator += weight;
                    }

                    double estimatedSignal = -100;
                    if (hasSignal)
                    {
                        if (minDistance == 0) estimatedSignal = closestSignal;
                        else estimatedSignal = numerator / denominator;
                    }

                    Color color = GetColorFromSignal(estimatedSignal);
                    byte alpha = (estimatedSignal <= -85 || !hasSignal) ? (byte)0 : (byte)160;

                    for (int dy = 0; dy < 2 && y + dy < height; dy++)
                    {
                        for (int dx = 0; dx < 2 && x + dx < width; dx++)
                        {
                            int blockIndex = ((y + dy) * stride) + ((x + dx) * 4);
                            pixels[blockIndex] = color.B;
                            pixels[blockIndex + 1] = color.G;
                            pixels[blockIndex + 2] = color.R;
                            pixels[blockIndex + 3] = alpha;
                        }
                    }
                }
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        // ==========================================
        // MATEMATICA PENTRU DETECȚIA OBSTACOLELOR
        // ==========================================
        private double GetObstacleAttenuation(double x1, double y1, double x2, double y2, List<MapElement> obstacles)
        {
            if (obstacles == null || obstacles.Count == 0) return 0;

            double totalPenalty = 0;
            // Pictogramele tale au 40x40 px, deci raza de impact e de ~20 px
            double obstacleRadius = 20.0;

            foreach (var obs in obstacles)
            {
                // Deoarece X și Y reprezintă colțul stânga-sus al imaginii, centrul e la X+20, Y+20
                double centerX = obs.X + obstacleRadius;
                double centerY = obs.Y + obstacleRadius;

                // Verificăm dacă linia imaginară între pixel și semnal taie acest obiect
                if (LineIntersectsCircle(x1, y1, x2, y2, centerX, centerY, obstacleRadius))
                {
                    totalPenalty += obs.SignalPenalty; // Adăugăm cei 12 dBm sau 15 dBm din modelul tău!
                }
            }
            return totalPenalty;
        }

        // Algoritm de intersecție Linie - Cerc
        private bool LineIntersectsCircle(double x1, double y1, double x2, double y2, double cx, double cy, double r)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double lengthSquared = dx * dx + dy * dy;

            if (lengthSquared == 0) return (Math.Pow(cx - x1, 2) + Math.Pow(cy - y1, 2)) <= r * r;

            // Proiectăm centrul obstacolului pe linia semnalului
            double t = Math.Max(0, Math.Min(1, ((cx - x1) * dx + (cy - y1) * dy) / lengthSquared));

            double closestX = x1 + t * dx;
            double closestY = y1 + t * dy;

            double distanceSquared = Math.Pow(cx - closestX, 2) + Math.Pow(cy - closestY, 2);

            return distanceSquared <= (r * r); // Dacă e mai mic decât raza, linia a tăiat obiectul
        }

        private Color GetColorFromSignal(double signal)
        {
            if (signal < -85) return Colors.Red;
            if (signal > -45) return Colors.Lime;

            double normalized = (signal - (-85)) / (-45 - (-85));
            byte r, g, b;

            if (normalized < 0.5)
            {
                r = 255;
                g = (byte)(255 * (normalized * 2));
                b = 0;
            }
            else
            {
                r = (byte)(255 * (1.0 - (normalized - 0.5) * 2));
                g = 255;
                b = 0;
            }

            return Color.FromRgb(r, g, b);
        }
    }
}