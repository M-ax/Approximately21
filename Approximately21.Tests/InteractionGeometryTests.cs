using System;
using System.Collections.Generic;
using Xunit;

namespace Approximately21.Tests;

public class InteractionGeometryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.4)]
    [InlineData(-0.7)]
    [InlineData(1.5707963)]
    public void RotatedCornersAndInteriorMatchInverseHitTest(float angle)
    {
        foreach (var point in new[] { (0f, 0f), (1f, .25f), (-1f, -.25f), (.5f, -.1f) })
        {
            var x = MathF.Cos(angle) * point.Item1 - MathF.Sin(angle) * point.Item2;
            var y = MathF.Sin(angle) * point.Item1 + MathF.Cos(angle) * point.Item2;
            Assert.True(InteractionGeometry.Contains(x, y, 2, .5f, angle));
        }
        var outsideX = MathF.Cos(angle) * 1.01f;
        var outsideY = MathF.Sin(angle) * 1.01f;
        Assert.False(InteractionGeometry.Contains(outsideX, outsideY, 2, .5f, angle));
    }

    [Fact]
    public void RotationIsNotAnAxisAlignedBoundingBox()
    {
        Assert.False(InteractionGeometry.Contains(.9f, 0, 2, .5f, MathF.PI / 2));
        Assert.True(InteractionGeometry.Contains(0, .9f, 2, .5f, MathF.PI / 2));
    }

    [Fact]
    public void TwoBlocksUnderOneShipKeepTheirOwnControls()
    {
        var parents = new Dictionary<int, int> { [11] = 10, [12] = 11, [21] = 20, [22] = 21, [10] = 1, [20] = 1, [1] = 0 };
        int Owner(int entity) => InteractionGeometry.FindOwner(entity, 0, parents.ContainsKey,
            e => e == 10 || e == 20, e => parents[e]);
        Assert.Equal(10, Owner(12));
        Assert.Equal(20, Owner(22));
        Assert.Equal(10, Owner(10));
        Assert.Equal(0, Owner(1));
        Assert.Equal(0, Owner(99));
    }

    [Fact]
    public void CyclicAndDestroyedParentsFailClosed()
    {
        Assert.Equal(0, InteractionGeometry.FindOwner(1, 0, _ => true, _ => false, e => e == 1 ? 2 : 1));
        Assert.Equal(0, InteractionGeometry.FindOwner(1, 0, e => e == 1, e => e == 2, _ => 2));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OnlyNearestSurfaceCanDispatchEvenIfDisabledOrEmpty(bool enabled, bool contains)
    {
        var distances = new[] { 4f, 2f, 8f, -1f, float.NaN, float.PositiveInfinity };
        var nearest = float.PositiveInfinity;
        var selected = -1;
        for (var i = 0; i < distances.Length; i++)
            if (InteractionGeometry.IsNearer(distances[i], nearest))
            {
                nearest = distances[i];
                selected = i;
            }
        Assert.Equal(1, selected);
        var dispatches = 0;
        for (var i = 0; i < distances.Length; i++)
            if (InteractionGeometry.CanDispatch(i == selected, i == selected ? contains : true, i == selected ? enabled : true))
                dispatches++;
        Assert.Equal(enabled && contains ? 1 : 0, dispatches);
        Assert.False(InteractionGeometry.IsNearer(2, nearest));
    }
}