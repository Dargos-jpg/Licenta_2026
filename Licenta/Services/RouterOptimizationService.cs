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
/// router position optimization service.
/// uses Log-Distance Path Loss + raycasting through drawn walls and obstacles.
/// physical parameters automatically adapt to the frequency band (2.4 / 5 / 6 GHz).
/// </summary>
public class RouterOptimizationService
{
    // WiFi physical parameters, adapted per band
    private double _frequencyGHz    = 2.4;
    private double _txPowerDbm      = 20.0;
    private double _pathLossExp     = 3.0;   // 2.4 GHz indoor
    private double _wallPenaltyDbm  = 10.0;  // 2.4 GHz: good penetration
    private string _bandLabel       = "2.4 GHz";

    private const double REFERENCE_DISTANCE_M = 1.0;
    private const double SHADOWING_STD_DEV    = 8.0;

    // algorithm parameters
    private const int    GRID_STEP             = 10;
    private const double EDGE_PENALTY_DISTANCE = 50.0;
    private const double CORNER_PENALTY_RADIUS = 80.0;

    // multi-criteria weights
    private const double WEIGHT_MIN_SIGNAL  = 0.5;
    private const double WEIGHT_UNIFORMITY  = 0.3;
    private const double WEIGHT_POSITION    = 0.2;

    // indicates whether the simulation should use WiFi 7 MLO (Multi-Link Operation)
    // in MLO mode, the combined 2.4 + 5 + 6 GHz coverage is simulated simultaneously.
    private bool _isMlo = false;

    // realistic maximum coverage range of the router (in meters).
    // beyond this distance the signal becomes negligible, a hard cutoff is applied.
    // values calibrated against typical indoor 802.11 specifications:
    //   2.4 GHz -> 28 m, 5 GHz -> 18 m, 6 GHz -> 10 m, WiFi 7 MLO -> 30 m
    private double _maxRangeM = 28.0;

    /// <summary>
    /// configures the physical parameters based on the WiFi channel.
    /// attenuation parameters (wall penalty, path loss exponent) are calibrated against
    /// real IEEE 802.11 measurements for residential interior walls (drywall/brick),
    /// not reinforced concrete (the old values of 15-20 dBm/wall were for concrete).
    ///
    /// supported WiFi generations:
    ///   WiFi 5 (802.11ac)  -> 5 GHz, channels 32-177
    ///   WiFi 6 (802.11ax)  -> 2.4 / 5 GHz, channels 1-177
    ///   WiFi 6E (802.11ax) -> 6 GHz, channels 183+
    ///   WiFi 7 (802.11be)  -> MLO across 2.4 + 5 + 6 GHz simultaneously (enabled via ConfigureWifi7)
    ///   WiFi 8 (802.11bn)  -> draft, ~2028, not yet supported
    /// </summary>
    public string Configure(int channel)
    {
        _isMlo = false; // reset MLO by default

        if (channel >= 32 && channel <= 177)
        {
            // 5 GHz - WiFi 5/6/7
            // path loss exponent 3.3 = typical residential (IEEE 802.11-2020 table B.3)
            // wall penalty 8 dBm = interior drywall/brick wall @ 5 GHz
            // (reinforced concrete would be 15-20 dBm, but apartments have light interior walls)
            _frequencyGHz   = 5.0;
            _txPowerDbm     = 23.0;
            _pathLossExp    = 3.3;
            _wallPenaltyDbm = 8.0;
            _maxRangeM      = 18.0;
            _bandLabel      = "5 GHz (WiFi 6)";
        }
        else if (channel > 177)
        {
            // 6 GHz - WiFi 6E/7
            // path loss exponent 3.7, wall penalty 12 dBm (short wave, more easily blocked)
            _frequencyGHz   = 6.0;
            _txPowerDbm     = 23.0;
            _pathLossExp    = 3.7;
            _wallPenaltyDbm = 12.0;
            _maxRangeM      = 10.0;
            _bandLabel      = "6 GHz (WiFi 6E/7)";
        }
        else
        {
            // 2.4 GHz - WiFi 4/5/6/7
            // path loss exponent 2.8, wall penalty 5 dBm (good penetration at low frequency)
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
    /// enables WiFi 7 MLO (Multi-Link Operation) mode.
    /// IEEE 802.11be: the router transmits simultaneously on 2.4 + 5 + 6 GHz;
    /// the client receives the best signal from any band at each point.
    /// GenerateOptimizedHeatmap will compute max(signal_2.4, signal_5, signal_6) per pixel.
    /// </summary>
    public void ConfigureWifi7Mlo()
    {
        // set the 5 GHz parameters as the primary band (most used by WiFi 7)
        _frequencyGHz   = 5.0;
        _txPowerDbm     = 26.0; // WiFi 7 allows up to 26 dBm (4x4 MIMO, 320 MHz)
        _pathLossExp    = 3.3;
        _wallPenaltyDbm = 8.0;
        _isMlo          = true;
        _maxRangeM      = 30.0;
        _bandLabel      = "WiFi 7 MLO (2.4+5+6 GHz)";
    }

    public string BandLabel => _bandLabel;
    public bool   IsMlo     => _isMlo;

    /// <summary>
    /// calibrates the router's transmit power (TX power) based on real measurements.
    /// inverts the Log-Distance Path Loss model: TX = MeasuredSignal + PathLoss + Shadowing.
    /// this way, a network at -54 dBm will simulate better than one at -68 dBm, regardless of band.
    /// </summary>
    public double CalibrateTxPower(
        Point routerPos,
        List<SurveyPoint> surveys,
        string? targetSSID,
        double pixelsPerMeter)
    {
        if (string.IsNullOrEmpty(targetSSID) || surveys == null || surveys.Count == 0)
            return _txPowerDbm; // keep the default value

        double wavelength = 3e8 / (_frequencyGHz * 1e9);
        double pl_d0      = 20 * Math.Log10(4 * Math.PI * REFERENCE_DISTANCE_M / wavelength);

        double txSum   = 0;
        int    txCount = 0;

        foreach (var sp in surveys)
        {
            // look up the measured signal for the selected network at this point
            int? measured = sp.DetectedNetworks
                .FirstOrDefault(n => n.SSID == targetSSID)
                ?.SignalStrength;

            if (measured == null) continue;

            double distPx = Distance(routerPos, new Point(sp.X, sp.Y));
            if (distPx < 5.0) continue; // too close, unreliable

            double distM = Math.Max(distPx / pixelsPerMeter, REFERENCE_DISTANCE_M);
            double pathLoss = pl_d0 + 10 * _pathLossExp * Math.Log10(distM / REFERENCE_DISTANCE_M);

            // estimated TX from the inverted model: TX = S_measured + PL + shadowing
            double estimatedTx = measured.Value + pathLoss + (SHADOWING_STD_DEV / 2.0);
            txSum   += estimatedTx;
            txCount++;
        }

        if (txCount > 0)
        {
            double calibrated = txSum / txCount;
            // clamp to a realistic physical range for WiFi routers: 10-33 dBm
            _txPowerDbm = Math.Clamp(calibrated, 10.0, 33.0);
        }

        return _txPowerDbm;
    }

    /// <summary>
    /// finds the optimal position for the router.
    /// if targetSSID is provided, the score also factors in consistency with real measurements,
    /// the candidate that best "explains" the measured signals wins.
    /// with more dense measured points (e.g. JCn4 with 18 points), the position will be more precise.
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

        // extract the real measurements for the target network (if an SSID is given)
        // each element: (point position, real measured signal in dBm)
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
            // 1. minimum signal and uniformity (from the physical model)
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

            // 2. consistency with real measurements (if an SSID is available)
            // the candidate whose physical model best predicts the real signals wins.
            // networks with more dense points (JCn4 with 18 points) yield a more precise position.
            double measureFitScore = 100.0;
            if (hasMeasurements)
            {
                double totalResidual = 0;
                foreach (var (pos, measured) in realMeasurements!)
                {
                    double simulated = CalculateSignalAtPoint(
                        candidate, pos, obstacles, wallSegments, pixelsPerMeter);
                    // absolute residual: how well the candidate explains the real signal
                    totalResidual += Math.Abs(measured - simulated);
                }
                double avgResidual = totalResidual / realMeasurements!.Count;
                // small residual -> high score (good candidate)
                measureFitScore = 100.0 / (1.0 + avgResidual / 12.0);
            }

            // 3. composite score
            // with real measurements: more weight on consistency with the real data
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
    /// computes the signal using full Log-Distance Path Loss + raycasting.
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

        // 1. free space path loss (depends on the band's frequency)
        double wavelength = 3e8 / (_frequencyGHz * 1e9);
        double pl_d0 = 20 * Math.Log10(4 * Math.PI * REFERENCE_DISTANCE_M / wavelength);

        // 2. log-distance path loss (the exponent depends on the band)
        double pathLoss = pl_d0 + 10 * _pathLossExp * Math.Log10(distMeters / REFERENCE_DISTANCE_M);

        // 3. obstacles (stamps) + drawn walls (full raycasting)
        double obstacleLoss = CalculateObstaclePenalty(routerPos, targetPos, obstacles);
        double wallLoss = CalculateWallPenalty(routerPos, targetPos, wallSegments);

        // 4. shadowing
        double shadowingPenalty = SHADOWING_STD_DEV / 2;

        return _txPowerDbm - pathLoss - obstacleLoss - wallLoss - shadowingPenalty;
    }

    /// <summary>
    /// raycasting through the drawn walls (identical to HeatmapService).
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
    /// raycasting through obstacles (stamps: fridge, microwave, etc.).
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
    /// positioning score (prefers the center, avoids edges/corners).
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

        // 1. light penalty for distance from the center of the surveyed area
        //    (reduced compared to before, signal matters more than geometry)
        double distFromCenter    = Distance(candidate, new Point(centerX, centerY));
        double maxDistFromCenter = Math.Sqrt(radiusX * radiusX + radiusY * radiusY);
        double centerPenalty     = maxDistFromCenter > 0
            ? (distFromCenter / maxDistFromCenter) * 15   // was 30
            : 0;

        // 2. edge penalty (reduced)
        double distToLeft   = candidate.X - minX;
        double distToRight  = maxX - candidate.X;
        double distToTop    = candidate.Y - minY;
        double distToBottom = maxY - candidate.Y;

        double minDistToEdge = Math.Min(Math.Min(distToLeft, distToRight),
                                        Math.Min(distToTop, distToBottom));

        double edgePenalty = 0;
        if (minDistToEdge < EDGE_PENALTY_DISTANCE)
            edgePenalty = (1.0 - minDistToEdge / EDGE_PENALTY_DISTANCE) * 20; // was 40

        // 3. corner penalty (reduced, signal in corners can be good thanks to reflections)
        bool isNearCorner =
            (distToLeft  < CORNER_PENALTY_RADIUS && distToTop    < CORNER_PENALTY_RADIUS) ||
            (distToRight < CORNER_PENALTY_RADIUS && distToTop    < CORNER_PENALTY_RADIUS) ||
            (distToLeft  < CORNER_PENALTY_RADIUS && distToBottom < CORNER_PENALTY_RADIUS) ||
            (distToRight < CORNER_PENALTY_RADIUS && distToBottom < CORNER_PENALTY_RADIUS);

        double cornerPenalty = isNearCorner ? 25 : 0; // was 50

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

    // geometry helper methods

    /// <summary>
    /// line-line intersection (identical to HeatmapService).
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
    /// extracts wall segments from the InkCanvas (identical to HeatmapService).
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
    /// directly generates a BitmapSource with the simulated signal heatmap from the optimal position.
    /// direct O(W x H) method: path loss + raycasting per pixel.
    /// if _isMlo = true (WiFi 7 MLO), computes max(2.4 GHz, 5 GHz, 6 GHz) per pixel,
    /// simulating the fact that a WiFi 7 client automatically picks the band with the best signal.
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
                // hard cutoff based on the real distance (pixels to meters)
                double distPx = Distance(routerPosition, new Point(x, y));
                double distM  = distPx / ppm;

                if (_maxRangeM > 0 && distM >= _maxRangeM)
                    continue; // fully transparent pixel, no signal calculation

                double signal;

                if (_isMlo)
                {
                    // WiFi 7 MLO: the client picks the band with the best signal per frame
                    // simulate all 3 bands and take the maximum
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

                // combined distance + signal fade (quadratic for a natural transition)
                // distFade: starts at 60% of the max range, reaches 1.0 at the range limit
                double distFade = 0.0;
                if (_maxRangeM > 0 && distM > _maxRangeM * 0.6)
                    distFade = Math.Min(1.0, (distM - _maxRangeM * 0.6) / (_maxRangeM * 0.4));

                // signalFade: starts at -62 dBm (not -80), fully disappears at -90 dBm
                double signalFade = signal < -62.0
                    ? Math.Min(1.0, (-62.0 - signal) / 28.0)
                    : 0.0;

                double fade  = Math.Max(distFade, signalFade);
                byte   alpha = (byte)(210 * (1.0 - fade * fade)); // quadratic = natural edge

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
    /// computes the signal for an explicitly specified band, used in MLO mode.
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

    // coverage statistics

    /// <summary>
    /// computes coverage statistics for the optimized heatmap (physical model).
    /// samples a grid of points and applies the Log-Distance Path Loss model.
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

            // count wall crossings for this point
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
        // uniformity: 100% means 0 std dev (perfect), drops as variance increases
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
    /// computes coverage statistics for the real heatmap (IDW from survey points).
    /// used for the "before" comparison prior to optimization.
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
            AvgWallCount:     0,   // real stats have no fixed router position
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
