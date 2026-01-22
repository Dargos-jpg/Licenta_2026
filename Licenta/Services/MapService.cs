using WifiSignalOptimizer.Services;
using System.IO;
using System.Windows.Media.Imaging;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Service for loading and managing floor plan images
/// </summary>
public sealed class MapService : IMapService
{
    public string[] SupportedExtensions => [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    public async Task<BitmapImage?> LoadFloorPlanAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            // Load image asynchronously to avoid UI blocking
            return await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze(); // Make it thread-safe for cross-thread access
                return bitmap;
            });
        }
        catch (Exception ex)
        {
            // Log exception in production
            System.Diagnostics.Debug.WriteLine($"Failed to load floor plan: {ex.Message}");
            return null;
        }
    }
}