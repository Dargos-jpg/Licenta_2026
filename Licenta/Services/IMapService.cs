using System.Windows.Media.Imaging;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Service contract for floor plan map operations
/// </summary>
public interface IMapService
{
    /// <summary>
    /// Loads a floor plan image from the specified file path
    /// </summary>
    /// <param name="filePath">Path to the image file</param>
    /// <returns>BitmapImage of the floor plan, or null if loading fails</returns>
    Task<BitmapImage?> LoadFloorPlanAsync(string filePath);

    /// <summary>
    /// Loads a floor plan image from a Base64 encoded PNG string (used when restoring a saved project)
    /// </summary>
    /// <param name="base64">Base64 encoded PNG string</param>
    /// <returns>BitmapImage of the floor plan, or null if loading fails</returns>
    Task<BitmapImage?> LoadFromBase64Async(string base64);

    /// <summary>
    /// Gets supported image file extensions
    /// </summary>
    string[] SupportedExtensions { get; }
}