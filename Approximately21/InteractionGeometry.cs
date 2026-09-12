using System;
using System.Collections.Generic;

namespace Approximately21;

public static class InteractionGeometry
{
    public static bool Contains(float x, float y, float width, float height, float radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return Math.Abs(c * x + s * y) <= width * 0.5 + 0.000001 &&
               Math.Abs(-s * x + c * y) <= height * 0.5 + 0.000001;
    }

    public static bool IsNearer(float distance, float nearest) =>
        !float.IsNaN(distance) && !float.IsInfinity(distance) && distance >= 0 && distance < nearest;

    // Ownership stops at a placed block, not at its containing ship. Invalid/cyclic paths fail closed.
    public static T FindOwner<T>(T start, T none, Func<T, bool> exists, Func<T, bool> isBlock,
        Func<T, T> parent)
    {
        var visited = new HashSet<T>();
        for (var current = start; !EqualityComparer<T>.Default.Equals(current, none) && exists(current) && visited.Add(current); current = parent(current))
        {
            if (isBlock(current))
                return current;
        }
        return none;
    }

    public static bool CanDispatch(bool surfaceSelected, bool contains, bool enabled) =>
        surfaceSelected && contains && enabled;
}