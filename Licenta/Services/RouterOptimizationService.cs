using System.Windows;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Serviciu de optimizare a poziției routerului folosind algoritmul Min-Max.
///
/// Formula matematică implementată:
///     arg(x,y) max( min_i( Signal_i(x,y) ) )
///
/// Traducere: Caută poziția (x,y) care MAXIMIZEAZĂ semnalul în cel mai
/// DEZAVANTAJAT punct al camerei — asigurând acoperire uniformă.
/// </summary>
public class RouterOptimizationService
{
    // Pasul grilei de scanare (în pixeli). Valoare mai mică = mai precis dar mai lent.
    private const int GRID_STEP = 15;

    // Atenuare în aer (dB per metru) — model simplificat path loss interior
    private const double PATH_LOSS_DB_PER_METER = 3.5;

    // Puterea de emisie simulată a routerului (dBm)
    private const double ROUTER_TX_POWER_DBM = 20.0;

    /// <summary>
    /// Găsește poziția optimă pentru router pe planul casei.
    /// </summary>
    /// <param name="obstacles">Lista de obstacole (pereți, frigidere, etc.)</param>
    /// <param name="surveyPoints">Punctele de măsurătoare WiFi existente</param>
    /// <param name="mapWidth">Lățimea planului în pixeli</param>
    /// <param name="mapHeight">Înălțimea planului în pixeli</param>
    /// <param name="pixelsPerMeter">Scara de calibrare</param>
    /// <returns>Coordonatele optime pentru router</returns>
    public Point FindOptimalPosition(
        List<MapElement> obstacles,
        List<SurveyPoint> surveyPoints,
        double mapWidth,
        double mapHeight,
        double pixelsPerMeter)
    {
        // Generăm grila de candidați (puncte posibile pentru router)
        var candidates = GenerateCandidateGrid(mapWidth, mapHeight);

        // Generăm punctele de evaluare (unde trebuie să ajungă semnalul)
        var evaluationPoints = GenerateEvaluationPoints(mapWidth, mapHeight, surveyPoints);

        Point bestPosition = new Point(mapWidth / 2, mapHeight / 2); // fallback: centrul hărții
        double bestMinSignal = double.MinValue;

        // ══════════════════════════════════════════════════
        // ALGORITMUL MIN-MAX
        // Pentru fiecare candidat de router:
        //   1. Calculăm semnalul în TOATE punctele de evaluare
        //   2. Găsim MINIMUL (cel mai slab semnal — worst case)
        //   3. Păstrăm candidatul cu cel mai mare MIN (max of mins)
        // ══════════════════════════════════════════════════
        foreach (var candidate in candidates)
        {
            double minSignalForThisPosition = double.MaxValue;

            foreach (var evalPoint in evaluationPoints)
            {
                double signal = CalculateSignalAtPoint(
                    routerPos: candidate,
                    targetPos: evalPoint,
                    obstacles: obstacles,
                    pixelsPerMeter: pixelsPerMeter);

                // Min: cel mai slab semnal din toate punctele evaluate
                if (signal < minSignalForThisPosition)
                    minSignalForThisPosition = signal;
            }

            // Max: vrem să maximizăm acest minim
            if (minSignalForThisPosition > bestMinSignal)
            {
                bestMinSignal = minSignalForThisPosition;
                bestPosition = candidate;
            }
        }

        return bestPosition;
    }

    /// <summary>
    /// Calculează puterea semnalului (dBm) de la router la un punct țintă,
    /// ținând cont de distanță (path loss) și obstacole (raycasting).
    /// </summary>
    private double CalculateSignalAtPoint(
        Point routerPos,
        Point targetPos,
        List<MapElement> obstacles,
        double pixelsPerMeter)
    {
        double distPx = Distance(routerPos, targetPos);

        // Evităm log(0) pentru distanță zero
        if (distPx < 1.0) return ROUTER_TX_POWER_DBM;

        double distMeters = distPx / pixelsPerMeter;

        // 1. Atenuare în aer (Path Loss liniar simplificat)
        double pathLoss = PATH_LOSS_DB_PER_METER * distMeters;

        // 2. Atenuare prin obstacole (Raycasting)
        double obstaclePenalty = CalculateObstaclePenalty(routerPos, targetPos, obstacles);

        // Semnal final estimat
        return ROUTER_TX_POWER_DBM - pathLoss - obstaclePenalty;
    }

    /// <summary>
    /// Calculează penalizarea totală a obstacolelor pe raza router → punct.
    /// Folosește același algoritm de intersecție linie-cerc ca HeatmapService.
    /// </summary>
    private double CalculateObstaclePenalty(Point from, Point to, List<MapElement> obstacles)
    {
        if (obstacles == null || obstacles.Count == 0) return 0;

        double total = 0;
        const double obstacleRadius = 20.0; // Raza de impact a iconițelor (40x40px → r=20)

        foreach (var obs in obstacles)
        {
            double cx = obs.X + obstacleRadius;
            double cy = obs.Y + obstacleRadius;

            if (LineIntersectsCircle(from.X, from.Y, to.X, to.Y, cx, cy, obstacleRadius))
                total += obs.SignalPenalty;
        }

        return total;
    }

    /// <summary>
    /// Intersecție linie-cerc (identic cu HeatmapService pentru consistență).
    /// </summary>
    private bool LineIntersectsCircle(
        double x1, double y1, double x2, double y2,
        double cx, double cy, double r)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lenSq = dx * dx + dy * dy;

        if (lenSq == 0)
            return (cx - x1) * (cx - x1) + (cy - y1) * (cy - y1) <= r * r;

        double t = Math.Max(0, Math.Min(1,
            ((cx - x1) * dx + (cy - y1) * dy) / lenSq));

        double closestX = x1 + t * dx;
        double closestY = y1 + t * dy;

        return (cx - closestX) * (cx - closestX) +
               (cy - closestY) * (cy - closestY) <= r * r;
    }

    /// <summary>
    /// Generează o grilă uniformă de poziții candidate pentru router.
    /// </summary>
    private List<Point> GenerateCandidateGrid(double width, double height)
    {
        var grid = new List<Point>();

        // Marginile le evităm (routerul nu se pune în zid)
        int margin = GRID_STEP * 2;

        for (int x = margin; x < (int)width - margin; x += GRID_STEP)
            for (int y = margin; y < (int)height - margin; y += GRID_STEP)
                grid.Add(new Point(x, y));

        return grid;
    }

    /// <summary>
    /// Generează punctele unde evaluăm calitatea semnalului.
    /// Dacă există puncte de survey reale, le includem — sunt mai relevante.
    /// Completăm cu o grilă de evaluare mai densă.
    /// </summary>
    private List<Point> GenerateEvaluationPoints(
        double width,
        double height,
        List<SurveyPoint> surveyPoints)
    {
        var points = new List<Point>();

        // Grilă de evaluare (mai densă decât candidații pentru precizie)
        int evalStep = GRID_STEP * 2;
        int margin = evalStep;

        for (int x = margin; x < (int)width - margin; x += evalStep)
            for (int y = margin; y < (int)height - margin; y += evalStep)
                points.Add(new Point(x, y));

        // Adăugăm punctele reale de survey (au prioritate implicită prin densitate)
        if (surveyPoints != null)
            foreach (var sp in surveyPoints)
                points.Add(new Point(sp.X, sp.Y));

        return points;
    }

    private double Distance(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}