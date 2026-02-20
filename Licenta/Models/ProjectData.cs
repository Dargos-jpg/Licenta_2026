using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Models
{
    /// <summary>
    /// Modelul complet al unui proiect salvat:
    /// puncte WiFi + obstacole + calibrare + schița vectorială + preview PNG
    /// </summary>
    public class ProjectData
    {
        public List<SurveyPoint> SurveyPoints { get; set; } = [];
        public List<MapElement> MapElements { get; set; } = [];
        public double PixelsPerMeter { get; set; } = 50.0;
        public bool IsCalibrated { get; set; } = false;

        // Schița vectorială serializată (format ISF nativ InkCanvas, Base64)
        // Null dacă proiectul nu conține o schiță desenată manual
        public string? SketchIsfBase64 { get; set; } = null;

        // Preview PNG al schiței (Base64) — folosit și ca FloorPlan la reload
        public string? SketchPngBase64 { get; set; } = null;
    }
}