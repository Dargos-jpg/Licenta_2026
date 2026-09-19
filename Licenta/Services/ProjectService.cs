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
        private static string LastProjectFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "WifiSignalOptimizer", "lastproject.txt");

        public static void SaveLastProjectPath(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(LastProjectFile)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(LastProjectFile, path);
            }
            catch { /* non-critical */ }
        }

        public static string? GetLastProjectPath()
        {
            try
            {
                if (!File.Exists(LastProjectFile)) return null;
                var path = File.ReadAllText(LastProjectFile).Trim();
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// loads a project directly from a known path without showing the open dialog.
        /// </summary>
        public (ProjectData? data, StrokeCollection? strokes) LoadProjectFromPath(string filePath)
        {
            try
            {
                string jsonString = File.ReadAllText(filePath);
                var projectData = JsonSerializer.Deserialize<ProjectData>(jsonString);

                if (projectData != null)
                {
                    StrokeCollection? strokes = null;
                    if (!string.IsNullOrEmpty(projectData.SketchIsfBase64))
                    {
                        try
                        {
                            byte[] isfBytes = Convert.FromBase64String(projectData.SketchIsfBase64);
                            using var ms = new MemoryStream(isfBytes);
                            strokes = new StrokeCollection(ms);
                        }
                        catch { }
                    }
                    SaveLastProjectPath(filePath);
                    return (projectData, strokes);
                }

                var oldPoints = JsonSerializer.Deserialize<List<SurveyPoint>>(jsonString);
                if (oldPoints?.Count > 0)
                {
                    SaveLastProjectPath(filePath);
                    return (new ProjectData
                    {
                        SurveyPoints = oldPoints,
                        MapElements  = [],
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

        /// <summary>
        /// saves the complete project: points, obstacles, calibration, vector sketch and PNG.
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
                // serialize the vector sketch ISF to Base64
                string? isfBase64 = null;
                if (sketchStrokes != null && sketchStrokes.Count > 0)
                {
                    using var ms = new MemoryStream();
                    sketchStrokes.Save(ms);
                    isfBase64 = Convert.ToBase64String(ms.ToArray());
                }

                // serialize the sketch PNG to Base64
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
                SaveLastProjectPath(saveDialog.FileName);

                // detailed message with what was saved
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
        /// loads the complete project. also supports the old format (backward compatibility).
        /// returns ProjectData and, optionally, a reconstructed StrokeCollection.
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

                // try the new format (full ProjectData)
                var projectData = JsonSerializer.Deserialize<ProjectData>(jsonString);

                if (projectData != null)
                {
                    // reconstruct the StrokeCollection from the ISF Base64
                    StrokeCollection? strokes = null;
                    if (!string.IsNullOrEmpty(projectData.SketchIsfBase64))
                    {
                        try
                        {
                            byte[] isfBytes = Convert.FromBase64String(projectData.SketchIsfBase64);
                            using var ms = new MemoryStream(isfBytes);
                            strokes = new StrokeCollection(ms);
                        }
                        catch { /* corrupt ISF, continue without the sketch */ }
                    }

                    var parts = new List<string> { $"{projectData.SurveyPoints.Count} puncte WiFi" };
                    if (projectData.MapElements.Count > 0) parts.Add($"{projectData.MapElements.Count} obstacole");
                    if (strokes != null) parts.Add("schiță vectorială");
                    if (projectData.IsCalibrated) parts.Add($"calibrare ({projectData.PixelsPerMeter:F1} px/m)");

                    MessageBox.Show(
                        $"Proiect încărcat!\n\nConținut: {string.Join(", ", parts)}.",
                        "Încărcare Completă", MessageBoxButton.OK, MessageBoxImage.Information);

                    SaveLastProjectPath(openDialog.FileName);
                    return (projectData, strokes);
                }

                // fallback: old file with just a List<SurveyPoint>
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