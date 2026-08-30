using System;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Collections.Generic;
using UnityEngine;

namespace Approximately21;

public sealed class CheckerboardBlockCatalogRegistrar : MonoBehaviour
{
    private const string BlockName = "Approximately21_CheckerboardBlock";
    private const string SourceItemName = "Frame Quarter";
    private const string BlockDisplayName = "Checkerboard Block";
    private const string BlockDescription = "A pink-and-black checkerboard test block.";

    private readonly ManualLogSource _log;
    private Core _registeredCore;
    private EPC_SpaceshipComponent _checkerboardBlock;
    private SCPrefab _checkerboardPrefab;
    private bool _registered;
    private bool _availabilityRefreshed;
    private bool _menuRefreshed;
    private bool _sourceNotFoundLogged;
    private bool _menuItemNotFoundLogged;
    private bool _refreshErrorLogged;
    private int _refreshDelay = 120;
    private SCPrefab _sourcePrefab;
    private Localization _localizedInstance;
    private static Texture2D _checkerboardTexture;
    private static CheckerboardBlockCatalogRegistrar _activeRegistrar;
    private static bool _inventoryPatchApplied;

    public CheckerboardBlockCatalogRegistrar(IntPtr pointer)
        : base(pointer)
    {
        _log = Plugin.Log;
        _activeRegistrar = this;
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

            if (!_registered || _refreshDelay-- > 0)
                return;

            _refreshDelay = 120;
            if (!_availabilityRefreshed)
                _availabilityRefreshed = RefreshAvailableComponents(core);

            if (_availabilityRefreshed && !_menuRefreshed)
            {
                _menuRefreshed = RefreshInventoryMenus();
                if (_menuRefreshed)
                    _log.LogInfo($"Added {BlockName} as an independent build-menu item.");
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
        _checkerboardBlock = null;
        _registered = false;
        _availabilityRefreshed = false;
        _menuRefreshed = false;
        _sourceNotFoundLogged = false;
        _menuItemNotFoundLogged = false;
        _refreshErrorLogged = false;
        _refreshDelay = 120;
        _log.LogInfo($"Detected a new game core; registering {BlockName} beside {SourceItemName} when it is available.");
    }

    private void TryInjectLocalization()
    {
        var localization = Localization._cachedInstance;
        if (localization == null || localization._map == null || ReferenceEquals(_localizedInstance, localization))
            return;

        localization._map[BlockName + "_Name"] = BlockDisplayName;
        localization._map[BlockName + "_Desc"] = BlockDescription;
        _localizedInstance = localization;
        _log.LogInfo($"Added localization for {BlockName}.");
    }

    private bool Register(Core core)
    {
        _checkerboardPrefab = new SCPrefab(BlockName);
        if (core._componentsMap.TryGetValue(_checkerboardPrefab, out var existingBlock) && existingBlock != null)
        {
            _checkerboardBlock = existingBlock;
            EnsureCatalogContains(core, existingBlock);
            return true;
        }

        var source = FindSourceComponent(core);
        if (source == null)
        {
            if (!_sourceNotFoundLogged)
            {
                _sourceNotFoundLogged = true;
                _log.LogInfo($"{SourceItemName} is not ready for {BlockName} yet; registration will retry.");
            }

            return false;
        }

        var sourcePrefab = new SCPrefab(source);
        _sourcePrefab = sourcePrefab;
        if (_checkerboardPrefab == sourcePrefab)
            throw new InvalidOperationException($"{BlockName} collides with the {SourceItemName} prefab identity.");

        var checkerboardBlock = UnityEngine.Object.Instantiate(source);
        checkerboardBlock.name = BlockName;
        checkerboardBlock.gameObject.name = BlockName;
        checkerboardBlock._iconTexture2D = _checkerboardTexture ??= CreateCheckerboardTexture();
        UnityEngine.Object.DontDestroyOnLoad(checkerboardBlock.gameObject);
        checkerboardBlock.gameObject.SetActive(false);

        core._componentsMap.Add(_checkerboardPrefab, checkerboardBlock);
        EnsureCatalogContains(core, checkerboardBlock);
        _checkerboardBlock = checkerboardBlock;
        EnsureInventoryPatchApplied();

        _log.LogInfo($"Registered {BlockName} beside {SourceItemName} with its own prefab identity.");
        return true;
    }

    private static EPC_SpaceshipComponent FindSourceComponent(Core core)
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
            if (!_refreshErrorLogged)
            {
                _refreshErrorLogged = true;
                _log.LogInfo($"Inventory is not ready for {BlockName} yet; registration will retry. {exception.Message}");
            }

            return false;
        }
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
            _log.LogInfo($"The {BlockName} menu row is not ready yet; refresh will retry.");
        }

        return false;
    }

    private static void EnsureInventoryPatchApplied()
    {
        if (_inventoryPatchApplied)
            return;

        new Harmony(BlockName + ".Catalog").PatchAll(typeof(CheckerboardBlockInventoryRefreshPatch));
        _inventoryPatchApplied = true;
    }

    internal static void RefreshInjectedInventoryRow(UIInventory inventory)
    {
        var registrar = _activeRegistrar;
        if (registrar == null || !registrar._registered)
            return;

        registrar.EnsureInventoryRow(inventory);
    }

    private bool EnsureInventoryRow(UIInventory inventory)
    {
        var itemMap = inventory._scPrefabToListItemMap;
        if (itemMap == null || inventory._allListItems == null)
            return false;

        if (!itemMap.TryGetValue(_checkerboardPrefab, out var checkerboardItem))
        {
            var itemPrefab = inventory._uiInventoryListItemPerfab;
            if (itemPrefab == null || _checkerboardBlock == null)
                return false;

            checkerboardItem = UnityEngine.Object.Instantiate(itemPrefab, inventory._itemsContainerContent);
            checkerboardItem.gameObject.name = BlockName + "_MenuItem";
            checkerboardItem.SetInventoryComponent(_checkerboardBlock);
            inventory._allListItems.Add(checkerboardItem);
            itemMap.Add(_checkerboardPrefab, checkerboardItem);
        }

        if (!itemMap.TryGetValue(_sourcePrefab, out var sourceItem))
            return false;

        checkerboardItem.SetAvailableComponents(sourceItem._currentTextAvailableComponents);
        checkerboardItem.gameObject.SetActive(sourceItem.gameObject.activeSelf);
        return true;
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
}

[HarmonyPatch(typeof(UIInventory), nameof(UIInventory.RefreshItems))]
internal static class CheckerboardBlockInventoryRefreshPatch
{
    [HarmonyPostfix]
    private static void Postfix(UIInventory __instance)
    {
        CheckerboardBlockCatalogRegistrar.RefreshInjectedInventoryRow(__instance);
    }
}