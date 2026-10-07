using ModernUO.CodeGeneratedEvents;

namespace Server.Engines.Spawners;

public static partial class SpawnerEvents
{
    /// <summary>
    /// A creature spawned by <paramref name="spawner"/> died, raised from <see cref="BaseSpawner.NotifySpawnedDeath"/>
    /// while the creature is still linked to its spawner. Lets other spawners react to deaths they do not own.
    /// </summary>
    [GeneratedEvent(nameof(SpawnedDeathEvent))]
    public static partial void SpawnedDeathEvent(BaseSpawner spawner, ISpawnable spawned, Mobile killer);
}
