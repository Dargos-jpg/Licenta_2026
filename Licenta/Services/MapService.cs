using System.IO;
using System.Windows.Media.Imaging;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Service for loading and managing floor plan images
/// </summary>
public sealed class MapService : IMapService
{
    public string[] SupportedExtensions => [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    /// <summary>
    /// Încarcă un plan de pe disc (fișier imagine).
    /// </summary>
    public async Task<BitmapImage?> LoadFloorPlanAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            // Citim bytes-ii pe background thread (I/O)
            byte[] fileBytes = await Task.Run(() => File.ReadAllBytes(filePath));

            // BitmapImage cu StreamSource TREBUIE creat pe UI thread (STA)
            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(fileBytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load floor plan: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reconstituie un BitmapImage dintr-un string Base64 (PNG salvat în proiect JSON).
    /// Folosit la încărcarea proiectului pentru a restaura schița ca FloorPlan.
    /// </summary>
    public async Task<BitmapImage?> LoadFromBase64Async(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        try
        {
            // Decodăm Base64 pe background thread (CPU)
            byte[] bytes = await Task.Run(() => Convert.FromBase64String(base64));

            // BitmapImage cu StreamSource TREBUIE creat pe UI thread (STA)
            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load floor plan from Base64: {ex.Message}");
            return null;
        }
    }
}