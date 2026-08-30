using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Approximately21;

internal sealed class InjectableBlockDefinition
{
    public InjectableBlockDefinition(InjectableBlockData data, string displayName, string description)
    {
        Data = data;
        DisplayName = displayName;
        Description = description;
    }

    public InjectableBlockData Data { get; }

    public string DisplayName { get; }

    public string Description { get; }
}

internal static class InjectableBlockConfiguration
{
    private static readonly Dictionary<Type, InjectableBlockDefinition> Definitions = new();

    public static void Register(Type blockType, InjectableBlockData data, string displayName, string description)
        => Definitions[blockType] = new InjectableBlockDefinition(data, displayName, description);

    public static string GetPrefabName(Type blockType) => Get(blockType).Data.PrefabName;

    public static string GetSourceItemName(Type blockType) => Get(blockType).Data.SourceItemName;

    public static string GetDisplayName(Type blockType) => Get(blockType).DisplayName;

    public static string GetDescription(Type blockType) => Get(blockType).Description;

    public static string GetAssetBundleFileName(Type blockType) => Get(blockType).Data.AssetBundleFileName;

    public static string GetAssetMeshName(Type blockType) => Get(blockType).Data.AssetMeshName;

    public static string GetRawMeshFileName(Type blockType) => Get(blockType).Data.RawMeshFileName;

    public static float GetRawMeshScale(Type blockType) => Get(blockType).Data.RawMeshScale;

    public static float GetModelColorRed(Type blockType) => Get(blockType).Data.ModelColorRed;

    public static float GetModelColorGreen(Type blockType) => Get(blockType).Data.ModelColorGreen;

    public static float GetModelColorBlue(Type blockType) => Get(blockType).Data.ModelColorBlue;

    public static Texture2D CreateIcon(Type blockType) => Get(blockType).Data.CreateIcon();

    private static InjectableBlockDefinition Get(Type blockType)
    {
        if (Definitions.TryGetValue(blockType, out var definition))
            return definition;

        throw new InvalidOperationException($"No injectable-block definition is registered for {blockType.FullName}.");
    }
}

internal static class BlockModelCache
{
    private static readonly Dictionary<string, Mesh> Meshes = new();

    public static bool TryGet(string blockName, out Mesh mesh)
        => Meshes.TryGetValue(blockName, out mesh);

    public static void Store(string blockName, Mesh mesh)
        => Meshes[blockName] = mesh;
}

internal static class BlockMaterialCache
{
    private static readonly Dictionary<string, Material> Materials = new();

    public static bool TryGet(string blockName, out Material material)
        => Materials.TryGetValue(blockName, out material);

    public static void Store(string blockName, Material material)
        => Materials[blockName] = material;
}

internal static class BlockTextureCache
{
    private static readonly Dictionary<string, Texture3D> Textures = new();

    public static bool TryGet(string blockName, out Texture3D texture)
        => Textures.TryGetValue(blockName, out texture);

    public static void Store(string blockName, Texture3D texture)
        => Textures[blockName] = texture;
}

internal static class ProceduralBlockMeshFactory
{
    public static Mesh CreateCube(string blockName)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var cubeMesh = cube.GetComponent<MeshFilter>().sharedMesh;
        var placeholderMesh = UnityEngine.Object.Instantiate(cubeMesh);
        placeholderMesh.name = blockName + "_PlaceholderMesh";
        UnityEngine.Object.Destroy(cube);
        return placeholderMesh;
    }
}

internal static class RawMeshModelLoader
{
    private const int MaximumVertexCount = 1_000_000;

    public static Mesh TryLoadMesh(string meshPath, string meshName, float scale)
    {
        using var stream = File.OpenRead(meshPath);
        using var reader = new BinaryReader(stream);

        var signature = reader.ReadBytes(4);
        if (signature.Length != 4 || signature[0] != 'A' || signature[1] != '2' || signature[2] != '1' || signature[3] != 'M')
            throw new InvalidDataException("The file does not contain an Approximately21 mesh.");

        var vertexCount = reader.ReadInt32();
        var indexCount = reader.ReadInt32();
        if (vertexCount <= 0 || vertexCount > MaximumVertexCount || indexCount <= 0 || indexCount % 3 != 0 || indexCount > vertexCount)
            throw new InvalidDataException("The mesh header contains invalid vertex or triangle counts.");

        var vertices = new Il2CppStructArray<Vector3>(vertexCount);
        for (var index = 0; index < vertexCount; index++)
            vertices[index] = new Vector3(reader.ReadSingle() * scale, reader.ReadSingle() * scale, reader.ReadSingle() * scale);

        var normals = new Il2CppStructArray<Vector3>(vertexCount);
        for (var index = 0; index < vertexCount; index++)
            normals[index] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        var textureCoordinates = new Il2CppStructArray<Vector2>(vertexCount);
        for (var index = 0; index < vertexCount; index++)
            textureCoordinates[index] = new Vector2(reader.ReadSingle(), reader.ReadSingle());

        var triangles = new Il2CppStructArray<int>(indexCount);
        for (var index = 0; index < indexCount; index++)
        {
            var vertexIndex = reader.ReadInt32();
            if (vertexIndex < 0 || vertexIndex >= vertexCount)
                throw new InvalidDataException("The mesh contains an invalid triangle index.");

            triangles[index] = vertexIndex;
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("The mesh contains unexpected trailing data.");

        var mesh = new Mesh { name = meshName };
        if (vertexCount > ushort.MaxValue)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, textureCoordinates);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}

internal static class AssetBundleModelLoader
{
    private static readonly Dictionary<string, AssetBundle> LoadedAssetBundles = new();
    private static readonly Dictionary<string, Il2CppStructArray<byte>> LoadedAssetBundleData = new();
    private static readonly Dictionary<string, IntPtr> LoadedAssetBundleHandles = new();
    private static readonly Dictionary<string, IntPtr> LoadedMeshHandles = new();
    private static readonly Dictionary<string, AssetBundleCreateRequest> PendingBundleRequests = new();

    public static bool IsLoading(string bundlePath)
        => PendingBundleRequests.TryGetValue(bundlePath, out var request) && request != null && !request.isDone;

    public static Mesh TryLoadMesh(string bundlePath, string meshName)
    {
        if (!LoadedAssetBundles.TryGetValue(bundlePath, out var bundle) || bundle == null)
        {
            if (!PendingBundleRequests.TryGetValue(bundlePath, out var request) || request == null)
            {
                if (!LoadedAssetBundleData.TryGetValue(bundlePath, out var bundleData) || bundleData == null)
                {
                    bundleData = new Il2CppStructArray<byte>(File.ReadAllBytes(bundlePath));
                    LoadedAssetBundleData[bundlePath] = bundleData;
                }

                request = AssetBundle.LoadFromMemoryAsync(bundleData);
                if (request == null)
                    return null;

                PendingBundleRequests[bundlePath] = request;
            }

            if (!request.isDone)
                return null;

            PendingBundleRequests.Remove(bundlePath);
            bundle = request.assetBundle;
            if (bundle == null)
                return null;

            LoadedAssetBundles[bundlePath] = bundle;
            LoadedAssetBundleHandles[bundlePath] = IL2CPP.il2cpp_gchandle_new(bundle.Pointer, false);
        }

        var mesh = bundle.LoadAsset<Mesh>(meshName);
        if (mesh != null && !LoadedMeshHandles.ContainsKey(bundlePath + "::" + meshName))
            LoadedMeshHandles[bundlePath + "::" + meshName] = IL2CPP.il2cpp_gchandle_new(mesh.Pointer, false);

        return mesh;
    }
}