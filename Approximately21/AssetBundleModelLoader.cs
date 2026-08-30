using System;
using System.Collections.Generic;
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

internal static class AssetBundleModelLoader
{
    private static readonly Dictionary<string, AssetBundle> LoadedAssetBundles = new();

    public static Mesh TryLoadMesh(string bundlePath, string meshName)
    {
        if (!LoadedAssetBundles.TryGetValue(bundlePath, out var bundle) || bundle == null)
        {
            bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
                return null;

            LoadedAssetBundles[bundlePath] = bundle;
        }

        return bundle.LoadAsset<Mesh>(meshName);
    }
}