using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Entities;

namespace Approximately21.Networking.Unity;

public sealed class BlackjackTableRegistry : IDisposable
{
    private readonly World _world;
    private readonly BlackjackStateService _service;
    private readonly BlackjackTableBindings<Entity> _bindings;
    private readonly HashSet<Entity> _tracked = new();
    private readonly SCPrefab _prefab = new("Approximately21_BlackjackTable");

    public BlackjackTableRegistry(World world, BlackjackStateService service)
    {
        _world = world;
        _service = service;
        _bindings = new BlackjackTableBindings<Entity>(service);
        _bindings.RegistrationFailed += Plugin.LogWarning;
        service.SessionReset += Clear;
    }

    public bool TryResolve(World world, Entity block, out string tableId)
    {
        tableId = null;
        return world != null && world.Pointer == _world.Pointer && world.IsCreated &&
               world.EntityManager.Exists(block) && _bindings.TryResolve(block, out tableId);
    }

    public void Pump()
    {
        if (!_world.IsCreated)
            return;
        var em = _world.EntityManager;
        foreach (var entity in _tracked.ToArray())
        {
            if (em.Exists(entity))
                continue;
            _bindings.ConfirmDestroyed(entity);
            _tracked.Remove(entity);
        }
        var components = new Il2CppStructArray<ComponentType>(2);
        components[0] = ComponentType.ReadOnly<SCPrefab>();
        components[1] = ComponentType.ReadOnly<NetcoreEntity>();
        var query = em.CreateEntityQuery(components);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (em.GetComponentData<SCPrefab>(entity)._prefab != _prefab._prefab)
                        continue;
                    var native = em.GetComponentData<NetcoreEntity>(entity);
                    if (native._id == NetcoreEntity.Null._id)
                        continue;
                    if (_tracked.Add(entity))
                        Plugin.LogInfo($"Blackjack block world={_world.Name} entity={entity.Index}:{entity.Version} native={native._id}");
                    _bindings.Observe(entity, native._id);
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }

    private void Clear() => _tracked.Clear();

    public void Dispose()
    {
        _service.SessionReset -= Clear;
        _bindings.RegistrationFailed -= Plugin.LogWarning;
        _bindings.Dispose();
        Clear();
    }
}