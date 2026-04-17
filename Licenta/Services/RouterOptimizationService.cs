using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Serviciu ultra-avansat de optimizare a poziției routerului.
/// Folosește Log-Distance Path Loss + Raycasting prin pereți desenați + obstacole.
/// Parametrii fizici se adaptează automat la banda de frecvență (2.4 / 5 / 6 GHz).
/// </summary>
public class RouterOptimizationService
{
    // === PARAMETRI FIZICI WiFi — adaptabili per bandă ===
    private double _frequencyGHz    = 2.4;
    private double _txPowerDbm      = 20.0;
    private double _pathLossExp     = 3.0;   // 2.4 GHz indoor
    private double _wallPenaltyDbm  = 10.0;  // 2.4 GHz: penetrare bună
    private string _bandLabel       = "2.4 GHz";

    private const double REFERENCE_DISTANCE_M = 1.0;
    private const double SHADOWING_STD_DEV    = 8.0;

    // === PARAMETRI ALGORITM ===
    private const int    GRID_STEP             = 10;
    private const double EDGE_PENALTY_DISTANCE = 50.0;
    private const double CORNER_PENALTY_RADIUS = 80.0;

    // === PONDERI MULTI-CRITERIA ===
    private const double WEIGHT_MIN_SIGNAL  = 0.5;
    private const double WEIGHT_UNIFORMITY  = 0.3;
    private const double WEIGHT_POSITION    = 0.2;

    // Indică dacă simularea trebuie să folosească WiFi 7 MLO (Multi-Link Operation)
    // În modul MLO, se simulează acoperirea combinată 2.4 + 5 + 6 GHz simultan.
    private bool _isMlo = false;

    // Raza maximă realistă de acoperire a routerului (în metri).
    // Dincolo de această distanță semnalul devine neglijabil — se aplică cutoff hard.
    // Valori calibrate pe specificațiile tipice 802.11 indoor:
    //   2.4 GHz → 28 m, 5 GHz → 18 m, 6 GHz → 10 m, WiFi 7 MLO → 30 m
    private double _maxRangeM = 28.0;

    /// <summary>
    /// Configurează parametrii fizici pe baza canalului WiFi.
    /// Parametrii de atenuare (wall penalty, path loss exponent) sunt calibrați pe
    /// măsurători reale IEEE 802.11 pentru pereți interiori rezidenţiali (gips-carton/cărămidă),
    /// NU beton armat (valorile vechi de 15–20 dBm/perete erau pentru beton).
    ///
    /// Generații WiFi suportate:
    ///   WiFi 5 (802.11ac)  → 5 GHz, canale 32–177
    ///   WiFi 6 (802.11ax)  → 2.4 / 5 GHz, canale 1–177
    ///   WiFi 6E (802.11ax) → 6 GHz, canale 183+
    ///   WiFi 7 (802.11be)  → MLO pe 2.4 + 5 + 6 GHz simultan (activat via ConfigureWifi7)
    ///   WiFi 8 (802.11bn)  → draft, ~2028, nesuportat încă
    /// </summary>
    public string Configure(int channel)
    {
        _isMlo = false; // reset MLO implicit

        if (channel >= 32 && channel <= 177)
        {
            // 5 GHz — WiFi 5/6/7
            // Path loss exponent 3.3 = rezidential tipic (IEEE 802.11-2020 tabel B.3)
            // Wall penalty 8 dBm = perete interior gips-carton/cărămidă @ 5 GHz
            // (beton armat ar fi 15–20 dBm, dar apartamentele au pereți interiori ușori)
            _frequencyGHz   = 5.0;
            _txPowerDbm     = 23.0;
            _pathLossExp    = 3.3;
            _wallPenaltyDbm = 8.0;
            _maxRangeM      = 18.0;
            _bandLabel      = "5 GHz (WiFi 6)";
        }
        else if (channel > 177)
        {
            // 6 GHz — WiFi 6E/7
            // Path loss exponent 3.7, wall penalty 12 dBm (undă scurtă, mai mult blocată)
            _frequencyGHz   = 6.0;
            _txPowerDbm     = 23.0;
            _pathLossExp    = 3.7;
            _wallPenaltyDbm = 12.0;
            _maxRangeM      = 10.0;
            _bandLabel      = "6 GHz (WiFi 6E/7)";
        }
        else
        {
            // 2.4 GHz — WiFi 4/5/6/7
            // Path loss exponent 2.8, wall penalty 5 dBm (penetrare bună la frecvență joasă)
            _frequencyGHz   = 2.4;
            _txPowerDbm     = 20.0;
            _pathLossExp    = 2.8;
            _wallPenaltyDbm = 5.0;
            _maxRangeM      = 28.0;
            _bandLabel      = "2.4 GHz (WiFi 6)";
        }
        return _bandLabel;
    }

    /// <summary>
    /// Activează modul WiFi 7 MLO (Multi-Link Operation).
    /// IEEE 802.11be: routerul transmite simultan pe 2.4 + 5 + 6 GHz;
    /// clientul primește semnalul cel mai bun din oricare bandă în fiecare punct.
    /// GenerateOptimizedHeatmap va calcula max(signal_2.4, signal_5, signal_6) per pixel.
    /// </summary>
    public void ConfigureWifi7Mlo()
    {
        // Setăm parametrii 5 GHz ca bandă primară (cea mai folosită de WiFi 7)
        _frequencyGHz   = 5.0;
        _txPowerDbm     = 26.0; // WiFi 7 permite până la 26 dBm (4×4 MIMO, 320 MHz)
        _pathLossExp    = 3.3;
        _wallPenaltyDbm = 8.0;
        _isMlo          = true;
        _maxRangeM      = 30.0;
        _bandLabel      = "WiFi 7 MLO (2.4+5+6 GHz)";
    }

    public string BandLabel => _bandLabel;
    public bool   IsMlo     => _isMlo;

    /// <summary>
    /// Calibrează puterea de emisie (TX power) a routerului pe baza măsurătorilor reale.
    /// Inversează modelul Log-Distance Path Loss: TX = SignalMasurat + PathLoss + Shadowing.
    /// Astfel, o rețea cu -54 dBm va simula mai bine decât una cu -68 dBm, indiferent de bandă.
    /// </summary>
    public double CalibrateTxPower(
        Point routerPos,
        List<SurveyPoint> surveys,
        string? targetSSID,
        double pixelsPerMeter)
    {
        if (string.IsNullOrEmpty(targetSSID) || surveys == null || surveys.Count == 0)
            return _txPowerDbm; // rămâne valoarea default

        double wavelength = 3e8 / (_frequencyGHz * 1e9);
        double pl_d0      = 20 * Math.Log10(4 * Math.PI * REFERENCE_DISTANCE_M / wavelength);

        double txSum   = 0;
        int    txCount = 0;

        foreach (var sp in surveys)
        {
            // Căutăm semnalul măsurat pentru rețeaua selectată în acest punct
            int? measured = sp.DetectedNetworks
                .FirstOrDefault(n => n.SSID == targetSSID)
                ?.SignalStrength;

            if (measured == null) continue;

            double distPx = Distance(routerPos, new Point(sp.X, sp.Y));
            if (distPx < 5.0) continue; // prea aproape, nesigur

            double distM = Math.Max(distPx / pixelsPerMeter, REFERENCE_DISTANCE_M);
            double pathLoss = pl_d0 + 10 * _pathLossExp * Math.Log10(distM / REFERENCE_DISTANCE_M);

            // TX estimat din inversa modelului: TX = S_masutat + PL + shadowing
            double estimatedTx = measured.Value + pathLoss + (SHADOWING_STD_DEV / 2.0);
            txSum   += estimatedTx;
            txCount++;
        }

        if (txCount > 0)
        {
            double calibrated = txSum / txCount;
            // Clamp în intervalul fizic realist pentru routere WiFi: 10–33 dBm
            _txPowerDbm = Math.Clamp(calibrated, 10.0, 33.0);
        }

        return _txPowerDbm;
    }

    /// <summary>
    /// Găsește poziția optimă pentru router.
    /// Dacă se furnizează targetSSID, include și consistența cu măsurătorile reale în scor —
    /// candidatul care "explică" cel mai bine semnalele măsurate va câștiga.
    /// Cu mai multe puncte măsurate dense (ex: JCn4 cu 18 pct), poziția va fi mai precisă.
    /// </summary>
    public Point FindOptimalPosition(
        List<MapElement> obstacles,
        List<SurveyPoint> surveyPoints,
        StrokeCollection drawnWalls,
        double mapWidth,
        double mapHeight,
        double pixelsPerMeter,
        string? targetSSID = null)
    {
        if (surveyPoints == null || surveyPoints.Count == 0)
            return new Point(mapWidth / 2, mapHeight / 2);

        var wallSegments = ExtractWallSegments(drawnWalls);

        // Extragem măsurătorile reale pentru rețeaua țintă (dacă există SSID)
        // Fiecare element: (poziție punct, semnal real măsurat în dBm)
        var realMeasurements = string.IsNullOrEmpty(targetSSID)
            ? null
            : surveyPoints
                .Select(sp => (
                    pos:    new Point(sp.X, sp.Y),
                    signal: sp.DetectedNetworks
                                .FirstOrDefault(n => n.SSID == targetSSID)
                                ?.SignalStrength))
                .Where(x => x.signal.HasValue)
                .Select(x => (x.pos, signal: x.signal!.Value))
                .ToList();

        bool hasMeasurements = realMeasurements?.Count > 0;

        var candidates     = GenerateBoundedCandidateGrid(surveyPoints, mapWidth, mapHeight);
        var evaluationPoints = GenerateWeightedEvaluationPoints(surveyPoints, mapWidth, mapHeight);

        Point  bestPosition = new Point(mapWidth / 2, mapHeight / 2);
        double bestScore    = double.MinValue;

        foreach (var candidate in candidates)
        {
            // ── 1. Semnal minim și uniformitate (din modelul fizic) ──
            double minSignal   = double.MaxValue;
            double totalSignal = 0;
            double totalWeight = 0;
            var    signals     = new List<double>();

            foreach (var (evalPoint, weight) in evaluationPoints)
            {
                double signal = CalculateSignalAtPoint(
                    candidate, evalPoint, obstacles, wallSegments, pixelsPerMeter);

                if (signal < minSignal) minSignal = signal;

                totalSignal += signal * weight;
                totalWeight += weight;
                signals.Add(signal);
            }

            double avgSignal      = totalSignal / totalWeight;
            double variance       = signals.Sum(s => Math.Pow(s - avgSignal, 2)) / signals.Count;
            double uniformityScore = 100.0 / (1.0 + variance / 100.0);
            double positionScore   = CalculatePositionScore(candidate, surveyPoints, mapWidth, mapHeight);
            double normalizedMin   = Math.Clamp((minSignal + 90.0) / 60.0, 0.0, 1.0);

            // ── 2. Consistență cu măsurătorile reale (dacă avem SSID) ──
            // Candidatul din care modelul fizic prezice cel mai bine semnalele reale câștigă.
            // Rețelele cu mai multe puncte dense (JCn4 cu 18 pct) vor genera o poziție mai precisă.
            double measureFitScore = 100.0;
            if (hasMeasurements)
            {
                double totalResidual = 0;
                foreach (var (pos, measured) in realMeasurements!)
                {
                    double simulated = CalculateSignalAtPoint(
                        candidate, pos, obstacles, wallSegments, pixelsPerMeter);
                    // Rezidual absolut: cât de bine explică candidatul semnalul real
                    totalResidual += Math.Abs(measured - simulated);
                }
                double avgResidual = totalResidual / realMeasurements!.Count;
                // Rezidual mic → scor mare (candidat bun)
                measureFitScore = 100.0 / (1.0 + avgResidual / 12.0);
            }

            // ── 3. Scor compus ──
            // Cu măsurători reale: mai multă greutate pe consistența cu datele reale
            double wSignal  = hasMeasurements ? 0.30 : 0.50;
            double wUniform = hasMeasurements ? 0.25 : 0.35;
            double wPos     = 0.15;
            double wFit     = hasMeasurements ? 0.30 : 0.00;

            double compositeScore =
                normalizedMin              * wSignal  +
                (uniformityScore / 100.0)  * wUniform +
                (positionScore   / 100.0)  * wPos     +
                (measureFitScore / 100.0)  * wFit;

            if (compositeScore > bestScore)
            {
                bestScore    = compositeScore;
                bestPosition = candidate;
            }
        }

        return bestPosition;
    }

    /// <summary>
    /// Calculează semnalul cu Log-Distance Path Loss + Raycasting complet.
    /// </summary>
    private double CalculateSignalAtPoint(
        Point routerPos,
        Point targetPos,
        List<MapElement> obstacles,
        List<(double X1, double Y1, double X2, double Y2)> wallSegments,
        double pixelsPerMeter)
    {
        double distPx = Distance(routerPos, targetPos);
        if (distPx < 1.0) return _txPowerDbm;

        double distMeters = distPx / pixelsPerMeter;
        if (distMeters < REFERENCE_DISTANCE_M)
            distMeters = REFERENCE_DISTANCE_M;

        // 1. Free Space Path Loss (depinde de frecvența benzii)
        double wavelength = 3e8 / (_frequencyGHz * 1e9);
        double pl_d0 = 20 * Math.Log10(4 * Math.PI * REFERENCE_DISTANCE_M / wavelength);

        // 2. Log-Distance Path Loss (exponentul depinde de bandă)
        double pathLoss = pl_d0 + 10 * _pathLossExp * Math.Log10(distMeters / REFERENCE_DISTANCE_M);

        // 3. Obstacole (stampile) + Ziduri desenate (raycasting complet)
        double obstacleLoss = CalculateObstaclePenalty(routerPos, targetPos, obstacles);
        double wallLoss = CalculateWallPenalty(routerPos, targetPos, wallSegments);

        // 4. Shadowing
        double shadowingPenalty = SHADOWING_STD_DEV / 2;

        return _txPowerDbm - pathLoss - obstacleLoss - wallLoss - shadowingPenalty;
    }

    /// <summary>
    /// ✅ NOU: Raycasting prin pereții desenați (identic cu HeatmapService).
    /// </summary>
    private double CalculateWallPenalty(
        Point from,
        Point to,
        List<(double X1, double Y1, double X2, double Y2)> wallSegments)
    {
        if (wallSegments == null || wallSegments.Count == 0)
            return 0;

        double totalPenalty = 0;

        foreach (var wall in wallSegments)
        {
            if (LinesIntersect(
                from.X, from.Y, to.X, to.Y,
                wall.X1, wall.Y1, wall.X2, wall.Y2))
            {
                totalPenalty += _wallPenaltyDbm;
            }
        }

        return totalPenalty;
    }

    /// <summary>
    /// Raycasting prin obstacole (stampile: frigider, microunde, etc.).
    /// </summary>
    private double CalculateObstaclePenalty(Point from, Point to, List<MapElement> obstacles)
    {
        if (obstacles == null || obstacles.Count == 0) return 0;

        double totalPenalty = 0;
        const double obstacleRadius = 20.0;

        foreach (var obs in obstacles)
        {
            double cx = obs.X + obstacleRadius;
            double cy = obs.Y + obstacleRadius;

            if (LineIntersectsCircle(from.X, from.Y, to.X, to.Y, cx, cy, obstacleRadius))
            {
                double distToObstacle = DistanceToLineSegment(
                    new Point(cx, cy), from, to);

                double attenuationFactor = 1.0 - (distToObstacle / obstacleRadius);
                totalPenalty += obs.SignalPenalty * Math.Max(0.5, attenuationFactor);
            }
        }

        return totalPenalty;
    }

    /// <summary>
    /// Scor de poziționare (preferință pentru centru, evită margini/colțuri).
    /// </summary>
    private double CalculatePositionScore(
        Point candidate,
        List<SurveyPoint> surveyPoints,
        double mapWidth,
        double mapHeight)
    {
        double score = 100.0;

        double minX = surveyPoints.Min(p => p.X);
        double maxX = surveyPoints.Max(p => p.X);
        double minY = surveyPoints.Min(p => p.Y);
        double maxY = surveyPoints.Max(p => p.Y);

        double centerX = (minX + maxX) / 2;
        double centerY = (minY + maxY) / 2;
        double radiusX = (maxX - minX) / 2;
        double radiusY = (maxY - minY) / 2;

        // 1. Penalizare ușoară pentru distanța de la centrul zonei de măsurătoare
        //    (redusă față de înainte — semnalul contează mai mult decât geometria)
        double distFromCenter    = Distance(candidate, new Point(centerX, centerY));
        double maxDistFromCenter = Math.Sqrt(radiusX * radiusX + radiusY * radiusY);
        double centerPenalty     = maxDistFromCenter > 0
            ? (distFromCenter / maxDistFromCenter) * 15   // era 30
            : 0;

        // 2. Penalizare margini (redusă)
        double distToLeft   = candidate.X - minX;
        double distToRight  = maxX - candidate.X;
        double distToTop    = candidate.Y - minY;
        double distToBottom = maxY - candidate.Y;

        double minDistToEdge = Math.Min(Math.Min(distToLeft, distToRight),
                                        Math.Min(distToTop, distToBottom));

        double edgePenalty = 0;
        if (minDistToEdge < EDGE_PENALTY_DISTANCE)
            edgePenalty = (1.0 - minDistToEdge / EDGE_PENALTY_DISTANCE) * 20; // era 40

        // 3. Penalizare colțuri (redusă — semnalul din colțuri poate fi bun prin reflexii)
        bool isNearCorner =
            (distToLeft  < CORNER_PENALTY_RADIUS && distToTop    < CORNER_PENALTY_RADIUS) ||
            (distToRight < CORNER_PENALTY_RADIUS && distToTop    < CORNER_PENALTY_RADIUS) ||
            (distToLeft  < CORNER_PENALTY_RADIUS && distToBottom < CORNER_PENALTY_RADIUS) ||
            (distToRight < CORNER_PENALTY_RADIUS && distToBottom < CORNER_PENALTY_RADIUS);

        double cornerPenalty = isNearCorner ? 25 : 0; // era 50

        return Math.Max(0, score - centerPenalty - edgePenalty - cornerPenalty);
    }

    private List<Point> GenerateBoundedCandidateGrid(
        List<SurveyPoint> surveyPoints,
        double mapWidth,
        double mapHeight)
    {
        var grid = new List<Point>();

        if (surveyPoints.Count == 0)
        {
            for (double x = mapWidth * 0.2; x < mapWidth * 0.8; x += GRID_STEP)
                for (double y = mapHeight * 0.2; y < mapHeight * 0.8; y += GRID_STEP)
                    grid.Add(new Point(x, y));
            return grid;
        }

        double minX = surveyPoints.Min(p => p.X);
        double maxX = surveyPoints.Max(p => p.X);
        double minY = surveyPoints.Min(p => p.Y);
        double maxY = surveyPoints.Max(p => p.Y);

        const double margin = 30;
        minX += margin; maxX -= margin;
        minY += margin; maxY -= margin;

        if (maxX <= minX || maxY <= minY)
            return new List<Point> { new Point(mapWidth / 2, mapHeight / 2) };

        for (double x = minX; x <= maxX; x += GRID_STEP)
            for (double y = minY; y <= maxY; y += GRID_STEP)
                grid.Add(new Point(x, y));

        return grid;
    }

    private List<(Point point, double weight)> GenerateWeightedEvaluationPoints(
        List<SurveyPoint> surveyPoints,
        double mapWidth,
        double mapHeight)
    {
        var points = new List<(Point, double)>();

        int evalStep = GRID_STEP * 3;
        double minX = surveyPoints.Min(p => p.X);
        double maxX = surveyPoints.Max(p => p.X);
        double minY = surveyPoints.Min(p => p.Y);
        double maxY = surveyPoints.Max(p => p.Y);

        for (double x = minX; x <= maxX; x += evalStep)
            for (double y = minY; y <= maxY; y += evalStep)
                points.Add((new Point(x, y), 1.0));

        foreach (var sp in surveyPoints)
            points.Add((new Point(sp.X, sp.Y), 3.0));

        return points;
    }

    // === HELPER METHODS (Geometry) ===

    /// <summary>
    /// ✅ Intersecție linie-linie (identic cu HeatmapService).
    /// </summary>
    private bool LinesIntersect(
        double x1, double y1, double x2, double y2,
        double x3, double y3, double x4, double y4)
    {
        double d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(d) < 1e-10) return false;

        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d;
        double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / d;

        return t > 0 && t < 1 && u > 0 && u < 1;
    }

    /// <summary>
    /// ✅ Extrage segmentele de zid din InkCanvas (identic cu HeatmapService).
    /// </summary>
    private List<(double X1, double Y1, double X2, double Y2)> ExtractWallSegments(
        StrokeCollection strokes)
    {
        var segments = new List<(double, double, double, double)>();
        if (strokes == null) return segments;

        foreach (var stroke in strokes)
        {
            var points = stroke.StylusPoints;
            for (int i = 0; i < points.Count - 1; i++)
            {
                segments.Add((
                    points[i].X,
                    points[i].Y,
                    points[i + 1].X,
                    points[i + 1].Y));
            }
        }

        return segments;
    }

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
    /// Generează direct un BitmapSource cu heatmap-ul de semnal simulat din poziția optimă.
    /// Metodă directă O(W×H) — path loss + raycasting per pixel.
    /// Dacă _isMlo = true (WiFi 7 MLO), calculează max(2.4 GHz, 5 GHz, 6 GHz) per pixel,
    /// simulând faptul că clientul WiFi 7 alege automat banda cu cel mai bun semnal.
    /// </summary>
    public BitmapSource GenerateOptimizedHeatmap(
        Point routerPosition,
        List<MapElement> obstacles,
        StrokeCollection drawnWalls,
        int width,
        int height,
        double pixelsPerMeter)
    {
        if (width <= 0 || height <= 0) return null!;

        var wallSegments = ExtractWallSegments(drawnWalls);
        const int step = 2;

        int    stride = width * 4;
        byte[] pixels = new byte[height * stride];

        double ppm = pixelsPerMeter > 1.0 ? pixelsPerMeter : 50.0;

        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                // ── Cutoff hard bazat pe distanță reală (pixeli → metri) ──
                double distPx = Distance(routerPosition, new Point(x, y));
                double distM  = distPx / ppm;

                if (_maxRangeM > 0 && distM >= _maxRangeM)
                    continue; // pixel complet transparent, fără calcul de semnal

                double signal;

                if (_isMlo)
                {
                    // WiFi 7 MLO: clientul alege banda cu cel mai bun semnal per-cadru
                    // Simulăm toate cele 3 benzi și luăm maximul
                    double s24 = CalcSignalForBand(routerPosition, new Point(x, y),
                        obstacles, wallSegments, ppm, 2.4, 2.8, 5.0, 20.0);
                    double s5  = CalcSignalForBand(routerPosition, new Point(x, y),
                        obstacles, wallSegments, ppm, 5.0, 3.3, 8.0, 26.0);
                    double s6  = CalcSignalForBand(routerPosition, new Point(x, y),
                        obstacles, wallSegments, ppm, 6.0, 3.7, 12.0, 26.0);
                    signal = Math.Max(s24, Math.Max(s5, s6));
                }
                else
                {
                    signal = CalculateSignalAtPoint(
                        routerPosition, new Point(x, y),
                        obstacles, wallSegments, ppm);
                }

                Color color = GetHeatmapColor(signal);

                // ── Fade combinat distanță + semnal (quadratic pentru trecere naturală) ──
                // distFade: începe la 60% din raza max, ajunge la 1.0 la limita razei
                double distFade = 0.0;
                if (_maxRangeM > 0 && distM > _maxRangeM * 0.6)
                    distFade = Math.Min(1.0, (distM - _maxRangeM * 0.6) / (_maxRangeM * 0.4));

                // signalFade: începe la -62 dBm (nu -80), dispare complet la -90 dBm
                double signalFade = signal < -62.0
                    ? Math.Min(1.0, (-62.0 - signal) / 28.0)
                    : 0.0;

                double fade  = Math.Max(distFade, signalFade);
                byte   alpha = (byte)(210 * (1.0 - fade * fade)); // quadratic = margine naturală

                for (int dy = 0; dy < step && y + dy < height; dy++)
                    for (int dx = 0; dx < step && x + dx < width; dx++)
                    {
                        int idx = (y + dy) * stride + (x + dx) * 4;
                        pixels[idx]     = color.B;
                        pixels[idx + 1] = color.G;
                        pixels[idx + 2] = color.R;
                        pixels[idx + 3] = alpha;
                    }
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Paletă culori identică cu HeatmapService: Verde → Galben → Portocaliu → Roșu.
    /// </summary>
    /// <summary>
    /// Calcul semnal pentru o bandă specificată explicit — folosit în modul MLO.
    /// </summary>
    private double CalcSignalForBand(
        Point routerPos, Point targetPos,
        List<MapElement> obstacles,
        List<(double X1, double Y1, double X2, double Y2)> wallSegments,
        double pixelsPerMeter,
        double freqGHz, double pathLossExp, double wallPenaltyDbm, double txPowerDbm)
    {
        double distPx = Distance(routerPos, targetPos);
        if (distPx < 1.0) return txPowerDbm;

        double distM = Math.Max(distPx / pixelsPerMeter, REFERENCE_DISTANCE_M);
        double wavelength = 3e8 / (freqGHz * 1e9);
        double pl_d0 = 20 * Math.Log10(4 * Math.PI * REFERENCE_DISTANCE_M / wavelength);
        double pathLoss = pl_d0 + 10 * pathLossExp * Math.Log10(distM / REFERENCE_DISTANCE_M);

        double wallLoss = 0;
        if (wallSegments != null)
            foreach (var wall in wallSegments)
                if (LinesIntersect(routerPos.X, routerPos.Y, targetPos.X, targetPos.Y,
                                   wall.X1, wall.Y1, wall.X2, wall.Y2))
                    wallLoss += wallPenaltyDbm;

        double obstacleLoss = CalculateObstaclePenalty(routerPos, targetPos, obstacles);
        return txPowerDbm - pathLoss - wallLoss - obstacleLoss - (SHADOWING_STD_DEV / 2.0);
    }

    private static Color GetHeatmapColor(double signal)
    {
        if (signal >= -40) return Color.FromRgb(0, 200, 0);
        if (signal <= -90) return Color.FromRgb(255, 0, 0);

        if (signal >= -55)
        {
            double t = (signal - (-55.0)) / 15.0;
            return Color.FromRgb((byte)(173 - (int)(173 * t)), (byte)(255 - (int)(55 * t)), 0);
        }
        if (signal >= -70)
        {
            double t = (signal - (-70.0)) / 15.0;
            return Color.FromRgb((byte)(255 - (int)(82 * t)), 255, 0);
        }
        if (signal >= -80)
        {
            double t = (signal - (-80.0)) / 10.0;
            return Color.FromRgb(255, (byte)(165 + (int)(90 * t)), 0);
        }

        double t2 = (signal - (-90.0)) / 10.0;
        return Color.FromRgb(255, (byte)(165 * t2), 0);
    }

    private double Distance(Point a, Point b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private double DistanceToLineSegment(Point point, Point lineStart, Point lineEnd)
    {
        double dx = lineEnd.X - lineStart.X;
        double dy = lineEnd.Y - lineStart.Y;
        double lenSq = dx * dx + dy * dy;

        if (lenSq == 0)
            return Distance(point, lineStart);

        double t = Math.Max(0, Math.Min(1,
            ((point.X - lineStart.X) * dx + (point.Y - lineStart.Y) * dy) / lenSq));

        Point closest = new Point(
            lineStart.X + t * dx,
            lineStart.Y + t * dy);

        return Distance(point, closest);
    }

    // ══════════════════════════════════════════════════════════
    // STATISTICI ACOPERIRE
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Calculează statistici de acoperire pentru heatmap-ul OPTIMIZAT (model fizic).
    /// Eșantionează o grilă de puncte și aplică modelul Log-Distance Path Loss.
    /// </summary>
    public CoverageStats CalculateOptimizedStats(
        Point routerPosition,
        List<MapElement> obstacles,
        StrokeCollection drawnWalls,
        int width, int height,
        double pixelsPerMeter)
    {
        var wallSegments = ExtractWallSegments(drawnWalls);
        double ppm = pixelsPerMeter > 1.0 ? pixelsPerMeter : 50.0;
        const int step = 8;

        int total = 0, excellent = 0, good = 0, fair = 0, poor = 0, dead = 0;
        double signalSum = 0, signalSumSq = 0;
        double wallCountSum = 0;

        for (int y = 0; y < height; y += step)
        for (int x = 0; x < width; x += step)
        {
            double distM = Distance(routerPosition, new Point(x, y)) / ppm;
            if (_maxRangeM > 0 && distM >= _maxRangeM) continue;

            double signal = _isMlo
                ? Math.Max(
                    CalcSignalForBand(routerPosition, new Point(x, y), obstacles, wallSegments, ppm, 2.4, 2.8, 5.0, 20.0),
                    Math.Max(
                        CalcSignalForBand(routerPosition, new Point(x, y), obstacles, wallSegments, ppm, 5.0, 3.3, 8.0, 26.0),
                        CalcSignalForBand(routerPosition, new Point(x, y), obstacles, wallSegments, ppm, 6.0, 3.7, 12.0, 26.0)))
                : CalculateSignalAtPoint(routerPosition, new Point(x, y), obstacles, wallSegments, ppm);

            // Count wall crossings for this point
            double wallAtten = GetObstacleAttenuationStatic(routerPosition.X, routerPosition.Y, x, y, obstacles, wallSegments);
            double wallCount = wallAtten / Math.Max(1.0, _wallPenaltyDbm);

            total++;
            signalSum   += signal;
            signalSumSq += signal * signal;
            wallCountSum += wallCount;

            if      (signal > -55) excellent++;
            else if (signal > -70) good++;
            else if (signal > -80) fair++;
            else if (signal > -90) poor++;
            else                   dead++;
        }

        if (total == 0) return new CoverageStats(0, 0, 0, 0, 0, -100, 0, 0, 0);

        double avg     = signalSum / total;
        double variance = signalSumSq / total - avg * avg;
        double stdDev  = Math.Sqrt(Math.Max(0, variance));
        // Uniformity: 100% means 0 std dev (perfect), drops as variance increases
        double uniformity = Math.Max(0, 100.0 - stdDev * 3.0);

        return new CoverageStats(
            ExcellentPercent: excellent * 100.0 / total,
            GoodPercent:      good      * 100.0 / total,
            FairPercent:      fair      * 100.0 / total,
            PoorPercent:      poor      * 100.0 / total,
            DeadZonePercent:  dead      * 100.0 / total,
            AverageSignal:    avg,
            UniformityScore:  uniformity,
            AvgWallCount:     wallCountSum / total,
            TotalSamples:     total);
    }

    /// <summary>
    /// Calculează statistici de acoperire pentru heatmap-ul REAL (IDW din survey points).
    /// Folosit pentru comparația "înainte" de optimizare.
    /// </summary>
    public static CoverageStats CalculateRealStats(
        List<HeatmapPoint> points,
        List<MapElement> obstacles,
        StrokeCollection drawnWalls,
        int width, int height,
        double pixelsPerMeter)
    {
        if (points == null || points.Count == 0)
            return new CoverageStats(0, 0, 0, 0, 0, -100, 0, 0, 0);

        const double idwPower = 3.0;
        double ppm = pixelsPerMeter > 1.0 ? pixelsPerMeter : 50.0;
        const int step = 8;

        var wallSegs = ExtractWallSegmentsStatic(drawnWalls);

        int total = 0, excellent = 0, good = 0, fair = 0, poor = 0, dead = 0;
        double signalSum = 0, signalSumSq = 0;

        for (int y = 0; y < height; y += step)
        for (int x = 0; x < width; x += step)
        {
            double numerator = 0, denominator = 0;
            foreach (var p in points)
            {
                double distPx = Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2));
                if (distPx < 1.0) distPx = 1.0;
                double wallPenalty = GetObstacleAttenuationStatic(x, y, p.X, p.Y, obstacles, wallSegs);
                double airLoss = (distPx / ppm) * 2.5;
                double effectiveSignal = p.SignalStrength - wallPenalty - airLoss;
                double weight = 1.0 / Math.Pow(distPx, idwPower);
                numerator  += weight * effectiveSignal;
                denominator += weight;
            }
            double signal = denominator > 0 ? numerator / denominator : -100;
            if (signal < -95) continue;

            total++;
            signalSum   += signal;
            signalSumSq += signal * signal;

            if      (signal > -55) excellent++;
            else if (signal > -70) good++;
            else if (signal > -80) fair++;
            else if (signal > -90) poor++;
            else                   dead++;
        }

        if (total == 0) return new CoverageStats(0, 0, 0, 0, 0, -100, 0, 0, 0);

        double avg      = signalSum / total;
        double variance = signalSumSq / total - avg * avg;
        double stdDev   = Math.Sqrt(Math.Max(0, variance));
        double uniformity = Math.Max(0, 100.0 - stdDev * 3.0);

        return new CoverageStats(
            ExcellentPercent: excellent * 100.0 / total,
            GoodPercent:      good      * 100.0 / total,
            FairPercent:      fair      * 100.0 / total,
            PoorPercent:      poor      * 100.0 / total,
            DeadZonePercent:  dead      * 100.0 / total,
            AverageSignal:    avg,
            UniformityScore:  uniformity,
            AvgWallCount:     0,   // real stats nu au pozitie fixa de router
            TotalSamples:     total);
    }

    private static List<(double X1, double Y1, double X2, double Y2)> ExtractWallSegmentsStatic(
        StrokeCollection strokes)
    {
        var list = new List<(double, double, double, double)>();
        if (strokes == null) return list;
        foreach (var stroke in strokes)
        {
            var pts = stroke.StylusPoints;
            for (int i = 0; i < pts.Count - 1; i++)
                list.Add((pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y));
        }
        return list;
    }

    private static double GetObstacleAttenuationStatic(
        double x1, double y1, double x2, double y2,
        List<MapElement> obstacles,
        List<(double X1, double Y1, double X2, double Y2)> wallSegments)
    {
        double total = 0;
        const double r = 20.0;
        if (obstacles != null)
            foreach (var obs in obstacles)
                if (LineIntersectsCircleStatic(x1, y1, x2, y2, obs.X + r, obs.Y + r, r))
                    total += obs.SignalPenalty;
        if (wallSegments != null)
            foreach (var w in wallSegments)
                if (LinesIntersectStatic(x1, y1, x2, y2, w.X1, w.Y1, w.X2, w.Y2))
                    total += 10.0;
        return total;
    }

    private static bool LinesIntersectStatic(
        double x1, double y1, double x2, double y2,
        double x3, double y3, double x4, double y4)
    {
        double d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(d) < 1e-10) return false;
        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d;
        double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / d;
        return t > 0 && t < 1 && u > 0 && u < 1;
    }

    private static bool LineIntersectsCircleStatic(
        double x1, double y1, double x2, double y2,
        double cx, double cy, double r)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double lenSq = dx * dx + dy * dy;
        if (lenSq == 0) return (cx-x1)*(cx-x1)+(cy-y1)*(cy-y1) <= r*r;
        double t = Math.Max(0, Math.Min(1, ((cx-x1)*dx+(cy-y1)*dy)/lenSq));
        return (cx-(x1+t*dx))*(cx-(x1+t*dx))+(cy-(y1+t*dy))*(cy-(y1+t*dy)) <= r*r;
    }
}