using System;
using System.Collections.Generic;

namespace HUMofA.ComputerUse;

public readonly record struct Point2D(double X, double Y)
{
    public static Point2D operator +(Point2D a, Point2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2D operator *(Point2D a, double scalar) => new(a.X * scalar, a.Y * scalar);
    public static Point2D operator *(double scalar, Point2D a) => new(a.X * scalar, a.Y * scalar);
    public static Point2D operator /(Point2D a, double scalar) => new(a.X / scalar, a.Y / scalar);

    public double DistanceTo(Point2D other) => Math.Sqrt(Math.Pow(other.X - X, 2) + Math.Pow(other.Y - Y, 2));
    public double Length => Math.Sqrt(X * X + Y * Y);
}

public static class BezierMath
{
    public const double DefaultA = 100.0; // Ballistic initiation constant in ms
    public const double DefaultB = 140.0; // Information processing rate index in ms/bit
    public const double DefaultTargetWidth = 40.0; // Default target width in pixels
    public const double MinMovementDuration = 140.0; // Milliseconds
    public const double MaxMovementDuration = 1200.0; // Milliseconds
    public const double DefaultTremorMaxSigma = 0.7; // Maximum micro-tremor sigma in pixels (<= 1.5)

    /// <summary>
    /// Computes movement duration according to Fitts's Law: MT = a + b * log2(D / W + 1)
    /// Clamped to [minDuration, maxDuration].
    /// </summary>
    public static double CalculateFittsDuration(
        double distance,
        double targetWidth = DefaultTargetWidth,
        double a = DefaultA,
        double b = DefaultB,
        double minDuration = MinMovementDuration,
        double maxDuration = MaxMovementDuration)
    {
        if (distance <= 0) return minDuration;
        double w = targetWidth > 0 ? targetWidth : DefaultTargetWidth;
        double indexDifficulty = Math.Log2((distance / w) + 1.0);
        double mt = a + (b * indexDifficulty);
        return Math.Clamp(mt, minDuration, maxDuration);
    }

    /// <summary>
    /// Minimum-jerk easing profile: t(tau) = 10*tau^3 - 15*tau^4 + 6*tau^5
    /// Strictly monotonic on [0, 1] with zero jerk and zero velocity at endpoints.
    /// </summary>
    public static double MinimumJerk(double tau)
    {
        tau = Math.Clamp(tau, 0.0, 1.0);
        return (10.0 * Math.Pow(tau, 3)) - (15.0 * Math.Pow(tau, 4)) + (6.0 * Math.Pow(tau, 5));
    }

    /// <summary>
    /// Evaluates cubic Bezier curve at parameter t in [0, 1]:
    /// B(t) = (1-t)^3 * P0 + 3(1-t)^2 * t * P1 + 3(1-t) * t^2 * P2 + t^3 * P3
    /// </summary>
    public static Point2D EvaluateCubicBezier(Point2D p0, Point2D p1, Point2D p2, Point2D p3, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        double u = 1.0 - t;
        double tt = t * t;
        double uu = u * u;
        double uuu = uu * u;
        double ttt = tt * t;

        double x = (uuu * p0.X) + (3.0 * uu * t * p1.X) + (3.0 * u * tt * p2.X) + (ttt * p3.X);
        double y = (uuu * p0.Y) + (3.0 * uu * t * p1.Y) + (3.0 * u * tt * p2.Y) + (ttt * p3.Y);

        return new Point2D(x, y);
    }

    /// <summary>
    /// Generates intermediate control points P1 and P2 with orthogonal chord displacement.
    /// </summary>
    public static (Point2D P1, Point2D P2) GenerateControlPoints(Point2D p0, Point2D p3, Random? rng = null)
    {
        rng ??= Random.Shared;
        Point2D v = p3 - p0;
        double d = v.Length;

        if (d < 1.0)
        {
            return (p0, p3);
        }

        // Curvature side (+1 or -1)
        double curvatureDirection = rng.NextDouble() < 0.5 ? 1.0 : -1.0;
        Point2D normal = new Point2D(-v.Y / d, v.X / d) * curvatureDirection;

        // Longitudinal progression factors: alpha1 in [0.20, 0.35], alpha2 in [0.65, 0.82]
        double alpha1 = 0.20 + (rng.NextDouble() * 0.15);
        double alpha2 = 0.65 + (rng.NextDouble() * 0.17);

        // Normal displacement magnitudes from Gaussian distribution: delta1 ~ N(0, (0.12*D)^2), delta2 ~ N(0, (0.08*D)^2)
        double delta1 = NextGaussian(rng, 0.0, 0.12 * d);
        double delta2 = NextGaussian(rng, 0.0, 0.08 * d);

        Point2D p1 = p0 + (v * alpha1) + (normal * delta1);
        Point2D p2 = p0 + (v * alpha2) + (normal * delta2);

        return (p1, p2);
    }

    /// <summary>
    /// Generates Gaussian random number using Box-Muller transform: mean + stdDev * Z
    /// </summary>
    public static double NextGaussian(Random rng, double mean = 0.0, double stdDev = 1.0)
    {
        double u1 = 1.0 - rng.NextDouble(); // (0, 1]
        double u2 = 1.0 - rng.NextDouble();
        double z0 = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + (stdDev * z0);
    }

    /// <summary>
    /// Computes full 1000Hz (1ms step) trajectory coordinates from P0 to P3.
    /// </summary>
    public static List<Point2D> GenerateTrajectory(
        Point2D p0,
        Point2D p3,
        double targetWidth = DefaultTargetWidth,
        bool addMicroTremor = true,
        double tremorMaxSigma = DefaultTremorMaxSigma,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        double distance = p0.DistanceTo(p3);
        double durationMs = CalculateFittsDuration(distance, targetWidth);
        int totalSteps = Math.Max(1, (int)Math.Round(durationMs));

        var (p1, p2) = GenerateControlPoints(p0, p3, rng);
        var trajectory = new List<Point2D>(totalSteps + 1);

        for (int step = 0; step <= totalSteps; step++)
        {
            double tau = (double)step / totalSteps;
            double t = MinimumJerk(tau);
            Point2D pt = EvaluateCubicBezier(p0, p1, p2, p3, t);

            if (addMicroTremor && step < totalSteps)
            {
                // Tremor dampens as tau approaches 1 (target acquisition)
                double sigmaTau = tremorMaxSigma * (1.0 - tau);
                double tremorX = NextGaussian(rng, 0.0, sigmaTau);
                double tremorY = NextGaussian(rng, 0.0, sigmaTau);
                pt = new Point2D(pt.X + tremorX, pt.Y + tremorY);
            }
            else if (step == totalSteps)
            {
                // Ensure exact endpoint convergence at tau = 1
                pt = p3;
            }

            trajectory.Add(pt);
        }

        return trajectory;
    }
}
