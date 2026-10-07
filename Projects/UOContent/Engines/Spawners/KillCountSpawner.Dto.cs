using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Server.Json;

namespace Server.Engines.Spawners;

public partial class KillCountSpawner
{
    // The running kill count is world state, not spawn data, so it is not exported.
    public override SpawnerDto ToDto()
    {
        var homeRange = DtoHomeRange;
        return new KillCountSpawnerDto
        {
            Guid = Guid,
            Name = DtoName,
            Location = Location,
            Map = Map,
            Count = Count,
            MinDelay = MinDelay,
            MaxDelay = MaxDelay,
            Team = Team,
            WalkingRange = DtoWalkingRange,
            Entries = EntryList ?? [],
            SpawnLocationIsHome = SpawnLocationIsHome,
            Group = Group,
            SpawnPositionMode = DtoSpawnPositionMode,
            MaxSpawnAttempts = DtoMaxSpawnAttempts,
            HomeRange = homeRange,
            SpawnBounds = homeRange >= 0 ? default : SpawnBounds,
            CountSpawners = _countedSpawners == null ? null : [.._countedSpawners],
            KillsRequired = KillsRequired,
            BossTimeout = BossTimeout,
            KillDecay = KillDecay
        };
    }
}

[JsonDiscoverableType("KillCountSpawner")]
public sealed record KillCountSpawnerDto : SpawnerDto
{
    [JsonPropertyName("spawnBounds")]
    [JsonPropertyOrder(8)]
    public Rectangle3D SpawnBounds { get; init; }

    [JsonPropertyName("entries")]
    [JsonPropertyOrder(10)]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<SpawnerEntry> Entries { get; init; }

    /// <summary>GUIDs of the spawners whose creatures' deaths are counted.</summary>
    [JsonPropertyName("countSpawners")]
    [JsonPropertyOrder(30)]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Guid> CountSpawners { get; init; }

    [JsonPropertyName("killsRequired")]
    [JsonPropertyOrder(31)]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int KillsRequired { get; init; }

    /// <summary>Time without combat after which the spawned creatures are removed; omitted or zero is off.</summary>
    [JsonPropertyName("bossTimeout")]
    [JsonPropertyOrder(32)]
    public TimeSpan BossTimeout { get; init; }

    /// <summary>Time without a counted kill after which the count starts to drop; omitted or zero is off.</summary>
    [JsonPropertyName("killDecay")]
    [JsonPropertyOrder(33)]
    public TimeSpan KillDecay { get; init; }

    [JsonIgnore]
    public override IReadOnlyList<SpawnerEntry> EntryView => Entries;

    protected override BaseSpawner CreateEmpty() => new KillCountSpawner();

    public override BaseSpawner ToSpawner()
    {
        var spawner = (KillCountSpawner)base.ToSpawner();
        try
        {
            if (SpawnBounds != default)
            {
                spawner.SpawnBounds = SpawnBounds;
            }

            spawner.SetCountedSpawners(CountSpawners);
            spawner.KillsRequired = KillsRequired;
            spawner.BossTimeout = BossTimeout > TimeSpan.Zero ? BossTimeout : TimeSpan.Zero;
            spawner.KillDecay = KillDecay > TimeSpan.Zero ? KillDecay : TimeSpan.Zero;
            return spawner;
        }
        catch
        {
            spawner.Delete();
            throw;
        }
    }
}
