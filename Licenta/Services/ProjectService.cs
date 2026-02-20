using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services
{
    public class ProjectService
    {
        /// <summary>
        /// Salvează proiectul complet: puncte, obstacole, calibrare, schiță vectorială și PNG.
        /// </summary>
        public void SaveProject(
            List<SurveyPoint> points,
            List<MapElement> mapElements,
            double pixelsPerMeter,
            bool isCalibrated,
            StrokeCollection? sketchStrokes = null,
            BitmapSource? sketchPng = null)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "WiFi Survey Project (*.json)|*.json",
                FileName = $"Survey_{DateTime.Now:yyyyMMdd_HHmm}"
            };

            if (saveDialog.ShowDialog() != true) return;

            try
            {
                // Serializăm schița vectorială ISF → Base64
                string? isfBase64 = null;
                if (sketchStrokes != null && sketchStrokes.Count > 0)
                {
                    using var ms = new MemoryStream();
                    sketchStrokes.Save(ms);
                    isfBase64 = Convert.ToBase64String(ms.ToArray());
                }

                // Serializăm PNG-ul schiței → Base64
                string? pngBase64 = null;
                if (sketchPng != null)
                {
                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(sketchPng));
                    encoder.Save(ms);
                    pngBase64 = Convert.ToBase64String(ms.ToArray());
                }

                var projectData = new ProjectData
                {
                    SurveyPoints = points,
                    MapElements = mapElements,
                    PixelsPerMeter = pixelsPerMeter,
                    IsCalibrated = isCalibrated,
                    SketchIsfBase64 = isfBase64,
                    SketchPngBase64 = pngBase64
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(projectData, options);
                File.WriteAllText(saveDialog.FileName, jsonString);

                // Mesaj detaliat cu ce s-a salvat
                var parts = new List<string> { $"{points.Count} puncte WiFi" };
                if (mapElements.Count > 0) parts.Add($"{mapElements.Count} obstacole");
                if (isfBase64 != null) parts.Add("schiță vectorială");
                if (isCalibrated) parts.Add($"calibrare ({pixelsPerMeter:F1} px/m)");

                MessageBox.Show(
                    $"Proiect salvat cu succes!\n\nConținut: {string.Join(", ", parts)}.",
                    "Salvare Completă", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Eroare la salvare: {ex.Message}", "Eroare",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Încarcă proiectul complet. Suportă și formatul vechi (compatibilitate).
        /// Returnează ProjectData și opțional StrokeCollection reconstituită.
        /// </summary>
        public (ProjectData? data, StrokeCollection? strokes) LoadProject()
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "WiFi Survey Project (*.json)|*.json"
            };

            if (openDialog.ShowDialog() != true) return (null, null);

            try
            {
                string jsonString = File.ReadAllText(openDialog.FileName);

                // Încercăm formatul nou (ProjectData complet)
                var projectData = JsonSerializer.Deserialize<ProjectData>(jsonString);

                if (projectData != null)
                {
                    // Reconstituim StrokeCollection din ISF Base64
                    StrokeCollection? strokes = null;
                    if (!string.IsNullOrEmpty(projectData.SketchIsfBase64))
                    {
                        try
                        {
                            byte[] isfBytes = Convert.FromBase64String(projectData.SketchIsfBase64);
                            using var ms = new MemoryStream(isfBytes);
                            strokes = new StrokeCollection(ms);
                        }
                        catch { /* ISF corupt - continuăm fără schiță */ }
                    }

                    var parts = new List<string> { $"{projectData.SurveyPoints.Count} puncte WiFi" };
                    if (projectData.MapElements.Count > 0) parts.Add($"{projectData.MapElements.Count} obstacole");
                    if (strokes != null) parts.Add("schiță vectorială");
                    if (projectData.IsCalibrated) parts.Add($"calibrare ({projectData.PixelsPerMeter:F1} px/m)");

                    MessageBox.Show(
                        $"Proiect încărcat!\n\nConținut: {string.Join(", ", parts)}.",
                        "Încărcare Completă", MessageBoxButton.OK, MessageBoxImage.Information);

                    return (projectData, strokes);
                }

                // Fallback: fișier vechi cu doar List<SurveyPoint>
                var oldPoints = JsonSerializer.Deserialize<List<SurveyPoint>>(jsonString);
                if (oldPoints != null && oldPoints.Count > 0)
                {
                    MessageBox.Show(
                        $"Am încărcat {oldPoints.Count} puncte (format vechi, fără calibrare sau schiță).",
                        "Compatibilitate", MessageBoxButton.OK, MessageBoxImage.Information);

                    return (new ProjectData
                    {
                        SurveyPoints = oldPoints,
                        MapElements = [],
                        PixelsPerMeter = 50.0,
                        IsCalibrated = false
                    }, null);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Eroare la încărcare: {ex.Message}", "Eroare",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return (null, null);
        }
    }
}