using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services
{
    public class HeatmapService
    {
        public BitmapSource GenerateHeatmap(List<SurveyPoint> points, int width, int height)
        {
            if (points == null || points.Count == 0 || width <= 0 || height <= 0)
                return null;

            // 1. Pregătim un array de pixeli (BGRA - 4 bytes per pixel)
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];

            // 2. Setări IDW (Inverse Distance Weighting)
            double power = 2.0; // Puterea de interpolare (2 e standard)
            double radius = 300.0; // Cât de departe "bate" un punct (în pixeli)

            // 3. Parcurgem fiecare pixel al imaginii (optimizare: poți sări din 2 în 2 pixeli pentru viteză)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double numerator = 0.0;
                    double denominator = 0.0;
                    double minDistance = double.MaxValue;
                    int closestSignal = -100;

                    // Calculăm influența fiecărui punct de măsură asupra acestui pixel
                    foreach (var p in points)
                    {
                        double dist = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));

                        // Dacă suntem exact pe punct, luăm valoarea lui
                        if (dist < 1.0)
                        {
                            closestSignal = p.SignalStrength;
                            minDistance = 0;
                            break;
                        }

                        // Dacă punctul e prea departe, îl ignorăm (pentru performanță)
                        if (dist > radius) continue;

                        double weight = 1.0 / Math.Pow(dist, power);
                        numerator += weight * p.SignalStrength;
                        denominator += weight;
                    }

                    // Calculăm semnalul estimat în acest pixel
                    double estimatedSignal;
                    if (minDistance == 0) estimatedSignal = closestSignal;
                    else if (denominator == 0) estimatedSignal = -100; // Fără semnal
                    else estimatedSignal = numerator / denominator;

                    // 4. Transformăm semnalul în culoare (Color Map)
                    Color color = GetColorFromSignal(estimatedSignal);

                    // Scriem pixelul în array (BGRA)
                    int index = (y * stride) + (x * 4);

                    // Aplicăm transparență doar unde nu e semnal
                    byte alpha = (estimatedSignal <= -95) ? (byte)0 : (byte)150; // 150 = Transparență medie

                    pixels[index] = color.B;     // Blue
                    pixels[index + 1] = color.G; // Green
                    pixels[index + 2] = color.R; // Red
                    pixels[index + 3] = alpha;   // Alpha
                }
            }

            // 5. Creăm imaginea finală
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

            // FIXUL ESTE AICI: "Înghețăm" imaginea ca să poată fi trimisă între thread-uri
            bitmap.Freeze();

            return bitmap;
        }

        // Funcție ajutătoare: Transformă dBm (-90 ... -30) în Culori (Roșu ... Verde)
        private Color GetColorFromSignal(double signal)
        {
            // Limite
            if (signal < -90) return Colors.Red;
            if (signal > -30) return Colors.Lime;

            // Normalizăm între 0 (slab) și 1 (puternic)
            double normalized = (signal - (-90)) / (-30 - (-90));

            // Interpolare simplă Roșu -> Galben -> Verde
            byte r, g, b;

            if (normalized < 0.5) // Roșu -> Galben
            {
                r = 255;
                g = (byte)(255 * (normalized * 2));
                b = 0;
            }
            else // Galben -> Verde
            {
                r = (byte)(255 * (1.0 - (normalized - 0.5) * 2));
                g = 255;
                b = 0;
            }

            return Color.FromRgb(r, g, b);
        }
    }
}