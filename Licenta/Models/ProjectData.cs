using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Models
{
    /// <summary>
    /// complete model of a saved project:
    /// WiFi points, obstacles, calibration, vector sketch, PNG preview
    /// </summary>
    public class ProjectData
    {
        public List<SurveyPoint> SurveyPoints { get; set; } = [];
        public List<MapElement> MapElements { get; set; } = [];
        public double PixelsPerMeter { get; set; } = 50.0;
        public bool IsCalibrated { get; set; } = false;

        // serialized vector sketch (native InkCanvas ISF format, Base64)
        // null if the project does not contain a manually drawn sketch
        public string? SketchIsfBase64 { get; set; } = null;

        // PNG preview of the sketch (Base64), also used as the FloorPlan on reload
        public string? SketchPngBase64 { get; set; } = null;
    }
}