
using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

namespace Approximately21;

public sealed class CheckerboardBlockRegistrar : MonoBehaviour
{
    private const string BlockName = "Approximately21_CheckerboardBlock";
    private const string TargetItemName = "Frame Quarter";

    private readonly ManualLogSource _log;
    private bool _registered;
    private bool _availabilityRefreshed;
    private bool _menuRefreshed;
    private bool _refreshErrorLogged;
    private bool _menuRefreshErrorLogged;
    private bool _menuItemNotFoundLogged;
    private bool _frameNotFoundLogged;
    private bool _placedVisualUpdated;
    private bool _placedVisualErrorLogged;
    private int _refreshDelay = 120;
    private int _liveVisualRefreshDelay;
    private Core _registeredCore;
    private SCPrefab _framePrefab;
    private EPC_SpaceshipComponent _checkerboardBlock;
    private readonly System.Collections.Generic.HashSet<string> _frameQuarterMeshInstanceIds = new();
    private static Texture2D _checkerboardTexture;
    private static Texture3D _checkerboardVolumeTexture;
    private static Mesh _checkerboardMesh;

    public CheckerboardBlockRegistrar(IntPtr pointer)
        : base(pointer)
    {
        _log = Plugin.Log;
    }

    private void Update()
    {
        try
        {
            var core = Core.Get();
            if (core == null || core._componentsMap == null)
                return;

            if (!ReferenceEquals(_registeredCore, core))
                ResetForCore(core);

            if (!_registered)
                _registered = Register(core);

            if (_registered && !_placedVisualUpdated)
            {
                _placedVisualUpdated = TryReplacePlacedRendererMaterials(_checkerboardBlock, _checkerboardTexture);
                if (_placedVisualUpdated)
                    _log.LogInfo($"Applied {BlockName} to the {TargetItemName} ECS prefab renderer.");
            }

            if (_placedVisualUpdated && _frameQuarterMeshInstanceIds.Count > 0 && _liveVisualRefreshDelay-- <= 0)
            {
                _liveVisualRefreshDelay = 10;
                RefreshLiveRendererMaterials(_checkerboardTexture);
            }

            if (!_registered || _refreshDelay-- > 0)
                return;

            _refreshDelay = 120;
            if (!_availabilityRefreshed)
                _availabilityRefreshed = RefreshAvailableComponents(core);

            if (_availabilityRefreshed && !_menuRefreshed)
            {
                _menuRefreshed = RefreshInventoryMenus();
                if (_menuRefreshed)
                    _log.LogInfo($"Added {BlockName} to the available buildable components and refreshed the build menu.");
            }
        }
        catch (Exception exception)
        {
            _log.LogError($"Could not register {BlockName}: {exception}");
        }
    }

    private void ResetForCore(Core core)
    {
        _registeredCore = core;
        _registered = false;
        _availabilityRefreshed = false;
        _menuRefreshed = false;
        _refreshErrorLogged = false;
        _menuRefreshErrorLogged = false;
        _menuItemNotFoundLogged = false;
        _frameNotFoundLogged = false;
        _placedVisualUpdated = false;
        _placedVisualErrorLogged = false;
        _liveVisualRefreshDelay = 0;
        _frameQuarterMeshInstanceIds.Clear();
        _refreshDelay = 120;
        _log.LogInfo($"Detected a new game core; registering {BlockName} when Frame Quarter is available.");
    }

    private bool Register(Core core)
    {
        Dictionary<SCPrefab, EPC_SpaceshipComponent> components = core._componentsMap;
        var frame = FindFramePrefab();
        if (frame == null)
        {
            if (!_frameNotFoundLogged)
            {
                _frameNotFoundLogged = true;
                _log.LogInfo($"Frame Quarter is not ready for {BlockName} yet; registration will retry.");
            }

            return false;
        }

        var checkerboard = _checkerboardTexture ?? CreateCheckerboardTexture();
        frame._iconTexture2D = checkerboard;
        _framePrefab = new SCPrefab(frame);
        _checkerboardBlock = frame;
        components[_framePrefab] = frame;
        _checkerboardTexture = checkerboard;

        _log.LogInfo($"Applied {BlockName} to the {TargetItemName} prototype; waiting for the inventory to initialize.");
        return true;
    }

    private bool RefreshAvailableComponents(Core core)
    {
        try
        {
            core.RefreshStandaloneAvailableComponents();
            _log.LogInfo($"Added {BlockName} to the available buildable inventory components; waiting for the build menu.");
            return true;
        }
        catch (Exception exception)
        {
            if (_refreshErrorLogged)
                return false;

            _refreshErrorLogged = true;
            _log.LogInfo($"Inventory is not ready for {BlockName} yet; registration will retry. {exception.Message}");
            return false;
        }
    }

    private bool RefreshInventoryMenus()
    {
        try
        {
            var inventories = Resources.FindObjectsOfTypeAll<UIInventory>();
            if (inventories.Length == 0)
                return false;

            var frameMenuItemFound = false;
            for (var index = 0; index < inventories.Length; index++)
            {
                var inventory = inventories[index];
                inventory.RefreshItems();

                if (!inventory._scPrefabToListItemMap.TryGetValue(_framePrefab, out var frameMenuItem))
                    continue;

                frameMenuItem.SetInventoryComponent(_checkerboardBlock);
                frameMenuItemFound = true;
            }

            if (!frameMenuItemFound && !_menuItemNotFoundLogged)
            {
                _menuItemNotFoundLogged = true;
                _log.LogInfo($"The Frame Quarter menu row is not ready for {BlockName} yet; refresh will retry.");
            }

            return frameMenuItemFound;
        }
        catch (Exception exception)
        {
            if (!_menuRefreshErrorLogged)
            {
                _menuRefreshErrorLogged = true;
                _log.LogInfo($"Build menu is not ready for {BlockName} yet; refresh will retry. {exception.Message}");
            }

            return false;
        }
    }

    private static EPC_SpaceshipComponent FindFramePrefab()
    {
        var components = Resources.FindObjectsOfTypeAll<EPC_SpaceshipComponent>();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component.GetName() == TargetItemName)
                return component;
        }

        return null;
    }

    private static Texture2D CreateCheckerboardTexture()
    {
        var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false)
        {
            name = BlockName + "_Icon",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };
        var pixels = new Color32[64];
        var pink = new Color32(255, 0, 144, 255);
        var black = new Color32(0, 0, 0, 255);

        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            pixels[y * 8 + x] = (x + y) % 2 == 0 ? pink : black;

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private bool TryReplacePlacedRendererMaterials(EPC_SpaceshipComponent component, Texture2D checkerboard)
    {
        try
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null)
                return false;

            var entityManager = world.EntityManager;
            var rootEntity = EntityPrefabComponent.Get(component);
            if (!entityManager.Exists(rootEntity))
                return false;

            var materialReplaced = TryReplaceRendererMaterial(entityManager, rootEntity, checkerboard);
            if (!entityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
                return materialReplaced;

            var linkedEntities = entityManager.GetBuffer<LinkedEntityGroup>(rootEntity, true);
            for (var index = 0; index < linkedEntities.Length; index++)
                materialReplaced |= TryReplaceRendererMaterial(entityManager, linkedEntities[index].Value, checkerboard);

            return materialReplaced;
        }
        catch (Exception exception)
        {
            if (!_placedVisualErrorLogged)
            {
                _placedVisualErrorLogged = true;
                _log.LogWarning($"Could not update the {TargetItemName} ECS prefab renderer: {exception.Message}");
            }

            return false;
        }
    }

    private bool TryReplaceRendererMaterial(EntityManager entityManager, Entity entity, Texture2D checkerboard)
    {
        if (!entityManager.HasComponent<CRPRendererData>(entity))
            return false;

        var rendererData = entityManager.GetComponentData<CRPRendererData>(entity);
        var mesh = rendererData.GetDrawDataMeshReference()._meshReference.Managed();
        if (mesh != null)
            _frameQuarterMeshInstanceIds.Add(mesh.name);

        var sourceMaterial = rendererData._material.Managed();
        if (sourceMaterial == null)
            return false;

        var material = GetCheckerboardMaterial(sourceMaterial, checkerboard);
        entityManager.SetComponentData(entity, new CRPRendererData(GetCheckerboardMesh(), 0, material));
        ApplyCheckerboardColor(entityManager, entity);
        return true;
    }

    private void RefreshLiveRendererMaterials(Texture2D checkerboard)
    {
        try
        {
            var worlds = World.All;
            for (var worldIndex = 0; worldIndex < worlds.Count; worldIndex++)
            {
                var world = worlds[worldIndex];
                if (world == null || !world.IsCreated)
                    continue;

                var entityManager = world.EntityManager;
                if (!world.IsCreated)
                    continue;

                RefreshLiveRendererMaterials(entityManager, checkerboard);
            }
        }
        catch (Exception exception)
        {
            if (!_placedVisualErrorLogged)
            {
                _placedVisualErrorLogged = true;
                _log.LogWarning($"Could not update live {TargetItemName} renderers: {exception.Message}");
            }
        }
    }

    private void RefreshLiveRendererMaterials(EntityManager entityManager, Texture2D checkerboard)
    {
        var requiredComponents = new Il2CppStructArray<ComponentType>(1);
        requiredComponents[0] = ComponentType.ReadOnly<CRPRendererData>();
        var query = entityManager.CreateEntityQuery(requiredComponents);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (var index = 0; index < entities.Length; index++)
                    TryReplaceLiveRendererMaterial(entityManager, entities[index], checkerboard);
            }
            finally
            {
                entities.Dispose();
            }
        }
        finally
        {
            query.Dispose();
        }
    }

    private void TryReplaceLiveRendererMaterial(EntityManager entityManager, Entity entity, Texture2D checkerboard)
    {
        var rendererData = entityManager.GetComponentData<CRPRendererData>(entity);
        var mesh = rendererData.GetDrawDataMeshReference()._meshReference.Managed();
        if (mesh == null || !_frameQuarterMeshInstanceIds.Contains(mesh.name))
            return;

        var sourceMaterial = rendererData._material.Managed();
        if (sourceMaterial == null || sourceMaterial.name == BlockName + "_PlacedMaterial")
            return;

        var material = GetCheckerboardMaterial(sourceMaterial, checkerboard);
        entityManager.SetComponentData(entity, new CRPRendererData(GetCheckerboardMesh(), 0, material));
        ApplyCheckerboardColor(entityManager, entity);
        _log.LogInfo($"Applied {BlockName} to a live {TargetItemName} ECS renderer.");
    }

    private static void ApplyCheckerboardColor(EntityManager entityManager, Entity entity)
    {
        if (!entityManager.HasComponent<CRPRendererMemory_Color>(entity))
            return;

        var colorMemory = entityManager.GetComponentData<CRPRendererMemory_Color>(entity);
        colorMemory.Unpack(out _, out var specularPacked);
        entityManager.SetComponentData(entity, new CRPRendererMemory_Color(new float4(1f, 1f, 1f, 1f), specularPacked));
    }

    private static void ApplyCheckerboardTexture(Material material, Texture2D checkerboard)
    {
        material.mainTexture = checkerboard;
        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", checkerboard);
        if (material.HasProperty("_Frame3DTexture"))
            material.SetTexture("_Frame3DTexture", GetCheckerboardVolumeTexture());
    }

    private static Texture3D GetCheckerboardVolumeTexture()
    {
        if (_checkerboardVolumeTexture != null)
            return _checkerboardVolumeTexture;

        _checkerboardVolumeTexture = new Texture3D(8, 8, 8, TextureFormat.RGBA32, false)
        {
            name = BlockName + "_Volume",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };
        var pixels = new Color32[8 * 8 * 8];
        var pink = new Color32(255, 0, 144, 255);
        var black = new Color32(0, 0, 0, 255);

        for (var z = 0; z < 8; z++)
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            pixels[x + 8 * (y + 8 * z)] = (x + y + z) % 2 == 0 ? pink : black;

        _checkerboardVolumeTexture.SetPixels32(pixels);
        _checkerboardVolumeTexture.Apply(false, false);
        return _checkerboardVolumeTexture;
    }

    private static Material GetCheckerboardMaterial(Material fallbackMaterial, Texture2D checkerboard)
    {
        var checkerboardMaterial = new Material(fallbackMaterial)
        {
            name = BlockName + "_PlacedMaterial"
        };
        ApplyCheckerboardTexture(checkerboardMaterial, checkerboard);
        return checkerboardMaterial;
    }

    private static Mesh GetCheckerboardMesh()
    {
        if (_checkerboardMesh != null)
            return _checkerboardMesh;

        const float extent = 0.45f;
        _checkerboardMesh = new Mesh { name = BlockName + "_Mesh" };
        _checkerboardMesh.vertices = new Il2CppStructArray<Vector3>(new[]
        {
            new Vector3(-extent, -extent, extent), new Vector3(extent, -extent, extent), new Vector3(extent, extent, extent), new Vector3(-extent, extent, extent),
            new Vector3(extent, -extent, -extent), new Vector3(-extent, -extent, -extent), new Vector3(-extent, extent, -extent), new Vector3(extent, extent, -extent),
            new Vector3(-extent, -extent, -extent), new Vector3(-extent, -extent, extent), new Vector3(-extent, extent, extent), new Vector3(-extent, extent, -extent),
            new Vector3(extent, -extent, extent), new Vector3(extent, -extent, -extent), new Vector3(extent, extent, -extent), new Vector3(extent, extent, extent),
            new Vector3(-extent, extent, extent), new Vector3(extent, extent, extent), new Vector3(extent, extent, -extent), new Vector3(-extent, extent, -extent),
            new Vector3(-extent, -extent, -extent), new Vector3(extent, -extent, -extent), new Vector3(extent, -extent, extent), new Vector3(-extent, -extent, extent)
        });
        _checkerboardMesh.triangles = new Il2CppStructArray<int>(new[]
        {
            0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11,
            12, 13, 14, 12, 14, 15, 16, 17, 18, 16, 18, 19, 20, 21, 22, 20, 22, 23
        });
        _checkerboardMesh.uv = new Il2CppStructArray<Vector2>(new[]
        {
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f),
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f),
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f),
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f),
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f),
            new Vector2(0f, 0f), new Vector2(4f, 0f), new Vector2(4f, 4f), new Vector2(0f, 4f)
        });
        _checkerboardMesh.RecalculateNormals();
        _checkerboardMesh.RecalculateBounds();
        return _checkerboardMesh;
    }

}