using System.Windows.Media.Imaging;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// service contract for floor plan map operations
/// </summary>
public interface IMapService
{
    /// <summary>
    /// loads a floor plan image from the specified file path
    /// </summary>
    /// <param name="filePath">path to the image file</param>
    /// <returns>bitmapImage of the floor plan, or null if loading fails</returns>
    Task<BitmapImage?> LoadFloorPlanAsync(string filePath);

    /// <summary>
    /// loads a floor plan image from a Base64 encoded PNG string (used when restoring a saved project)
    /// </summary>
    /// <param name="base64">base64 encoded PNG string</param>
    /// <returns>bitmapImage of the floor plan, or null if loading fails</returns>
    Task<BitmapImage?> LoadFromBase64Async(string base64);

    /// <summary>
    /// gets supported image file extensions
    /// </summary>
    string[] SupportedExtensions { get; }
}