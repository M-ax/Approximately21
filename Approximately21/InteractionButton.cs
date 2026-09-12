using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using UnityEngine;

namespace Approximately21;

public sealed class InteractionButton
{
    private readonly Vector2 _size;
    private readonly Vector3 _localOffset;

    internal InteractionButton(string name, Vector2 size, Vector3 localOffset)
    {
        Name = name;
        _size = size;
        _localOffset = localOffset;
    }

    public string Name { get; }

    public event Action<InteractionContext> Clicked;

    public float RotationRadians { get; init; }
    public string Label { get; init; }
    public Func<InteractionContext, InteractionVisualState> GetVisualState { get; set; }

    internal Vector2 Size => _size;
    internal InteractionVisualState Evaluate(InteractionContext context) =>
        GetVisualState?.Invoke(context) ?? new InteractionVisualState(Label ?? Name, true);

    internal Mesh Mesh { get; private set; }

    internal Vector3 LocalOffset => _localOffset;

    internal void EnsureMesh(string prefabName)
    {
        if (Mesh != null)
            return;

        var vertices = new Il2CppStructArray<Vector3>(4);
        vertices[0] = new Vector3(-_size.x * 0.5f, 0f, -_size.y * 0.5f);
        vertices[1] = new Vector3(-_size.x * 0.5f, 0f, _size.y * 0.5f);
        vertices[2] = new Vector3(_size.x * 0.5f, 0f, _size.y * 0.5f);
        vertices[3] = new Vector3(_size.x * 0.5f, 0f, -_size.y * 0.5f);
        for (var index = 0; index < vertices.Length; index++)
            vertices[index] = Rotate(vertices[index]);
        var normals = new Il2CppStructArray<Vector3>(4);
        for (var index = 0; index < normals.Length; index++)
            normals[index] = Vector3.up;

        var triangles = new Il2CppStructArray<int>(6);
        triangles[0] = 0;
        triangles[1] = 1;
        triangles[2] = 2;
        triangles[3] = 0;
        triangles[4] = 2;
        triangles[5] = 3;

        Mesh = new Mesh { name = $"{prefabName}_{Name}ButtonQuad" };
        Mesh.SetVertices(vertices);
        Mesh.SetNormals(normals);
        Mesh.SetTriangles(triangles, 0);
        Mesh.RecalculateBounds();
    }

    internal bool Contains(Vector3 localHit, Bounds interactionBounds)
    {
        var center = interactionBounds.center + _localOffset;
        return InteractionGeometry.Contains(localHit.x - center.x, localHit.z - center.z,
            _size.x, _size.y, RotationRadians);
    }

    internal Vector3 Rotate(Vector3 point)
    {
        var c = Mathf.Cos(RotationRadians);
        var s = Mathf.Sin(RotationRadians);
        return new Vector3(c * point.x - s * point.z, point.y, s * point.x + c * point.z);
    }

    internal void ReleaseMesh()
    {
        if (Mesh != null) UnityEngine.Object.Destroy(Mesh);
        Mesh = null;
    }

    internal void InvokeClick(InteractionContext tableEntityIndex)
    {
        Clicked?.Invoke(tableEntityIndex);
    }
}
