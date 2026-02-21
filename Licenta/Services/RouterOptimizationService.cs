using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Ink;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

/// <summary>
/// Serviciu ultra-avansat de optimizare a poziției routerului.
/// Folosește Log-Distance Path Loss + Raycasting prin pereți desenați + obstacole.
/// </summary>
public class RouterOptimizationService
{
    // === PARAMETRI FIZICI WiFi (IEEE 802.11n/ac/ax) ===
    private const double FREQUENCY_GHZ = 2.4;
    private const double ROUTER_TX_POWER_DBM = 20.0;
    private const double PATH_LOSS_EXPONENT = 3.5;
    private const double REFERENCE_DISTANCE_M = 1.0;
    private const double SHADOWING_STD_DEV = 8.0;

    // === PARAMETRI ALGORITM ===
    private const int GRID_STEP = 10;
    private const double EDGE_PENALTY_DISTANCE = 50.0;
    private const double CORNER_PENALTY_RADIUS = 80.0;

    // === PONDERI MULTI-CRITERIA ===
    private const double WEIGHT_MIN_SIGNAL = 0.5;
    private const double WEIGHT_UNIFORMITY = 0.3;
    private const double WEIGHT_POSITION = 0.2;

    // === PENALTY PER ZID DESENAT ===
    private const double WALL_PENALTY_DBM = 10.0; // Atenuare standard perete interior

    /// <summary>
    /// Găsește poziția optimă pentru router (cu suport complet pentru ziduri desenate).
    /// </summary>
    public Point FindOptimalPosition(
        List<MapElement> obstacles,
        List<SurveyPoint> surveyPoints,
        StrokeCollection drawnWalls, // ✅ NOU: Pereții desenați
        double mapWidth,
        double mapHeight,
        double pixelsPerMeter)
    {
        if (surveyPoints == null || surveyPoints.Count == 0)
            return new Point(mapWidth / 2, mapHeight / 2);

        // ✅ Extragem segmentele de zid o singură dată (optimizare)
        var wallSegments = ExtractWallSegments(drawnWalls);

        var candidates = GenerateBoundedCandidateGrid(surveyPoints, mapWidth, mapHeight);
        var evaluationPoints = GenerateWeightedEvaluationPoints(surveyPoints, mapWidth, mapHeight);

        Point bestPosition = new Point(mapWidth / 2, mapHeight / 2);
        double bestScore = double.MinValue;

        foreach (var candidate in candidates)
        {
            double minSignal = double.MaxValue;
            double totalSignal = 0;
            int count = 0;
            var signals = new List<double>();

            foreach (var (evalPoint, weight) in evaluationPoints)
            {
                double signal = CalculateSignalAtPoint(
                    candidate, evalPoint, obstacles, wallSegments, pixelsPerMeter);

                double weightedSignal = signal * weight;

                if (weightedSignal < minSignal)
                    minSignal = weightedSignal;

                totalSignal += signal;
                signals.Add(signal);
                count++;
            }

            double avgSignal = totalSignal / count;

            // Uniformity
            double variance = signals.Sum(s => Math.Pow(s - avgSignal, 2)) / count;
            double uniformityScore = 100.0 / (1.0 + variance / 100.0);

            // Position quality
            double positionScore = CalculatePositionScore(
                candidate, surveyPoints, mapWidth, mapHeight);

            // Composite score
            double compositeScore =
                (minSignal / 100.0) * WEIGHT_MIN_SIGNAL +
                (uniformityScore / 100.0) * WEIGHT_UNIFORMITY +
                (positionScore / 100.0) * WEIGHT_POSITION;

            if (compositeScore > bestScore)
            {
                bestScore = compositeScore;
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
        if (distPx < 1.0) return ROUTER_TX_POWER_DBM;

        double distMeters = distPx / pixelsPerMeter;
        if (distMeters < REFERENCE_DISTANCE_M)
            distMeters = REFERENCE_DISTANCE_M;

        // 1. Free Space Path Loss
        double wavelength = 3e8 / (FREQUENCY_GHZ * 1e9);
        double pl_d0 = 20 * Math.Log10(4 * Math.PI * REFERENCE_DISTANCE_M / wavelength);

        // 2. Log-Distance Path Loss
        double pathLoss = pl_d0 + 10 * PATH_LOSS_EXPONENT * Math.Log10(distMeters / REFERENCE_DISTANCE_M);

        // 3. ✅ Obstacole (stampile) + Ziduri desenate (raycasting complet)
        double obstacleLoss = CalculateObstaclePenalty(routerPos, targetPos, obstacles);
        double wallLoss = CalculateWallPenalty(routerPos, targetPos, wallSegments);

        // 4. Shadowing
        double shadowingPenalty = SHADOWING_STD_DEV / 2;

        return ROUTER_TX_POWER_DBM - pathLoss - obstacleLoss - wallLoss - shadowingPenalty;
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
                totalPenalty += WALL_PENALTY_DBM;
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

        // 1. Penalty pentru distanță de la centru
        double distFromCenter = Distance(candidate, new Point(centerX, centerY));
        double maxDistFromCenter = Math.Sqrt(radiusX * radiusX + radiusY * radiusY);
        double centerPenalty = (distFromCenter / maxDistFromCenter) * 30;

        // 2. Penalty pentru proximitate marginilor
        double distToLeft = candidate.X - minX;
        double distToRight = maxX - candidate.X;
        double distToTop = candidate.Y - minY;
        double distToBottom = maxY - candidate.Y;

        double minDistToEdge = Math.Min(Math.Min(distToLeft, distToRight),
                                        Math.Min(distToTop, distToBottom));

        double edgePenalty = 0;
        if (minDistToEdge < EDGE_PENALTY_DISTANCE)
            edgePenalty = (1.0 - minDistToEdge / EDGE_PENALTY_DISTANCE) * 40;

        // 3. Penalty masiv pentru colțuri
        bool isNearCorner =
            (distToLeft < CORNER_PENALTY_RADIUS && distToTop < CORNER_PENALTY_RADIUS) ||
            (distToRight < CORNER_PENALTY_RADIUS && distToTop < CORNER_PENALTY_RADIUS) ||
            (distToLeft < CORNER_PENALTY_RADIUS && distToBottom < CORNER_PENALTY_RADIUS) ||
            (distToRight < CORNER_PENALTY_RADIUS && distToBottom < CORNER_PENALTY_RADIUS);

        double cornerPenalty = isNearCorner ? 50 : 0;

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
}