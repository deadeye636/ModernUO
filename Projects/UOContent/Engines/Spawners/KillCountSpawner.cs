using System;
using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using ModernUO.Serialization;
using Server.Collections;
using Server.Mobiles;

namespace Server.Engines.Spawners;

/// <summary>
/// Spawns its entries only after <see cref="KillsRequired"/> creatures of the counted spawners have died,
/// in the manner of a mini champion (e.g. a named boss among a field of undead). The spawn timer never
/// runs: kills count only while this spawner is not full, the counter starts again from zero when the
/// spawn happens, so a new cycle begins once the spawned creatures are gone.
/// <para>
/// <see cref="BossTimeout"/>: spawned creatures that have not been in combat for that long are removed, and
/// the count starts again from zero. <see cref="KillDecay"/>: once that long has passed without a counted
/// kill, the count drops by <see cref="KillDecayStep"/>, and again after every further such interval, but
/// not while anything of this spawner is out. Zero disables either. Both deadlines are serialized timers,
/// so a world load resumes them with the delay that was left at save; server downtime does not count.
/// </para>
/// </summary>
[SerializationGenerator(1)]
public partial class KillCountSpawner : Spawner
{
    // Combat is sampled rather than hooked, because the spawned creature can be any class. An aggressor
    // entry lives for AggressorInfo.ExpireDelay (2 minutes by default) after the last harmful act, so
    // sampling at half that, at most once a minute, sees every fight.
    private static readonly TimeSpan CombatCheckInterval = TimeSpan.FromMinutes(1);

    // Floor for the sample, so a tiny BossTimeout set through [props cannot sample on every tick.
    private static readonly TimeSpan MinCombatCheckInterval = TimeSpan.FromSeconds(5);

    // Live instances, so a creature death finds its counters without scanning the world. Single-threaded.
    private static readonly HashSet<KillCountSpawner> _instances = [];

    private TimerExecutionToken _combatCheckToken;

    [SerializableField(0, getter: "private", setter: "private")]
    private List<Guid> _countedSpawners;

    [SerializableField(1)]
    [SerializedCommandProperty(AccessLevel.Developer)]
    [InvalidateProperties]
    private int _killsRequired;

    [SerializableField(2, fieldChanged: nameof(OnKillCountChanged))]
    [SerializedCommandProperty(AccessLevel.Developer)]
    [InvalidateProperties]
    private int _killCount;

    /// <summary>Time without combat after which the spawned creatures are removed; zero disables it.</summary>
    [SerializableField(3, fieldChanged: nameof(OnBossTimeoutChanged))]
    [SerializedCommandProperty(AccessLevel.Developer)]
    private TimeSpan _bossTimeout;

    /// <summary>Time without a counted kill after which the count starts to drop; zero disables it.</summary>
    [SerializableField(4, fieldChanged: nameof(OnKillDecayChanged))]
    [SerializedCommandProperty(AccessLevel.Developer)]
    private TimeSpan _killDecay;

    [SerializableField(5, getter: "private", setter: "private")]
    [DeserializeTimer(nameof(DeserializeBossTimeoutTimer))]
    private Timer _bossTimeoutTimer;

    [SerializableField(6, getter: "private", setter: "private")]
    [DeserializeTimer(nameof(DeserializeKillDecayTimer))]
    private Timer _killDecayTimer;

    [Constructible(AccessLevel.Developer)]
    public KillCountSpawner()
    {
        _instances.Add(this);
    }

    [Constructible(AccessLevel.Developer)]
    public KillCountSpawner(string spawnedName) : base(spawnedName)
    {
        _instances.Add(this);
    }

    public KillCountSpawner(
        int amount,
        int killsRequired,
        IReadOnlyList<Guid> countedSpawners,
        params ReadOnlySpan<string> spawnedNames
    ) : base(amount, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, spawnedNames)
    {
        _killsRequired = killsRequired;
        SetCountedSpawners(countedSpawners);
        _instances.Add(this);
    }

    public override string DefaultName => "Kill Count Spawner";

    /// <summary>GUIDs of the spawners whose creatures' deaths are counted.</summary>
    public IReadOnlyList<Guid> CountedSpawnerGuids =>
        _countedSpawners ?? (IReadOnlyList<Guid>)Array.Empty<Guid>();

    [CommandProperty(AccessLevel.Developer)]
    public int CountedSpawnerCount => _countedSpawners?.Count ?? 0;

    /// <summary>How far one decay interval lowers the count: a tenth of <see cref="KillsRequired"/>, at least 1.</summary>
    [CommandProperty(AccessLevel.Developer)]
    public int KillDecayStep => Math.Max(1, _killsRequired / 10);

    /// <summary>Time left until the spawned creatures are removed for lack of combat, null when not pending.</summary>
    internal TimeSpan? BossTimeoutRemaining =>
        _bossTimeoutTimer?.Running == true ? _bossTimeoutTimer.Next - Core.Now : null;

    /// <summary>Time left until the next decay step, null when not pending.</summary>
    internal TimeSpan? KillDecayRemaining =>
        _killDecayTimer?.Running == true ? _killDecayTimer.Next - Core.Now : null;

    public void SetCountedSpawners(IReadOnlyList<Guid> guids)
    {
        if (guids == null || guids.Count == 0)
        {
            CountedSpawners = null;
            return;
        }

        var list = new List<Guid>(guids.Count);
        for (var i = 0; i < guids.Count; i++)
        {
            if (guids[i] != Guid.Empty && guids[i] != Guid && !list.Contains(guids[i]))
            {
                list.Add(guids[i]);
            }
        }

        CountedSpawners = list.Count > 0 ? list : null;
    }

    /// <summary>Number of live counters, for tests and diagnostics.</summary>
    public static int InstanceCount => _instances.Count;

    public bool Counts(BaseSpawner spawner) =>
        spawner != null && spawner != this && _countedSpawners?.Contains(spawner.Guid) == true;

    // The timer is never armed; spawning is driven by the kill count alone.
    public override void DoTimer(TimeSpan delay)
    {
        if (Running)
        {
            End = Core.Now;
        }
    }

    // Import and [Respawn call this: clear what is out, but do not hand out the creature for free.
    public override void Respawn()
    {
        StopBossTimeout();
        RemoveSpawns();
    }

    /// <summary>Counts one death of a creature from a counted spawner; spawns when the threshold is reached.</summary>
    public void RegisterKill()
    {
        if (Deleted || !Running || Map == null || Map == Map.Internal || _killsRequired <= 0)
        {
            return;
        }

        Defrag();

        if (IsFull)
        {
            return;
        }

        KillCount++;
        // A counted kill starts the idle time over.
        StartKillDecay();

        if (_killCount < _killsRequired)
        {
            return;
        }

        for (var i = Spawned.Count; i < Count; i++)
        {
            Spawn();
        }

        // A spawn that found no valid position retries on the next counted kill instead of costing a full cycle.
        KillCount = Spawned.Count > 0 ? 0 : _killsRequired - 1;
    }

    protected override void OnSpawned(SpawnerEntry entry, ISpawnable spawned)
    {
        base.OnSpawned(entry, spawned);
        StartBossTimeout();
    }

    /// <summary>True when the mobile fights or was part of a harmful act within the aggressor expiry.</summary>
    public static bool IsInCombat(Mobile m)
    {
        if (m.Combatant != null)
        {
            return true;
        }

        return HasLiveAggression(m.Aggressors) || HasLiveAggression(m.Aggressed);
    }

    private static bool HasLiveAggression(List<AggressorInfo> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].Expired)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasSpawns => Spawned?.Count > 0;

    private bool AnySpawnInCombat()
    {
        if (!HasSpawns)
        {
            return false;
        }

        foreach (var spawned in Spawned.Keys)
        {
            if (spawned is Mobile { Deleted: false } m && IsInCombat(m))
            {
                return true;
            }
        }

        return false;
    }

    private void StartBossTimeout()
    {
        StopBossTimeout();

        if (_bossTimeout <= TimeSpan.Zero || Deleted)
        {
            return;
        }

        BossTimeoutTimer = Timer.DelayCall(_bossTimeout, OnBossTimeout);
        StartCombatCheck();
    }

    private void StartCombatCheck()
    {
        _combatCheckToken.Cancel();
        if (_bossTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var interval = _bossTimeout;
        var halfExpiry = AggressorInfo.ExpireDelay / 2;
        if (halfExpiry < interval)
        {
            interval = halfExpiry;
        }

        if (CombatCheckInterval < interval)
        {
            interval = CombatCheckInterval;
        }

        if (interval < MinCombatCheckInterval)
        {
            interval = MinCombatCheckInterval;
        }

        Timer.StartTimer(interval, interval, CheckCombat, out _combatCheckToken);
    }

    private void StopBossTimeout()
    {
        _combatCheckToken.Cancel();

        if (_bossTimeoutTimer != null)
        {
            _bossTimeoutTimer.Stop();
            BossTimeoutTimer = null;
        }
    }

    private void CheckCombat()
    {
        Defrag();

        if (!HasSpawns)
        {
            StopBossTimeout();
            return;
        }

        if (AnySpawnInCombat())
        {
            // Restart the deadline only; this token keeps sampling.
            _bossTimeoutTimer?.Stop();
            BossTimeoutTimer = Timer.DelayCall(_bossTimeout, OnBossTimeout);
        }
    }

    private void OnBossTimeout()
    {
        Defrag();

        if (!HasSpawns)
        {
            StopBossTimeout();
            return;
        }

        // Combat since the last sample still counts.
        if (AnySpawnInCombat())
        {
            BossTimeoutTimer = Timer.DelayCall(_bossTimeout, OnBossTimeout);
            return;
        }

        StopBossTimeout();
        RemoveSpawns();
        KillCount = 0;
    }

    private void StartKillDecay()
    {
        StopKillDecay();

        if (_killDecay > TimeSpan.Zero && _killCount > 0 && !Deleted)
        {
            KillDecayTimer = Timer.DelayCall(_killDecay, OnKillDecay);
        }
    }

    private void StopKillDecay()
    {
        if (_killDecayTimer != null)
        {
            _killDecayTimer.Stop();
            KillDecayTimer = null;
        }
    }

    private void OnKillDecay()
    {
        KillDecayTimer = null;
        Defrag();

        // Kills do not count while something is out, and the count does not decay either.
        if (!HasSpawns)
        {
            KillCount = Math.Max(0, _killCount - KillDecayStep);
        }

        StartKillDecay();
    }

    private void OnKillCountChanged(int oldValue, int newValue)
    {
        if (newValue <= 0)
        {
            StopKillDecay();
        }
        else if (_killDecayTimer?.Running != true)
        {
            StartKillDecay();
        }
    }

    private void OnBossTimeoutChanged(TimeSpan oldValue, TimeSpan newValue)
    {
        Defrag();

        if (newValue <= TimeSpan.Zero || !HasSpawns)
        {
            StopBossTimeout();
        }
        else
        {
            StartBossTimeout();
        }
    }

    private void OnKillDecayChanged(TimeSpan oldValue, TimeSpan newValue) => StartKillDecay();

    private void DeserializeBossTimeoutTimer(TimeSpan delay) => _bossTimeoutTimer = Timer.DelayCall(delay, OnBossTimeout);

    private void DeserializeKillDecayTimer(TimeSpan delay) => _killDecayTimer = Timer.DelayCall(delay, OnKillDecay);

    public override void GetSpawnerProperties(IPropertyList list)
    {
        base.GetSpawnerProperties(list);

        if (Running && _killsRequired > 0)
        {
            list.Add(1060658, $"{"kills"}\t{_killCount}{"/"}{_killsRequired}"); // ~1_val~: ~2_val~
        }
    }

    public override void OnAfterDuped(Item newItem)
    {
        base.OnAfterDuped(newItem);

        if (newItem is KillCountSpawner copy)
        {
            copy.SetCountedSpawners(_countedSpawners);
        }
    }

    public override void OnDelete()
    {
        _instances.Remove(this);
        StopBossTimeout();
        StopKillDecay();
        base.OnDelete();
    }

    [AfterDeserialization]
    private void AfterDeserialization()
    {
        _instances.Add(this);

        if (_bossTimeoutTimer?.Running == true)
        {
            StartCombatCheck();
        }
    }

    [OnEvent(nameof(SpawnerEvents.SpawnedDeathEvent))]
    public static void OnSpawnedDeath(BaseSpawner spawner, ISpawnable spawned, Mobile killer)
    {
        if (_instances.Count == 0 || spawned is BaseCreature { Controlled: true } or BaseCreature { Summoned: true })
        {
            return;
        }

        // A spawn may construct another counter, so iterate a snapshot rather than the live set.
        using var counters = PooledRefList<KillCountSpawner>.Create();
        foreach (var counter in _instances)
        {
            if (counter.Counts(spawner))
            {
                counters.Add(counter);
            }
        }

        for (var i = 0; i < counters.Count; i++)
        {
            counters[i].RegisterKill();
        }
    }
}
