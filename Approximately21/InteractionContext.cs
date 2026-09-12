using Unity.Entities;

namespace Approximately21;

// Local ECS identity only; never sent over the network.
public readonly struct InteractionContext
{
    public InteractionContext(World world, Entity block)
    {
        World = world;
        Block = block;
    }

    public World World { get; }
    public Entity Block { get; }
    public bool IsValid => World != null && World.IsCreated && World.EntityManager.Exists(Block);
}