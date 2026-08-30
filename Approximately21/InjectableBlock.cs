using System;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Approximately21;

public sealed class InjectableBlockData
{
    public InjectableBlockData(string prefabName,
        string sourceItemName,
        Func<Texture2D> createIcon,
        string assetBundleFileName,
        string assetMeshName,
        string rawMeshFileName,
        float rawMeshScale,
        float modelColorRed,
        float modelColorGreen,
        float modelColorBlue)
    {
        PrefabName = prefabName;
        SourceItemName = sourceItemName;
        CreateIcon = createIcon;
        AssetBundleFileName = assetBundleFileName;
        AssetMeshName = assetMeshName;
        RawMeshFileName = rawMeshFileName;
        RawMeshScale = rawMeshScale;
        ModelColorRed = modelColorRed;
        ModelColorGreen = modelColorGreen;
        ModelColorBlue = modelColorBlue;
    }

    public string PrefabName { get; }

    public string SourceItemName { get; }

    public Func<Texture2D> CreateIcon { get; }

    public string AssetBundleFileName { get; }

    public string AssetMeshName { get; }

    public string RawMeshFileName { get; }

    public float RawMeshScale { get; }

    public float ModelColorRed { get; }

    public float ModelColorGreen { get; }

    public float ModelColorBlue { get; }
}

public class InjectableBlock : MonoBehaviour
{
    private const int MaterialGroupCount = 4;

    private readonly ManualLogSource _log;
    private readonly System.Collections.Generic.Dictionary<int, EPC_Renderer> _additionalRenderers = new();
    private readonly System.Collections.Generic.Dictionary<int, Entity> _additionalRendererEntities = new();
    private Core _registeredCore;
    private EPC_SpaceshipComponent _injectedBlock;
    private SCPrefab _injectedPrefab;
    private SCPrefab _sourcePrefab;
    private Texture2D _iconTexture;
    private bool _registered;
    private bool _availabilityRefreshed;
    private bool _menuRefreshed;
    private bool _sourceNotFoundLogged;
    private bool _menuItemNotFoundLogged;
    private bool _refreshErrorLogged;
    private bool _placedModelApplied;
    private int _refreshDelay = 120;
    private Localization _localizedInstance;
    private static readonly System.Collections.Generic.List<InjectableBlock> ActiveBlocks = new();
    private static bool _inventoryPatchApplied;

    private string PrefabName => InjectableBlockConfiguration.GetPrefabName(GetType());

    private string SourceItemName => InjectableBlockConfiguration.GetSourceItemName(GetType());

    private string DisplayName => InjectableBlockConfiguration.GetDisplayName(GetType());

    private string Description => InjectableBlockConfiguration.GetDescription(GetType());

    private string AssetBundleFileName => InjectableBlockConfiguration.GetAssetBundleFileName(GetType());

    private string AssetMeshName => InjectableBlockConfiguration.GetAssetMeshName(GetType());

    private string RawMeshFileName => InjectableBlockConfiguration.GetRawMeshFileName(GetType());

    private float RawMeshScale => InjectableBlockConfiguration.GetRawMeshScale(GetType());

    private float ModelColorRed => InjectableBlockConfiguration.GetModelColorRed(GetType());

    private float ModelColorGreen => InjectableBlockConfiguration.GetModelColorGreen(GetType());

    private float ModelColorBlue => InjectableBlockConfiguration.GetModelColorBlue(GetType());

    protected InjectableBlock()
        : base(IntPtr.Zero)
    {
        _log = Plugin.Log;
        ActiveBlocks.Add(this);
    }

    protected InjectableBlock(IntPtr pointer)
        : base(pointer)
    {
        _log = Plugin.Log;
        ActiveBlocks.Add(this);
    }

    private void Update()
    {
        try
        {
            TryInjectLocalization();

            var core = Core.Get();
            if (core == null || core._componentsMap == null || core._spaceshipComponents == null)
                return;

            if (!ReferenceEquals(_registeredCore, core))
                ResetForCore(core);

            if (!_registered)
                _registered = Register(core);

            if (_registered && !_placedModelApplied)
            {
                _placedModelApplied = TryApplyPlacedModel();
                if (_placedModelApplied)
                    _log.LogInfo($"Applied {PrefabName}'s placed model to its ECS prefab renderer.");
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
                    _log.LogInfo($"Added {PrefabName} as an independent build-menu item.");
            }
        }
        catch (Exception exception)
        {
            _log.LogError($"Could not register {PrefabName}: {exception}");
        }
    }

    private void ResetForCore(Core core)
    {
        _registeredCore = core;
        _injectedBlock = null;
        _additionalRenderers.Clear();
        _additionalRendererEntities.Clear();
        _registered = false;
        _availabilityRefreshed = false;
        _menuRefreshed = false;
        _sourceNotFoundLogged = false;
        _menuItemNotFoundLogged = false;
        _refreshErrorLogged = false;
        _placedModelApplied = false;
        _refreshDelay = 120;
        _log.LogInfo($"Detected a new game core; registering {PrefabName} beside {SourceItemName} when it is available.");
    }

    private void TryInjectLocalization()
    {
        var localization = Localization._cachedInstance;
        if (localization == null || localization._map == null || ReferenceEquals(_localizedInstance, localization))
            return;

        localization._map[PrefabName + "_Name"] = DisplayName;
        localization._map[PrefabName + "_Desc"] = Description;
        _localizedInstance = localization;
        _log.LogInfo($"Added localization for {PrefabName}.");
    }

    private bool Register(Core core)
    {
        var source = FindSourceComponent(core);
        if (source == null)
        {
            if (!_sourceNotFoundLogged)
            {
                _sourceNotFoundLogged = true;
                _log.LogInfo($"{SourceItemName} is not ready for {PrefabName} yet; registration will retry.");
            }

            return false;
        }

        _sourcePrefab = new SCPrefab(source);
        _injectedPrefab = new SCPrefab(PrefabName);
        if (_injectedPrefab == _sourcePrefab)
            throw new InvalidOperationException($"{PrefabName} collides with the {SourceItemName} prefab identity.");

        if (core._componentsMap.TryGetValue(_injectedPrefab, out var existingBlock) && existingBlock != null)
        {
            _injectedBlock = existingBlock;
            EnsureCatalogContains(core, existingBlock);
        }
        else
        {
            _injectedBlock = UnityEngine.Object.Instantiate(source);
            _injectedBlock.name = PrefabName;
            _injectedBlock.gameObject.name = PrefabName;
            _injectedBlock._iconTexture2D = _iconTexture ??= InjectableBlockConfiguration.CreateIcon(GetType());
            UnityEngine.Object.DontDestroyOnLoad(_injectedBlock.gameObject);
            _injectedBlock.gameObject.SetActive(false);
            ConfigureRendererChildren(_injectedBlock);

            core._componentsMap.Add(_injectedPrefab, _injectedBlock);
            EnsureCatalogContains(core, _injectedBlock);
            _log.LogInfo($"Registered {PrefabName} beside {SourceItemName} with its own prefab identity.");
        }

        EnsureInventoryPatchApplied();
        return true;
    }

    private EPC_SpaceshipComponent FindSourceComponent(Core core)
    {
        var components = core._spaceshipComponents;
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component != null && component.GetName() == SourceItemName)
                return component;
        }

        return null;
    }

    private static void EnsureCatalogContains(Core core, EPC_SpaceshipComponent component)
    {
        var components = core._spaceshipComponents;
        for (var index = 0; index < components.Length; index++)
        {
            if (components[index] == component)
                return;
        }

        var expandedComponents = new Il2CppReferenceArray<EPC_SpaceshipComponent>(components.Length + 1);
        for (var index = 0; index < components.Length; index++)
            expandedComponents[index] = components[index];

        expandedComponents[components.Length] = component;
        core._spaceshipComponents = expandedComponents;
    }

    private void ConfigureRendererChildren(EPC_SpaceshipComponent block)
    {
        var renderers = block.GetComponentsInChildren<EPC_Renderer>(true);
        if (renderers.Length == 0)
            throw new InvalidOperationException($"{SourceItemName} has no renderer template for {PrefabName}.");

        var mesh = GetPlacedMesh(0);
        if (mesh == null)
            throw new InvalidOperationException($"Could not load {PrefabName}'s table mesh.");

        ConfigureRenderer(renderers[0], mesh, GetMaterialColor(0));
        for (var materialGroup = 1; materialGroup < MaterialGroupCount; materialGroup++)
        {
            var groupMesh = GetPlacedMesh(materialGroup);
            if (groupMesh == null)
                continue;

            var groupObject = new GameObject($"{PrefabName}_Renderer_{materialGroup}");
            groupObject.transform.SetParent(block.transform, false);
            var groupRenderer = groupObject.AddComponent<EPC_Renderer>();
            ConfigureRenderer(groupRenderer, groupMesh, GetMaterialColor(materialGroup), renderers[0]);
            _additionalRenderers.Add(materialGroup, groupRenderer);
        }

        _log.LogInfo($"Configured {PrefabName}'s native table renderers.");
    }

    private static void ConfigureRenderer(
        EPC_Renderer renderer,
        Mesh mesh,
        SpaceshipComponentColor color,
        EPC_Renderer sourceRenderer = null)
    {
        renderer._mesh = mesh;
        renderer._submeshID = 0;
        renderer._spaceshipColor = color;
        if (sourceRenderer != null)
            renderer._material = sourceRenderer._material;
    }


    private bool RefreshAvailableComponents(Core core)
    {
        try
        {
            core.RefreshStandaloneAvailableComponents();
            _log.LogInfo($"Added {PrefabName} to the available buildable inventory components; waiting for the build menu.");
            return true;
        }
        catch (Exception exception)
        {
            if (!_refreshErrorLogged)
            {
                _refreshErrorLogged = true;
                _log.LogInfo($"Inventory is not ready for {PrefabName} yet; registration will retry. {exception.Message}");
            }

            return false;
        }
    }

    private bool TryApplyPlacedModel()
    {
        if (_injectedBlock == null || GetPlacedMesh(0) == null)
            return false;

        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return false;

        var rootEntity = EntityPrefabComponent.Get(_injectedBlock);
        return world.EntityManager.Exists(rootEntity) &&
               ApplyPlacedModelGroups(world.EntityManager, rootEntity, GetPlacedMesh(0));
    }

    private bool ApplyPlacedModelGroups(EntityManager entityManager, Entity rootEntity, Mesh firstMesh)
    {
        var rendererEntity = rootEntity;
        if (!entityManager.HasComponent<CRPRendererData>(rendererEntity))
        {
            if (!entityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
                return false;

            var linkedEntities = entityManager.GetBuffer<LinkedEntityGroup>(rootEntity, true);
            for (var index = 0; index < linkedEntities.Length; index++)
            {
                if (!entityManager.HasComponent<CRPRendererData>(linkedEntities[index].Value))
                    continue;

                rendererEntity = linkedEntities[index].Value;
                break;
            }
        }

        if (!entityManager.HasComponent<CRPRendererData>(rendererEntity) ||
            !ApplyPlacedModel(entityManager, rendererEntity, firstMesh, GetMaterialColor(0)))
            return false;

        foreach (var (materialGroup, renderer) in _additionalRenderers)
        {
            var mesh = GetPlacedMesh(materialGroup);
            if (mesh == null)
                continue;

            if (!_additionalRendererEntities.TryGetValue(materialGroup, out var rendererGroupEntity) ||
                !entityManager.Exists(rendererGroupEntity))
            {
                rendererGroupEntity = renderer.CreateLinkedGroupChild(entityManager, rootEntity);
                _additionalRendererEntities[materialGroup] = rendererGroupEntity;
            }

            if (!entityManager.HasComponent<CRPRendererData>(rendererGroupEntity) ||
                !ApplyPlacedModel(entityManager, rendererGroupEntity, mesh, GetMaterialColor(materialGroup)))
                return false;
        }

        return true;
    }

    private bool ApplyPlacedModel(EntityManager entityManager, Entity entity, Mesh mesh, SpaceshipComponentColor color)
    {
        var rendererData = entityManager.GetComponentData<CRPRendererData>(entity);
        var material = GetPlacedMaterial(rendererData._material.Managed());
        if (material == null)
            return false;

        entityManager.SetComponentData(entity, new CRPRendererData(mesh, 0, material));
        SpaceshipComponentExtensions.GetProperties(
            color,
            out var paintColor,
            out var paintSpecular);
        EPC_SpaceshipComponent.BlueprintFunctions.LoadSpaceshipComponentColorsSet(
            entityManager,
            entity,
            color, color, color, color, color, color, color, color);
        if (entityManager.HasComponent<CRPRendererMemory_Color>(entity))
        {
            entityManager.SetComponentData(entity, new CRPRendererMemory_Color(paintColor, paintSpecular));
        }

        return true;
    }

    private static SpaceshipComponentColor GetMaterialColor(int materialGroup)
        => materialGroup switch
        {
            0 => SpaceshipComponentColor.Brown,
            1 => SpaceshipComponentColor.Green,
            2 => SpaceshipComponentColor.LightGray,
            3 => SpaceshipComponentColor.Brown,
            _ => SpaceshipComponentColor.Green
        };


    private Material GetPlacedMaterial(Material sourceMaterial)
    {
        if (sourceMaterial == null)
            return null;

        if (BlockMaterialCache.TryGet(PrefabName, out var cachedMaterial))
            return cachedMaterial;

        var material = UnityEngine.Object.Instantiate(sourceMaterial);
        material.name = PrefabName + "_Material";
        var color = new Color(ModelColorRed, ModelColorGreen, ModelColorBlue, 1f);
        var supportedColorProperties = string.Empty;
        SetMaterialColorIfSupported(material, "_Color", color, ref supportedColorProperties);
        SetMaterialColorIfSupported(material, "_BaseColor", color, ref supportedColorProperties);
        SetMaterialColorIfSupported(material, "_FrameColor", color, ref supportedColorProperties);
        SetMaterialColorIfSupported(material, "_TintColor", color, ref supportedColorProperties);
        if (material.HasProperty("_Frame3DTexture"))
            material.SetTexture("_Frame3DTexture", GetPlacedTexture(color));
        _log.LogInfo($"{PrefabName} material uses '{sourceMaterial.name}' and shader '{material.shader.name}', supported color properties: {supportedColorProperties}, all properties: {GetShaderProperties(material.shader)}.");
        BlockMaterialCache.Store(PrefabName, material);
        return material;
    }

    private Texture3D GetPlacedTexture(Color color)
    {
        if (BlockTextureCache.TryGet(PrefabName, out var cachedTexture))
            return cachedTexture;

        const int size = 8;
        var pixels = new Il2CppStructArray<Color>(size * size * size);
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = color;

        var texture = new Texture3D(size, size, size, TextureFormat.RGBA32, false)
        {
            name = PrefabName + "_FeltTexture"
        };
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        BlockTextureCache.Store(PrefabName, texture);
        return texture;
    }

    private static void SetMaterialColorIfSupported(Material material, string propertyName, Color color, ref string supportedProperties)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetColor(propertyName, color);
            supportedProperties += string.IsNullOrEmpty(supportedProperties) ? propertyName : ", " + propertyName;
        }
    }

    private static string GetShaderProperties(Shader shader)
    {
        if (shader == null)
            return string.Empty;

        var properties = string.Empty;
        for (var index = 0; index < shader.GetPropertyCount(); index++)
            properties += string.IsNullOrEmpty(properties) ? shader.GetPropertyName(index) : ", " + shader.GetPropertyName(index);

        return properties;
    }

    private Mesh GetPlacedMesh(int materialGroup)
    {
        if (BlockModelCache.TryGet(PrefabName, out var cachedMeshes))
            return materialGroup < cachedMeshes.Length ? cachedMeshes[materialGroup] : null;

        if (materialGroup != 0)
        {
            GetPlacedMesh(0);
            return BlockModelCache.TryGet(PrefabName, out cachedMeshes) && materialGroup < cachedMeshes.Length
                ? cachedMeshes[materialGroup]
                : null;
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(InjectableBlock).Assembly.Location);
        var meshPath = string.IsNullOrWhiteSpace(pluginDirectory) || string.IsNullOrWhiteSpace(RawMeshFileName)
            ? null
            : Path.Combine(pluginDirectory, RawMeshFileName);
        if (!string.IsNullOrWhiteSpace(meshPath) && File.Exists(meshPath))
        {
            try
            {
                var rawMeshes = RawMeshModelLoader.TryLoadMeshes(meshPath, PrefabName + "_Mesh", RawMeshScale);
                BlockModelCache.Store(PrefabName, rawMeshes);
                _log.LogInfo($"Loaded {PrefabName}'s raw model from {RawMeshFileName}.");
                return rawMeshes[0];
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Could not load {PrefabName}'s raw model; trying the AssetBundle fallback. {exception}");
            }
        }

        var placeholderMesh = ProceduralBlockMeshFactory.CreateCube(PrefabName);
        if (placeholderMesh != null)
        {
            BlockModelCache.Store(PrefabName, placeholderMesh);
            _log.LogInfo($"Using the procedural placeholder model for {PrefabName}.");
        }

        return placeholderMesh;
    }

    private bool RefreshInventoryMenus()
    {
        var inventories = Resources.FindObjectsOfTypeAll<UIInventory>();
        if (inventories.Length == 0)
            return false;

        for (var index = 0; index < inventories.Length; index++)
        {
            var inventory = inventories[index];
            inventory.RefreshItems();
            if (EnsureInventoryRow(inventory))
                return true;
        }

        if (!_menuItemNotFoundLogged)
        {
            _menuItemNotFoundLogged = true;
            _log.LogInfo($"The {PrefabName} menu row is not ready yet; refresh will retry.");
        }

        return false;
    }

    private static void EnsureInventoryPatchApplied()
    {
        if (_inventoryPatchApplied)
            return;

        new Harmony(typeof(InjectableBlock).FullName).PatchAll(typeof(InjectableBlockInventoryRefreshPatch));
        _inventoryPatchApplied = true;
    }

    private static void RefreshInjectedInventoryRows(UIInventory inventory)
    {
        for (var index = 0; index < ActiveBlocks.Count; index++)
            ActiveBlocks[index].EnsureInventoryRow(inventory);
    }

    private bool EnsureInventoryRow(UIInventory inventory)
    {
        if (!_registered)
            return false;

        var itemMap = inventory._scPrefabToListItemMap;
        if (itemMap == null || inventory._allListItems == null)
            return false;

        if (!itemMap.TryGetValue(_injectedPrefab, out var injectedItem))
        {
            var itemPrefab = inventory._uiInventoryListItemPerfab;
            if (itemPrefab == null || _injectedBlock == null)
                return false;

            injectedItem = UnityEngine.Object.Instantiate(itemPrefab, inventory._itemsContainerContent);
            injectedItem.gameObject.name = PrefabName + "_MenuItem";
            injectedItem.SetInventoryComponent(_injectedBlock);
            inventory._allListItems.Add(injectedItem);
            itemMap.Add(_injectedPrefab, injectedItem);
        }

        if (!itemMap.TryGetValue(_sourcePrefab, out var sourceItem))
            return false;

        injectedItem.SetAvailableComponents(sourceItem._currentTextAvailableComponents);
        injectedItem.gameObject.SetActive(sourceItem.gameObject.activeSelf);
        return true;
    }

    [HarmonyPatch(typeof(UIInventory), nameof(UIInventory.RefreshItems))]
    private static class InjectableBlockInventoryRefreshPatch
    {
        [HarmonyPostfix]
        private static void Postfix(UIInventory __instance)
        {
            RefreshInjectedInventoryRows(__instance);
        }
    }
}