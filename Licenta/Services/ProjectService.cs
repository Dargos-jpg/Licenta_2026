using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32; // Pentru dialogurile de Save/Open
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services
{
    public class ProjectService
    {
        // Salvăm lista de puncte
        public void SaveProject(List<SurveyPoint> points)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "WiFi Survey Project (*.json)|*.json",
                FileName = $"Survey_{DateTime.Now:yyyyMMdd_HHmm}"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string jsonString = JsonSerializer.Serialize(points, options);
                    File.WriteAllText(saveDialog.FileName, jsonString);
                    MessageBox.Show("Proiect salvat cu succes!", "Succes", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Eroare la salvare: {ex.Message}", "Eroare", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Încărcăm lista de puncte
        public List<SurveyPoint>? LoadProject()
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "WiFi Survey Project (*.json)|*.json"
            };

            if (openDialog.ShowDialog() == true)
            {
                try
                {
                    string jsonString = File.ReadAllText(openDialog.FileName);
                    var points = JsonSerializer.Deserialize<List<SurveyPoint>>(jsonString);

                    // Verificare simplă
                    if (points != null)
                    {
                        MessageBox.Show($"Am încărcat {points.Count} puncte de măsurare.", "Succes", MessageBoxButton.OK, MessageBoxImage.Information);
                        return points;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Eroare la încărcare: {ex.Message}", "Eroare", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return null; // Dacă a anulat sau a crăpat
        }
    }
}