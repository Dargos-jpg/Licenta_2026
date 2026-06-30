using System.IO;
using System.Windows.Media.Imaging;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// service for loading and managing floor plan images
/// </summary>
public sealed class MapService : IMapService
{
    public string[] SupportedExtensions => [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    /// <summary>
    /// loads a floor plan from disk (image file).
    /// </summary>
    public async Task<BitmapImage?> LoadFloorPlanAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            // read the bytes on a background thread (I/O)
            byte[] fileBytes = await Task.Run(() => File.ReadAllBytes(filePath));

            // a BitmapImage with StreamSource must be created on the UI thread (STA)
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
    /// reconstructs a BitmapImage from a Base64 string (PNG saved in the project JSON).
    /// used when loading a project to restore the sketch as the FloorPlan.
    /// </summary>
    public async Task<BitmapImage?> LoadFromBase64Async(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        try
        {
            // decode Base64 on a background thread (CPU)
            byte[] bytes = await Task.Run(() => Convert.FromBase64String(base64));

            // a BitmapImage with StreamSource must be created on the UI thread (STA)
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