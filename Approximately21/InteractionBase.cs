using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Transforms;

namespace Approximately21;

public abstract class InteractionBase : IDisposable
{
    private const float PointerSize = 0.0225f;
    private const float SurfaceOffset = 0.004f;
    private const float PointerOffset = 0.01f;
    private const float ButtonOffset = 0.012f;

    private readonly string _prefabName;
    private readonly List<InteractionButton> _buttons = new();
    private readonly List<InteractionInstance> _interactionInstances = new();
    private readonly List<EPC_Renderer> _buttonRenderers = new();
    private readonly List<EPC_Renderer> _labelRenderers = new();
    private readonly Dictionary<InteractionButton, Mesh> _labelMarkers = new();
    private readonly List<Texture3D> _ownedTextures = new();
    private Core _configuredCore;
    private Material _disabledMaterial;
    private Material _labelMaterial;
    private Mesh _interactionQuad;
    private Mesh _pointerQuad;
    private Material _interactionSurfaceMaterial;
    private Material _pointerMaterial;
    private Material _buttonMaterial;
    private EPC_Renderer _interactionSurfaceRenderer;
    private EPC_Renderer _pointerRenderer;
    private World _cameraWorld;
    private Entity _cameraEntity;
    private bool _renderWarningLogged;
    private bool _cameraReadyLogged;
    private bool _pointerVisibleLogged;
    private bool _pointerFailureLogged;
    private int _cameraPriority = int.MinValue;
    private int _interactionDiscoveryDelay;

    protected InteractionBase(string prefabName)
    {
        _prefabName = prefabName;
    }

    protected void RegisterButton(InteractionButton button)
    {
        if (button == null)
            throw new ArgumentNullException(nameof(button));
        if (_interactionSurfaceRenderer != null)
            throw new InvalidOperationException("Buttons must be registered before interaction renderers are configured.");

        for (var index = 0; index < _buttons.Count; index++)
        {
            if (_buttons[index].Name == button.Name)
                throw new InvalidOperationException($"An interaction button named '{button.Name}' is already registered.");
        }

        _buttons.Add(button);
    }

    public bool TryConfigureAttachedComponents(
        EPC_SpaceshipComponent block,
        Core core,
        Bounds interactionBounds)
    {
        if (_configuredCore == null || core == null || _configuredCore.Pointer != core.Pointer)
        {
            ResetRendering();
            _configuredCore = core;
        }
        EnsureInteractionMeshes();
        if (_interactionSurfaceRenderer != null && _pointerRenderer != null &&
            _buttonRenderers.Count == _buttons.Count)
            return true;

        var renderers = block.GetComponentsInChildren<EPC_Renderer>(true);
        if (renderers.Length == 0)
            return false;

        _interactionSurfaceRenderer = CreateInteractionRenderer(
            block,
            renderers[0],
            _interactionQuad,
            _prefabName + "_InteractionSurfaceRenderer",
            interactionBounds.center,
            new Vector3(interactionBounds.size.x, 1f, interactionBounds.size.z));
        _pointerRenderer = CreateInteractionRenderer(
            block,
            renderers[0],
            _pointerQuad,
            _prefabName + "_PointerRenderer",
            interactionBounds.center,
            new Vector3(PointerSize, PointerSize, PointerSize));
        for (var index = 0; index < _buttons.Count; index++)
        {
            var button = _buttons[index];
            _buttonRenderers.Add(CreateInteractionRenderer(
                block,
                renderers[0],
                button.Mesh,
                $"{_prefabName}_{button.Name}ButtonRenderer",
                interactionBounds.center + button.LocalOffset,
                Vector3.one));
            var marker = CreateInteractionSurfaceMarker();
            marker.name = $"{_prefabName}_{button.Name}Label";
            _labelMarkers.Add(button, marker);
            _labelRenderers.Add(CreateInteractionRenderer(block, renderers[0], marker,
                $"{_prefabName}_{button.Name}LabelRenderer", interactionBounds.center + button.LocalOffset,
                Vector3.one));
        }

        Plugin.LogInfo(
            $"Configured {_prefabName}'s interaction geometry from tabletop bounds " +
            $"center={interactionBounds.center}, size={interactionBounds.size}.");
        return true;
    }

    public void Update(Mesh tableMesh, Bounds interactionBounds)
    {
        try
        {
            if (_interactionDiscoveryDelay-- <= 0)
            {
                _interactionDiscoveryDelay = 120;
                RefreshInteractionInstances(tableMesh);
            }

            var hasCameraRay = TryGetCameraRay(out var cameraRay);
            var primaryClickPressed = IsPrimaryClickPressed();
            InteractionInstance selected = null;
            var nearest = float.PositiveInfinity;
            foreach (var candidate in _interactionInstances)
            {
                if (!hasCameraRay || !IsInteractionInstanceValid(candidate)) continue;
                var matrix = ToMatrix(candidate.World.EntityManager.GetComponentData<LocalToWorld>(candidate.TableEntity));
                var normal = matrix.inverse.transpose.MultiplyVector(Vector3.up).normalized;
                if (TryGetSurfaceHit(cameraRay, normal, matrix.MultiplyPoint3x4(interactionBounds.center),
                        matrix, interactionBounds, out _, out var distance, out _) &&
                    InteractionGeometry.IsNearer(distance, nearest))
                {
                    nearest = distance;
                    selected = candidate;
                }
            }
            for (var index = _interactionInstances.Count - 1; index >= 0; index--)
            {
                var instance = _interactionInstances[index];
                if (!IsInteractionInstanceValid(instance))
                {
                    instance.Dispose();
                    _interactionInstances.RemoveAt(index);
                    continue;
                }

                UpdateInteractionLayer(instance, hasCameraRay, cameraRay,
                    primaryClickPressed && ReferenceEquals(instance, selected), interactionBounds);
                if (!ReferenceEquals(instance, selected))
                    SetPointerVisible(instance.World.EntityManager, instance.PointerEntity, false);
            }
        }
        catch (Exception exception)
        {
            if (_renderWarningLogged)
                return;

            _renderWarningLogged = true;
            Plugin.LogWarning($"Could not render {_prefabName}'s interaction layer: {exception}");
        }
    }

    private bool IsInteractionInstanceValid(InteractionInstance instance)
    {
        if (instance.World == null || !instance.World.IsCreated)
            return false;

        var entityManager = instance.World.EntityManager;
        if (!entityManager.Exists(instance.BlockEntity) || !entityManager.Exists(instance.TableEntity) ||
            !entityManager.HasComponent<LocalToWorld>(instance.TableEntity) || !entityManager.Exists(instance.SurfaceEntity) ||
            !entityManager.HasComponent<LocalTransform>(instance.SurfaceEntity) || !entityManager.Exists(instance.PointerEntity) ||
            !entityManager.HasComponent<LocalTransform>(instance.PointerEntity))
            return false;

        for (var index = 0; index < _buttons.Count; index++)
        {
            if (!instance.ButtonEntities.TryGetValue(_buttons[index], out var buttonEntity) ||
                !entityManager.Exists(buttonEntity) || !instance.LabelEntities.TryGetValue(_buttons[index], out var labelEntity) ||
                !entityManager.Exists(labelEntity) || !entityManager.HasComponent<LocalTransform>(buttonEntity) ||
                !entityManager.HasComponent<LocalTransform>(labelEntity))
                return false;
        }

        return true;
    }

    private void RefreshInteractionInstances(Mesh tableMesh)
    {
        var previousInstances = new List<InteractionInstance>(_interactionInstances);
        _interactionInstances.Clear();
        _cameraWorld = null;
        _cameraEntity = Entity.Null;
        _cameraPriority = int.MinValue;
        var worlds = World.All;
        for (var worldIndex = 0; worldIndex < worlds.Count; worldIndex++)
        {
            var world = worlds[worldIndex];
            if (world == null || !world.IsCreated)
                continue;

            DiscoverInteractionInstances(world, tableMesh, previousInstances);
            DiscoverCamera(world);
        }

        for (var index = 0; index < previousInstances.Count; index++)
            previousInstances[index].Dispose();
    }

    [HideFromIl2Cpp]
    private void DiscoverInteractionInstances(
        World world,
        Mesh tableMesh,
        List<InteractionInstance> previousInstances)
    {
        var entityManager = world.EntityManager;
        var requiredComponents = new Il2CppStructArray<ComponentType>(2);
        requiredComponents[0] = ComponentType.ReadOnly<CRPRendererData>();
        requiredComponents[1] = ComponentType.ReadOnly<LocalToWorld>();
        var query = entityManager.CreateEntityQuery(requiredComponents);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (var index = 0; index < entities.Length; index++)
                    ConfigureLiveInteractionRenderer(entityManager, entities[index]);
                for (var index = 0; index < entities.Length; index++)
                {
                    var tableEntity = entities[index];
                    var rendererData = entityManager.GetComponentData<CRPRendererData>(tableEntity);
                    var mesh = rendererData.GetDrawDataMeshReference()._meshReference.Managed();
                    if (mesh == null || mesh.name != tableMesh.name)
                        continue;

                    var tableRoot = GetOwningBlock(entityManager, tableEntity);
                    if (tableRoot.Equals(Entity.Null)) continue;
                    var surfaceEntity = FindInteractionEntity(entityManager, tableRoot, _interactionQuad.name, entities);
                    var pointerEntity = FindInteractionEntity(entityManager, tableRoot, _pointerQuad.name, entities);
                    if (surfaceEntity.Equals(Entity.Null) || pointerEntity.Equals(Entity.Null))
                        continue;

                    var instance = TakePreviousInteractionInstance(previousInstances, world, tableEntity) ??
                                   new InteractionInstance(world, tableEntity);
                    instance.BlockEntity = tableRoot;
                    instance.SurfaceEntity = surfaceEntity;
                    instance.PointerEntity = pointerEntity;
                    var hasAllButtons = true;
                    for (var buttonIndex = 0; buttonIndex < _buttons.Count; buttonIndex++)
                    {
                        var button = _buttons[buttonIndex];
                        var buttonEntity = FindInteractionEntity(entityManager, tableRoot, button.Mesh.name, entities);
                        var labelEntity = FindInteractionEntity(entityManager, tableRoot, _labelMarkers[button].name, entities);
                        if (buttonEntity.Equals(Entity.Null) || labelEntity.Equals(Entity.Null))
                        {
                            hasAllButtons = false;
                            break;
                        }

                        if (!instance.ButtonEntities.TryGetValue(button, out var previousButton) || !previousButton.Equals(buttonEntity) ||
                            !instance.LabelEntities.TryGetValue(button, out var previousLabel) || !previousLabel.Equals(labelEntity))
                            instance.Visuals.Remove(button);
                        instance.ButtonEntities[button] = buttonEntity;
                        instance.LabelEntities[button] = labelEntity;
                        instance.LabelFallbacks[button] = _labelMarkers[button];
                    }

                    if (!hasAllButtons)
                    {
                        instance.Dispose();
                        continue;
                    }

                    _interactionInstances.Add(instance);
                }
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

    [HideFromIl2Cpp]
    private void UpdateInteractionLayer(
        InteractionInstance instance,
        bool hasCameraRay,
        Ray cameraRay,
        bool primaryClickPressed,
        Bounds interactionBounds)
    {
        var entityManager = instance.World.EntityManager;
        var tableMatrix = ToMatrix(entityManager.GetComponentData<LocalToWorld>(instance.TableEntity));
        var surfacePoint = tableMatrix.MultiplyPoint3x4(interactionBounds.center);
        var surfaceNormal = tableMatrix.inverse.transpose.MultiplyVector(Vector3.up).normalized;
        var context = new InteractionContext(instance.World, instance.BlockEntity);
        UpdateVisuals(instance, context);
        if (!hasCameraRay)
        {
            SetPointerVisible(entityManager, instance.PointerEntity, false);
            LogPointerFailure("no rendering CRPCameraData entity was found");
            return;
        }

        if (Vector3.Dot(surfaceNormal, cameraRay.origin - surfacePoint) < 0f)
            surfaceNormal = -surfaceNormal;
        AlignInteractionSurface(entityManager, instance.TableEntity, instance.SurfaceEntity,
            surfacePoint + surfaceNormal * SurfaceOffset);
        for (var buttonIndex = 0; buttonIndex < _buttons.Count; buttonIndex++)
        {
            var button = _buttons[buttonIndex];
            var buttonCenter = tableMatrix.MultiplyPoint3x4(interactionBounds.center + button.LocalOffset);
            AlignInteractionSurface(entityManager, instance.TableEntity, instance.ButtonEntities[button],
                buttonCenter + surfaceNormal * ButtonOffset);
            AlignInteractionSurface(entityManager, instance.TableEntity, instance.LabelEntities[button],
                buttonCenter + surfaceNormal * (ButtonOffset + 0.001f));
        }

        if (!TryGetSurfaceHit(cameraRay, surfaceNormal, surfacePoint, tableMatrix, interactionBounds,
                out cameraRay, out var distance, out var localHit))
        {
            SetPointerVisible(entityManager, instance.PointerEntity, false);
            LogPointerFailure($"ECS camera axis from {cameraRay.origin} does not intersect the tabletop bounds");
            return;
        }

        var worldHit = cameraRay.GetPoint(distance) + surfaceNormal * PointerOffset;
        var rootEntity = GetParent(entityManager, instance.PointerEntity);
        var rootMatrix = !rootEntity.Equals(Entity.Null) && entityManager.HasComponent<LocalToWorld>(rootEntity)
            ? ToMatrix(entityManager.GetComponentData<LocalToWorld>(rootEntity))
            : Matrix4x4.identity;
        var pointerPosition = rootMatrix.inverse.MultiplyPoint3x4(worldHit);
        var pointerTransform = entityManager.GetComponentData<LocalTransform>(instance.PointerEntity);
        pointerTransform.Position = new float3(pointerPosition.x, pointerPosition.y, pointerPosition.z);
        pointerTransform.Scale = PointerSize;
        entityManager.SetComponentData(instance.PointerEntity, pointerTransform);
        if (!_pointerVisibleLogged)
        {
            _pointerVisibleLogged = true;
            Plugin.LogInfo($"Showing {_prefabName}'s interaction pointer at local position {pointerPosition}.");
        }

        if (!primaryClickPressed)
            return;

        for (var buttonIndex = 0; buttonIndex < _buttons.Count; buttonIndex++)
        {
            var button = _buttons[buttonIndex];
            if (!button.Contains(localHit, interactionBounds))
                continue;

            if (InteractionGeometry.CanDispatch(true, true, button.Evaluate(context).Enabled))
                button.InvokeClick(context);
            break;
        }
    }

    private static InteractionInstance TakePreviousInteractionInstance(
        List<InteractionInstance> previousInstances,
        World world,
        Entity tableEntity)
    {
        for (var index = 0; index < previousInstances.Count; index++)
        {
            var instance = previousInstances[index];
            if (!ReferenceEquals(instance.World, world) || !instance.TableEntity.Equals(tableEntity))
                continue;

            previousInstances.RemoveAt(index);
            return instance;
        }

        return null;
    }

    private bool IsPrimaryClickPressed()
    {
        var mouse = Mouse.current;
        return mouse != null && mouse.leftButton.wasPressedThisFrame;
    }

    private void UpdateVisuals(InteractionInstance instance, InteractionContext context)
    {
        var em = instance.World.EntityManager;
        foreach (var button in _buttons)
        {
            var state = button.Evaluate(context);
            var hadState = instance.Visuals.TryGetValue(button, out var previous);
            if (!hadState || previous.Enabled != state.Enabled)
                em.SetComponentData(instance.ButtonEntities[button], new CRPRendererData(button.Mesh, 0,
                    state.Enabled ? _buttonMaterial : _disabledMaterial));
            if (!hadState || previous.Text != state.Text)
            {
                var label = InteractionGlyphs.Create(button, state.Text);
                label.name = _labelMarkers[button].name;
                em.SetComponentData(instance.LabelEntities[button], new CRPRendererData(label, 0, _labelMaterial));
                if (instance.Labels.TryGetValue(button, out var old)) UnityEngine.Object.Destroy(old);
                instance.Labels[button] = label;
            }
            instance.Visuals[button] = state;
        }
    }

    public void ResetRendering()
    {
        foreach (var instance in _interactionInstances)
        {
            if (instance.World != null && instance.World.IsCreated)
            {
                var em = instance.World.EntityManager;
                var renderEntities = new List<Entity>(instance.ButtonEntities.Values);
                renderEntities.AddRange(instance.LabelEntities.Values);
                renderEntities.Add(instance.SurfaceEntity);
                renderEntities.Add(instance.PointerEntity);
                foreach (var entity in renderEntities)
                    if (em.Exists(entity) && em.HasComponent<CRPRendererData>(entity))
                        em.RemoveComponent<CRPRendererData>(entity);
            }
            instance.Dispose();
        }
        _interactionInstances.Clear();
        foreach (var renderer in _buttonRenderers)
            if (renderer != null) UnityEngine.Object.Destroy(renderer.gameObject);
        foreach (var renderer in _labelRenderers)
            if (renderer != null) UnityEngine.Object.Destroy(renderer.gameObject);
        if (_interactionSurfaceRenderer != null) UnityEngine.Object.Destroy(_interactionSurfaceRenderer.gameObject);
        if (_pointerRenderer != null) UnityEngine.Object.Destroy(_pointerRenderer.gameObject);
        _buttonRenderers.Clear();
        _labelRenderers.Clear();
        foreach (var mesh in _labelMarkers.Values) UnityEngine.Object.Destroy(mesh);
        _labelMarkers.Clear();
        foreach (var button in _buttons) button.ReleaseMesh();
        if (_interactionQuad != null) UnityEngine.Object.Destroy(_interactionQuad);
        if (_pointerQuad != null) UnityEngine.Object.Destroy(_pointerQuad);
        foreach (var material in new[] { _interactionSurfaceMaterial, _pointerMaterial, _buttonMaterial, _disabledMaterial, _labelMaterial })
            if (material != null) UnityEngine.Object.Destroy(material);
        foreach (var texture in _ownedTextures) UnityEngine.Object.Destroy(texture);
        _ownedTextures.Clear();
        _interactionQuad = _pointerQuad = null;
        _interactionSurfaceMaterial = _pointerMaterial = _buttonMaterial = _disabledMaterial = _labelMaterial = null;
        _interactionSurfaceRenderer = _pointerRenderer = null;
        _configuredCore = null;
        _cameraWorld = null;
        _cameraEntity = Entity.Null;
        _cameraPriority = int.MinValue;
        _interactionDiscoveryDelay = 0;
        _renderWarningLogged = _cameraReadyLogged = _pointerVisibleLogged = _pointerFailureLogged = false;
    }

    public virtual void Dispose() => ResetRendering();

    private void LogPointerFailure(string reason)
    {
        if (_pointerFailureLogged)
            return;

        _pointerFailureLogged = true;
        Plugin.LogInfo($"Hiding {_prefabName}'s interaction pointer because {reason}.");
    }

    private void ConfigureLiveInteractionRenderer(EntityManager entityManager, Entity entity)
    {
        var rendererData = entityManager.GetComponentData<CRPRendererData>(entity);
        var mesh = rendererData.GetDrawDataMeshReference()._meshReference.Managed();
        if (mesh == null || !IsInteractionMesh(mesh))
            return;

        var currentMaterial = rendererData._material.Managed();
        EnsureInteractionRendering(currentMaterial);
        if (mesh.name != _pointerQuad.name && mesh.name != _interactionQuad.name)
            return;
        var material = mesh.name == _pointerQuad.name
            ? _pointerMaterial
            : mesh.name == _interactionQuad.name
                ? _interactionSurfaceMaterial
                : _buttonMaterial;
        if (material != null && (currentMaterial == null || currentMaterial.name != material.name))
            entityManager.SetComponentData(entity, new CRPRendererData(mesh, 0, material));
    }

    private bool IsInteractionMesh(Mesh mesh)
    {
        if (mesh.name == _interactionQuad.name || mesh.name == _pointerQuad.name)
            return true;

        for (var index = 0; index < _buttons.Count; index++)
        {
            if (mesh.name == _buttons[index].Mesh.name)
                return true;
        }

        return false;
    }

    private void AlignInteractionSurface(
        EntityManager entityManager,
        Entity tableEntity,
        Entity surfaceEntity,
        Vector3 worldCenter)
    {
        if (!entityManager.HasComponent<LocalTransform>(surfaceEntity))
            return;

        var tableMatrix = ToMatrix(entityManager.GetComponentData<LocalToWorld>(tableEntity));
        var rootEntity = GetParent(entityManager, surfaceEntity);
        var rootMatrix = !rootEntity.Equals(Entity.Null) && entityManager.HasComponent<LocalToWorld>(rootEntity)
            ? ToMatrix(entityManager.GetComponentData<LocalToWorld>(rootEntity))
            : Matrix4x4.identity;
        var localCenter = rootMatrix.inverse.MultiplyPoint3x4(worldCenter);
        var surfaceTransform = entityManager.GetComponentData<LocalTransform>(surfaceEntity);
        surfaceTransform.Position = new float3(localCenter.x, localCenter.y, localCenter.z);
        surfaceTransform.Rotation = quaternion.identity;
        surfaceTransform.Scale = 1f;
        entityManager.SetComponentData(surfaceEntity, surfaceTransform);
        var basis = rootMatrix.inverse * tableMatrix;
        var postTransform = new PostTransformMatrix
        {
            Value = new float4x4(
                new float4(basis.m00, basis.m10, basis.m20, 0),
                new float4(basis.m01, basis.m11, basis.m21, 0),
                new float4(basis.m02, basis.m12, basis.m22, 0),
                new float4(0, 0, 0, 1))
        };
        if (entityManager.HasComponent<PostTransformMatrix>(surfaceEntity))
            entityManager.SetComponentData(surfaceEntity, postTransform);
        else
            entityManager.AddComponentData(surfaceEntity, postTransform);
    }

    private Entity FindInteractionEntity(
        EntityManager entityManager,
        Entity tableRoot,
        string meshName,
        NativeArray<Entity> entities)
    {
        for (var index = 0; index < entities.Length; index++)
        {
            var entity = entities[index];
            if (!entityManager.HasComponent<LocalTransform>(entity))
                continue;

            var rendererData = entityManager.GetComponentData<CRPRendererData>(entity);
            var mesh = rendererData.GetDrawDataMeshReference()._meshReference.Managed();
            if (mesh != null && mesh.name == meshName &&
                GetOwningBlock(entityManager, entity).Equals(tableRoot))
                return entity;
        }

        return Entity.Null;
    }

    private void DiscoverCamera(World world)
    {
        var entityManager = world.EntityManager;
        var requiredComponents = new Il2CppStructArray<ComponentType>(2);
        requiredComponents[0] = ComponentType.ReadOnly<CRPCameraData>();
        requiredComponents[1] = ComponentType.ReadOnly<LocalToWorld>();
        var query = entityManager.CreateEntityQuery(requiredComponents);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (var index = 0; index < entities.Length; index++)
                {
                    var cameraData = entityManager.GetComponentData<CRPCameraData>(entities[index]);
                    var priority = cameraData._priority + (cameraData._rendering ? 1_000_000 : 0);
                    if (priority <= _cameraPriority)
                        continue;

                    _cameraPriority = priority;
                    _cameraWorld = world;
                    _cameraEntity = entities[index];
                }
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

    private bool TryGetCameraRay(out Ray cameraRay)
    {
        cameraRay = default;
        if (_cameraWorld == null || !_cameraWorld.IsCreated ||
            _cameraEntity.Equals(Entity.Null) || !_cameraWorld.EntityManager.Exists(_cameraEntity))
            return false;

        var cameraMatrix = ToMatrix(_cameraWorld.EntityManager.GetComponentData<LocalToWorld>(_cameraEntity));
        var positionColumn = cameraMatrix.GetColumn(3);
        var forwardColumn = cameraMatrix.GetColumn(2);
        cameraRay = new Ray(
            new Vector3(positionColumn.x, positionColumn.y, positionColumn.z),
            new Vector3(forwardColumn.x, forwardColumn.y, forwardColumn.z).normalized);
        if (!_cameraReadyLogged)
        {
            _cameraReadyLogged = true;
            Plugin.LogInfo(
                $"Using CRP camera entity {_cameraEntity.Index} for {_prefabName}'s interaction pointer " +
                $"at {cameraRay.origin} with axis {cameraRay.direction}.");
        }

        return cameraRay.direction.sqrMagnitude > 0f;
    }

    private static bool TryGetSurfaceHit(
        Ray cameraAxis,
        Vector3 surfaceNormal,
        Vector3 surfacePoint,
        Matrix4x4 tableMatrix,
        Bounds interactionBounds,
        out Ray cameraRay,
        out float distance,
        out Vector3 localHit)
    {
        var surfacePlane = new Plane(surfaceNormal, surfacePoint);
        cameraRay = cameraAxis;
        if (surfacePlane.Raycast(cameraRay, out distance))
        {
            localHit = tableMatrix.inverse.MultiplyPoint3x4(cameraRay.GetPoint(distance));
            if (localHit.x >= interactionBounds.min.x && localHit.x <= interactionBounds.max.x &&
                localHit.z >= interactionBounds.min.z && localHit.z <= interactionBounds.max.z)
                return true;
        }

        distance = 0f;
        localHit = default;
        return false;
    }

    private Entity GetOwningBlock(EntityManager entityManager, Entity entity)
    {
        var prefab = new SCPrefab(_prefabName);
        return InteractionGeometry.FindOwner(entity, Entity.Null, e => entityManager.Exists(e),
            e => entityManager.HasComponent<SCPrefab>(e) && entityManager.HasComponent<NetcoreEntity>(e) &&
                 entityManager.GetComponentData<SCPrefab>(e)._prefab == prefab._prefab,
            e => GetParent(entityManager, e));
    }

    private static Entity GetParent(EntityManager entityManager, Entity entity) =>
        entityManager.HasComponent<Parent>(entity) ? entityManager.GetComponentData<Parent>(entity).Value : Entity.Null;

    private static void SetPointerVisible(EntityManager entityManager, Entity pointerEntity, bool visible)
    {
        var pointerTransform = entityManager.GetComponentData<LocalTransform>(pointerEntity);
        var scale = visible ? PointerSize : 0f;
        if (Mathf.Approximately(pointerTransform.Scale, scale))
            return;

        pointerTransform.Scale = scale;
        entityManager.SetComponentData(pointerEntity, pointerTransform);
    }

    private void EnsureInteractionRendering(Material sourceMaterial)
    {
        EnsureInteractionMeshes();
        if ((_interactionSurfaceMaterial != null && _pointerMaterial != null && _buttonMaterial != null) ||
            sourceMaterial == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        _interactionSurfaceMaterial = shader != null ? new Material(shader) : new Material(sourceMaterial);
        _interactionSurfaceMaterial.name = _prefabName + "_InteractionSurfaceMaterial";
        ConfigureMaterial(_interactionSurfaceMaterial, new Color(1f, 1f, 1f, 0f), true);

        _pointerMaterial = shader != null ? new Material(shader) : new Material(sourceMaterial);
        _pointerMaterial.name = _prefabName + "_PointerMaterial";
        ConfigureMaterial(_pointerMaterial, Color.red, false);
        _pointerMaterial.renderQueue = 3100;

        _buttonMaterial = shader != null ? new Material(shader) : new Material(sourceMaterial);
        _buttonMaterial.name = _prefabName + "_ButtonMaterial";
        ConfigureMaterial(_buttonMaterial, new Color(0.12f, 0.35f, 0.9f, 1f), false);
        _buttonMaterial.renderQueue = 3050;
        _disabledMaterial = new Material(_buttonMaterial) { name = _prefabName + "_DisabledMaterial" };
        ConfigureMaterial(_disabledMaterial, new Color(0.16f, 0.16f, 0.16f, 1f), false);
        _labelMaterial = new Material(_buttonMaterial) { name = _prefabName + "_LabelMaterial" };
        ConfigureMaterial(_labelMaterial, Color.white, false);
        _labelMaterial.renderQueue = 3060;
    }

    private void EnsureInteractionMeshes()
    {
        if (_interactionQuad == null)
            _interactionQuad = CreateInteractionSurfaceMarker();
        if (_pointerQuad == null)
        {
            _pointerQuad = CreateInteractionQuad();
            _pointerQuad.name = _prefabName + "_PointerQuad";
        }

        for (var index = 0; index < _buttons.Count; index++)
            _buttons[index].EnsureMesh(_prefabName);
    }

    private static EPC_Renderer CreateInteractionRenderer(
        EPC_SpaceshipComponent block,
        EPC_Renderer sourceRenderer,
        Mesh mesh,
        string name,
        Vector3 localPosition,
        Vector3 localScale)
    {
        var rendererObject = new GameObject(name);
        rendererObject.transform.SetParent(block.transform, false);
        rendererObject.transform.localPosition = localPosition;
        rendererObject.transform.localScale = localScale;
        var renderer = rendererObject.AddComponent<EPC_Renderer>();
        renderer._mesh = mesh;
        renderer._submeshID = 0;
        renderer._spaceshipColor = SpaceshipComponentColor.LightGray;
        renderer._material = sourceRenderer._material;
        return renderer;
    }

    private void ConfigureMaterial(Material material, Color color, bool transparent)
    {
        SetColorIfSupported(material, "_Color", color);
        SetColorIfSupported(material, "_BaseColor", color);
        SetColorIfSupported(material, "_FrameColor", color);
        SetColorIfSupported(material, "_TintColor", color);
        if (material.HasProperty("_Frame3DTexture"))
        {
            var texture = CreateSolidTexture(color);
            _ownedTextures.Add(texture);
            material.SetTexture("_Frame3DTexture", texture);
        }

        if (!transparent)
            return;

        material.SetInt("_Surface", 1);
        material.SetInt("_SrcBlend", 5);
        material.SetInt("_DstBlend", 10);
        material.SetInt("_ZWrite", 0);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = 3000;
    }

    private static void SetColorIfSupported(Material material, string propertyName, Color color)
    {
        if (material.HasProperty(propertyName))
            material.SetColor(propertyName, color);
    }

    private static Texture3D CreateSolidTexture(Color color)
    {
        var texture = new Texture3D(1, 1, 1, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Il2CppStructArray<Color>(1);
        pixels[0] = color;
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static Mesh CreateInteractionQuad()
    {
        var vertices = new Il2CppStructArray<Vector3>(4);
        vertices[0] = new Vector3(-0.5f, 0f, -0.5f);
        vertices[1] = new Vector3(-0.5f, 0f, 0.5f);
        vertices[2] = new Vector3(0.5f, 0f, 0.5f);
        vertices[3] = new Vector3(0.5f, 0f, -0.5f);
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

        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private Mesh CreateInteractionSurfaceMarker()
    {
        var mesh = CreateInteractionQuad();
        mesh.name = _prefabName + "_InteractionQuad";
        mesh.SetVertices(new Il2CppStructArray<Vector3>(4));
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Matrix4x4 ToMatrix(LocalToWorld localToWorld)
    {
        var value = localToWorld.Value;
        return new Matrix4x4(
            new Vector4(value.c0.x, value.c0.y, value.c0.z, value.c0.w),
            new Vector4(value.c1.x, value.c1.y, value.c1.z, value.c1.w),
            new Vector4(value.c2.x, value.c2.y, value.c2.z, value.c2.w),
            new Vector4(value.c3.x, value.c3.y, value.c3.z, value.c3.w));
    }

    private sealed class InteractionInstance
    {
        internal readonly World World;
        internal readonly Entity TableEntity;
        internal readonly Dictionary<InteractionButton, Entity> ButtonEntities = new();
        internal readonly Dictionary<InteractionButton, Entity> LabelEntities = new();
        internal readonly Dictionary<InteractionButton, InteractionVisualState> Visuals = new();
        internal readonly Dictionary<InteractionButton, Mesh> Labels = new();
        internal readonly Dictionary<InteractionButton, Mesh> LabelFallbacks = new();
        internal Entity BlockEntity;
        internal Entity SurfaceEntity;
        internal Entity PointerEntity;

        internal InteractionInstance(World world, Entity tableEntity)
        {
            World = world;
            TableEntity = tableEntity;
        }

        internal void Dispose()
        {
            if (World != null && World.IsCreated)
            {
                var em = World.EntityManager;
                if (em.Exists(PointerEntity) && em.HasComponent<LocalTransform>(PointerEntity))
                    SetPointerVisible(em, PointerEntity, false);
                // Detach owned meshes before releasing them, including during temporary renderer loss.
                foreach (var pair in LabelEntities)
                    if (em.Exists(pair.Value) && em.HasComponent<CRPRendererData>(pair.Value) && LabelFallbacks.TryGetValue(pair.Key, out var marker))
                    {
                        var material = em.GetComponentData<CRPRendererData>(pair.Value)._material.Managed();
                        em.SetComponentData(pair.Value, new CRPRendererData(marker, 0, material));
                    }
            }
            foreach (var mesh in Labels.Values) UnityEngine.Object.Destroy(mesh);
            Labels.Clear();
            LabelFallbacks.Clear();
            Visuals.Clear();
            LabelEntities.Clear();
            ButtonEntities.Clear();
        }
    }
}
