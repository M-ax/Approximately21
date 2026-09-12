using System;
using System.Linq;

namespace Approximately21.Blackjack;

public readonly record struct SeatPoint(double X, double Z);
public readonly record struct SeatRectangle(double X, double Z, double Width, double Depth);

public readonly record struct BlackjackSeatPose(int SeatIndex, double X, double Z,
    double RotationRadians, double Width, double Depth, double ArcDistance)
{
    public SeatPoint Transform(double x, double z) => new(
        X + Math.Cos(RotationRadians) * x - Math.Sin(RotationRadians) * z,
        Z + Math.Sin(RotationRadians) * x + Math.Cos(RotationRadians) * z);

    public SeatPoint[] Corners => new[]
    {
        Transform(-Width / 2, -Depth / 2), Transform(Width / 2, -Depth / 2),
        Transform(Width / 2, Depth / 2), Transform(-Width / 2, Depth / 2)
    };
}

public static class BlackjackSeatLayout
{
    // Group 1 felt boundary from BlackjackTableMesh.bin at scale 0.1. The curved edge is -Z,
    // not +Z. Coordinates below are relative to the felt bounds, not the ship or whole mesh.
    public const double ModelWidth = 1.03636;
    public const double ModelDepth = 0.61818;
    private const double ModelCenterZ = 0.007143;
    private static readonly SeatPoint[] HalfEdge =
    {
        new(0.047085, -0.301947), new(0.175056, -0.280356),
        new(0.240728, -0.258765), new(0.323250, -0.215584),
        new(0.402227, -0.161607), new(0.449566, -0.107630),
        new(0.485931, -0.053652), new(0.504565, 0.000325)
    };

    // Child rectangles are fractions of the panel. +Z extends inward toward the dealer.
    public static SeatRectangle ActionRectangle(int index)
    {
        if (index < 0 || index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        return new SeatRectangle(index % 2 == 0 ? -0.25 : 0.25, 0.13 - index / 2 * 0.108, 0.47, 0.10);
    }

    public static SeatRectangle Header => new(0, 0.425, 0.97, 0.12);
    public static SeatRectangle Wager => new(0, 0.28, 0.57, 0.12);
    public static SeatRectangle Minus => new(-0.40, 0.28, 0.17, 0.12);
    public static SeatRectangle Plus => new(0.40, 0.28, 0.17, 0.12);
    public static SeatRectangle Status => new(0, -0.37, 0.97, 0.22);

    public static SeatRectangle SummaryRectangle(int index)
    {
        if (index < 0 || index >= 6) throw new ArgumentOutOfRangeException(nameof(index));
        return new SeatRectangle(0, -0.278 - index * 0.036, 0.97, 0.033);
    }

    public static SeatPoint[] Boundary(double width, double depth)
    {
        ValidateBounds(width, depth);
        var edge = Edge(width, depth).ToList();
        edge.Add(new SeatPoint(width / 2, depth / 2));
        edge.Add(new SeatPoint(-width / 2, depth / 2));
        return edge.ToArray();
    }

    private static SeatPoint[] Edge(double width, double depth) => Enumerable.Reverse(HalfEdge)
        .Select(p => new SeatPoint(-p.X, p.Z)).Concat(HalfEdge)
        .Select(p => new SeatPoint(p.X * width / ModelWidth, (p.Z - ModelCenterZ) * depth / ModelDepth)).ToArray();

    public static BlackjackSeatPose[] Create(double width, double depth)
    {
        ValidateBounds(width, depth);
        var scale = Math.Min(width / ModelWidth, depth / ModelDepth);
        var edge = Edge(width, depth);
        var inset = new SeatPoint[edge.Length];
        var normals = new SeatPoint[edge.Length - 1];
        for (var i = 0; i < normals.Length; i++)
        {
            var dx = edge[i + 1].X - edge[i].X;
            var dz = edge[i + 1].Z - edge[i].Z;
            var length = Math.Sqrt(dx * dx + dz * dz);
            normals[i] = new SeatPoint(-dz / length, dx / length);
        }
        for (var i = 0; i < edge.Length; i++)
        {
            var a = normals[Math.Max(0, i - 1)];
            var b = normals[Math.Min(normals.Length - 1, i)];
            var factor = 0.10 * scale / (1 + a.X * b.X + a.Z * b.Z);
            inset[i] = new SeatPoint(edge[i].X + (a.X + b.X) * factor, edge[i].Z + (a.Z + b.Z) * factor);
        }
        var distances = new double[inset.Length];
        for (var i = 1; i < inset.Length; i++)
            distances[i] = distances[i - 1] + Distance(inset[i - 1], inset[i]);
        var margin = 0.11 * scale;
        var poses = new BlackjackSeatPose[BlackjackLimits.MaxSeats];
        for (var seat = 0; seat < poses.Length; seat++)
        {
            var distance = margin + seat * (distances.Last() - 2 * margin) / (poses.Length - 1);
            var segment = 0;
            while (segment < inset.Length - 2 && distances[segment + 1] < distance) segment++;
            var a = inset[segment];
            var b = inset[segment + 1];
            var t = (distance - distances[segment]) / (distances[segment + 1] - distances[segment]);
            poses[seat] = new BlackjackSeatPose(seat, a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t,
                Math.Atan2(b.Z - a.Z, b.X - a.X), 0.125 * scale, 0.145 * scale, distance);
        }
        return poses;
    }

    // Uniform width-normalized coordinates preserve rotations and rectangle geometry.
    public static BlackjackSeatPose[] CreateNormalized() => Create(1, ModelDepth / ModelWidth);

    private static double Distance(SeatPoint a, SeatPoint b) => Math.Sqrt(
        (b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    private static void ValidateBounds(double width, double depth)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ||
            double.IsNaN(depth) || double.IsInfinity(depth) || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
    }
}