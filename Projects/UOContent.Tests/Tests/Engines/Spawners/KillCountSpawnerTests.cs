using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Server;
using Server.Engines.Spawners;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests.Engines.Spawners;

[Collection("Sequential UOContent Tests")]
public class KillCountSpawnerTests : IDisposable
{
    private static readonly Point3D FeederLocation = new(1600, 1600, 0);
    private static readonly Point3D BossLocation = new(1601, 1600, 0);

    private readonly List<IEntity> _created = [];

    public void Dispose()
    {
        for (var i = _created.Count - 1; i >= 0; i--)
        {
            _created[i].Delete();
        }
    }

    private T Track<T>(T entity) where T : IEntity
    {
        _created.Add(entity);
        return entity;
    }

    private Spawner NewFeeder()
    {
        var feeder = Track(new Spawner(10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, "Rabbit"));
        feeder.MoveToWorld(FeederLocation, Map.Felucca);
        return feeder;
    }

    private KillCountSpawner NewBossSpawner(int killsRequired, params Guid[] counted)
    {
        var spawner = Track(new KillCountSpawner(1, killsRequired, counted, "Rat"));
        spawner.MoveToWorld(BossLocation, Map.Felucca);
        return spawner;
    }

    private static void KillOneFrom(BaseSpawner feeder)
    {
        feeder.Spawn();
        BaseCreature victim = null;
        foreach (var spawned in feeder.Spawned.Keys)
        {
            if (spawned is BaseCreature bc && !bc.Deleted && bc.Alive)
            {
                victim = bc;
                break;
            }
        }

        Assert.NotNull(victim);
        victim.Kill();
    }

    private static BaseCreature Boss(KillCountSpawner spawner)
    {
        spawner.Defrag();
        foreach (var spawned in spawner.Spawned.Keys)
        {
            return spawned as BaseCreature;
        }

        return null;
    }

    [Fact]
    public void Kills_OfCountedSpawner_SpawnTheBossAtTheThreshold()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(3, feeder.Guid);

        Assert.True(boss.Running);
        Assert.True(boss.IsEmpty);

        KillOneFrom(feeder);
        KillOneFrom(feeder);
        Assert.Equal(2, boss.KillCount);
        Assert.True(boss.IsEmpty);

        KillOneFrom(feeder);
        Assert.NotNull(Boss(boss));
        Assert.Equal(0, boss.KillCount);
    }

    [Fact]
    public void Kills_WhileTheBossLives_DoNotCount_AndCountingRestartsAfterHisDeath()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(2, feeder.Guid);

        KillOneFrom(feeder);
        KillOneFrom(feeder);
        var first = Boss(boss);
        Assert.NotNull(first);

        KillOneFrom(feeder);
        KillOneFrom(feeder);
        Assert.Equal(0, boss.KillCount);
        Assert.Same(first, Boss(boss));

        first.Kill();
        Assert.Equal(0, boss.KillCount);
        Assert.Null(Boss(boss));

        KillOneFrom(feeder);
        Assert.Equal(1, boss.KillCount);
        Assert.Null(Boss(boss));

        KillOneFrom(feeder);
        var second = Boss(boss);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Kills_OfOtherSpawners_TamedOrStoppedSpawners_DoNotCount()
    {
        var feeder = NewFeeder();
        var other = Track(new Spawner(5, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, "Rabbit"));
        other.MoveToWorld(new Point3D(1602, 1600, 0), Map.Felucca);
        var boss = NewBossSpawner(5, feeder.Guid);

        KillOneFrom(other);
        Assert.Equal(0, boss.KillCount);

        feeder.Spawn();
        BaseCreature tame = null;
        foreach (var spawned in feeder.Spawned.Keys)
        {
            tame = spawned as BaseCreature;
        }

        Assert.NotNull(tame);
        var owner = Track(new PlayerMobile());
        tame.Controlled = true;
        tame.ControlMaster = owner;
        tame.Kill();
        Assert.Equal(0, boss.KillCount);

        boss.Running = false;
        KillOneFrom(feeder);
        Assert.Equal(0, boss.KillCount);
    }

    [Fact]
    public void Respawn_ClearsTheBossButSpawnsNothing()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(1, feeder.Guid);

        boss.Respawn();
        Assert.True(boss.IsEmpty);

        KillOneFrom(feeder);
        Assert.NotNull(Boss(boss));

        boss.Respawn();
        Assert.True(boss.IsEmpty);
        Assert.Equal(0, boss.KillCount);
    }

    [Fact]
    public void State_SurvivesABinaryRoundTrip_AndTheLoadedSpawnerCounts()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(100, feeder.Guid);
        KillOneFrom(feeder);
        KillOneFrom(feeder);
        Assert.Equal(2, boss.KillCount);

        var bytes = SpawnerBlob.Write(boss);
        var loaded = Track(SpawnerBlob.Read<KillCountSpawner>(bytes, (Serial)0x40001101));

        Assert.Equal(2, loaded.KillCount);
        Assert.Equal(100, loaded.KillsRequired);
        Assert.Equal([feeder.Guid], loaded.CountedSpawnerGuids);
        Assert.Equal("Rat", Assert.Single(loaded.Entries).SpawnedName);

        boss.Delete();
        loaded.MoveToWorld(BossLocation, Map.Felucca);
        KillOneFrom(feeder);
        Assert.Equal(3, loaded.KillCount);
    }

    [Fact]
    public void Delete_StopsCounting_AndUnregisters()
    {
        var feeder = NewFeeder();
        var before = KillCountSpawner.InstanceCount;
        var boss = NewBossSpawner(100, feeder.Guid);
        Assert.Equal(before + 1, KillCountSpawner.InstanceCount);

        boss.Delete();
        Assert.Equal(before, KillCountSpawner.InstanceCount);

        KillOneFrom(feeder);

        Assert.Equal(0, boss.KillCount);
    }

    [Fact]
    public void TwoCounters_OnOneFeeder_CountIndependently()
    {
        var feeder = NewFeeder();
        var first = NewBossSpawner(2, feeder.Guid);
        var second = Track(new KillCountSpawner(1, 3, [feeder.Guid], "Rat"));
        second.MoveToWorld(new Point3D(1603, 1600, 0), Map.Felucca);

        KillOneFrom(feeder);
        KillOneFrom(feeder);

        Assert.NotNull(Boss(first));
        Assert.Equal(2, second.KillCount);
        Assert.Null(Boss(second));
    }

    [Fact]
    public void Dupe_KeepsTheCountedSpawners()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(2, feeder.Guid);
        var copy = Track(new KillCountSpawner());

        boss.Dupe(copy);

        Assert.NotEqual(boss.Guid, copy.Guid);
        Assert.Equal([feeder.Guid], copy.CountedSpawnerGuids);
        Assert.Equal(2, copy.KillsRequired);
    }

    [Fact]
    public void SetCountedSpawners_DropsEmptyDuplicateAndSelf()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(1);

        boss.SetCountedSpawners([feeder.Guid, Guid.Empty, feeder.Guid, boss.Guid]);

        Assert.Equal([feeder.Guid], boss.CountedSpawnerGuids);
        Assert.False(boss.Counts(boss));
    }

    [Fact]
    public void Json_RoundTripsTheTrigger_ButNotTheRunningCount()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(100, feeder.Guid);
        boss.HomeRange = 15;
        boss.WalkingRange = 30;
        boss.KillCount = 42;
        boss.BossTimeout = TimeSpan.FromMinutes(60);
        boss.KillDecay = TimeSpan.FromMinutes(30);

        var json = JsonSerializer.Serialize(new List<SpawnerDto> { boss.ToDto() }, SpawnerJsonSerializer.Options);
        Assert.Contains("\"$type\": \"KillCountSpawner\"", json);
        Assert.Contains("\"killsRequired\": 100", json);
        Assert.Contains("\"bossTimeout\": \"01:00:00\"", json);
        Assert.Contains("\"killDecay\": \"00:30:00\"", json);
        Assert.Contains(feeder.Guid.ToString(), json);
        Assert.Contains("\"homeRange\": 15", json);
        Assert.DoesNotContain("\"killCount\"", json);

        var dto = Assert.Single(JsonSerializer.Deserialize<List<SpawnerDto>>(json, SpawnerJsonSerializer.Options));
        var rebuilt = Track(Assert.IsType<KillCountSpawner>(dto.ToSpawner()));
        Assert.Equal(100, rebuilt.KillsRequired);
        Assert.Equal(0, rebuilt.KillCount);
        Assert.Equal([feeder.Guid], rebuilt.CountedSpawnerGuids);
        Assert.Equal(30, rebuilt.WalkingRange);
        Assert.Equal(TimeSpan.FromMinutes(60), rebuilt.BossTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), rebuilt.KillDecay);
        Assert.Null(rebuilt.KillDecayRemaining);
        Assert.Null(rebuilt.BossTimeoutRemaining);
    }

    [Fact]
    public void Json_OmitsDisabledTimeouts_AndReadsTheirAbsenceAsOff()
    {
        var feeder = NewFeeder();
        var boss = NewBossSpawner(5, feeder.Guid);

        var json = JsonSerializer.Serialize(new List<SpawnerDto> { boss.ToDto() }, SpawnerJsonSerializer.Options);
        Assert.DoesNotContain("\"bossTimeout\"", json);
        Assert.DoesNotContain("\"killDecay\"", json);

        var dto = Assert.Single(JsonSerializer.Deserialize<List<SpawnerDto>>(json, SpawnerJsonSerializer.Options));
        var rebuilt = Track(Assert.IsType<KillCountSpawner>(dto.ToSpawner()));
        Assert.Equal(TimeSpan.Zero, rebuilt.BossTimeout);
        Assert.Equal(TimeSpan.Zero, rebuilt.KillDecay);
    }

    [Fact]
    public void Import_ReplacesAPlainSpawnerWithTheSameGuid()
    {
        var guid = new Guid("44444444-4444-4444-4444-444444444444");
        var plain = new Spawner(1, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), 0, default, "Rat");
        plain.MoveToWorld(BossLocation, Map.Felucca);
        // The import looks spawners up by GUID; filing the plain one under the JSON GUID stands in for
        // the first version's plain spawner, which carried the GUID the generator still writes.
        var all = new Dictionary<Guid, ISpawner> { [guid] = plain };
        var json = $$"""
            [ { "$type": "KillCountSpawner", "guid": "{{guid}}", "location": [1601, 1600, 0], "map": "Felucca",
                "count": 1, "minDelay": "00:05:00", "maxDelay": "00:10:00", "homeRange": 15,
                "entries": [ { "name": "Rat", "maxCount": 1, "probability": 100 } ],
                "countSpawners": [ "{{Guid.NewGuid()}}" ], "killsRequired": 100 } ]
            """;

        var dir = Path.Combine(Path.GetTempPath(), "muo-killcount-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "boss.json");
        File.WriteAllText(path, json);
        try
        {
            ImportSpawnersCommand.ImportFile(new FileInfo(path), all);

            Assert.True(plain.Deleted);
            var placed = Assert.IsType<KillCountSpawner>(all[guid]);
            Track(placed);
            Assert.Equal(guid, placed.Guid);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Import_PlacesTheSpawnerEmpty()
    {
        var feeder = NewFeeder();
        var dir = Path.Combine(Path.GetTempPath(), "muo-killcount-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "boss.json");
        File.WriteAllText(path, $$"""
            [
              {
                "$type": "KillCountSpawner",
                "guid": "33333333-3333-3333-3333-333333333333",
                "name": "Test Boss",
                "location": [1601, 1600, 0],
                "map": "Felucca",
                "count": 1,
                "minDelay": "00:30:00",
                "maxDelay": "01:00:00",
                "homeRange": 15,
                "walkingRange": 30,
                "entries": [ { "name": "Rat", "maxCount": 1, "probability": 100 } ],
                "countSpawners": [ "{{feeder.Guid}}" ],
                "killsRequired": 2
              }
            ]
            """);

        try
        {
            var all = new Dictionary<Guid, ISpawner>();
            ImportSpawnersCommand.ImportFile(new FileInfo(path), all);

            var placed = Assert.IsType<KillCountSpawner>(all[new Guid("33333333-3333-3333-3333-333333333333")]);
            Track(placed);
            Assert.True(placed.IsEmpty);
            Assert.True(placed.Running);
            Assert.Equal(2, placed.KillsRequired);

            KillOneFrom(feeder);
            KillOneFrom(feeder);
            Assert.NotNull(Boss(placed));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Drives the timer wheel and the loop clock together, the way the event loop does.
    private static void StartClock()
    {
        Core._tickCount = 0;
        Timer.Init(0);
    }

    private static void Advance(TimeSpan span)
    {
        var end = Core.TickCount + (long)span.TotalMilliseconds;
        while (Core.TickCount - end < 0)
        {
            Core._tickCount += 8;
            Core._now += TimeSpan.FromMilliseconds(8);
            Timer.Slice(Core.TickCount);
        }
    }

    // A world load after downtime: the loop clock jumped, and the save loader shifts anchored times by as much.
    private static T ReadAfterDowntime<T>(byte[] bytes, Serial serial, TimeSpan downtime) where T : Item
    {
        Core._now += downtime;
        var item = (T)Activator.CreateInstance(typeof(T), serial)!;
        item.Deserialize(new BufferReader(bytes) { AnchoredTimeShift = downtime });
        return item;
    }

    private static void AssertAbout(TimeSpan expected, TimeSpan? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value, expected - TimeSpan.FromSeconds(1), expected + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void BossTimeout_RemovesTheIdleBoss_AndCountingStartsAgainFromZero()
    {
        StartClock();
        var feeder = NewFeeder();
        var spawner = NewBossSpawner(2, feeder.Guid);
        spawner.BossTimeout = TimeSpan.FromMinutes(60);

        KillOneFrom(feeder);
        KillOneFrom(feeder);
        var boss = Boss(spawner);
        Assert.NotNull(boss);
        AssertAbout(TimeSpan.FromMinutes(60), spawner.BossTimeoutRemaining);

        Advance(TimeSpan.FromMinutes(59));
        Assert.False(boss.Deleted);

        Advance(TimeSpan.FromMinutes(2));
        Assert.True(boss.Deleted);
        Assert.Null(Boss(spawner));
        Assert.Equal(0, spawner.KillCount);
        Assert.Null(spawner.BossTimeoutRemaining);

        KillOneFrom(feeder);
        Assert.Equal(1, spawner.KillCount);
    }

    [Fact]
    public void BossTimeout_DoesNotFireWhileTheBossIsInCombat()
    {
        StartClock();
        var feeder = NewFeeder();
        var spawner = NewBossSpawner(1, feeder.Guid);
        spawner.BossTimeout = TimeSpan.FromMinutes(60);
        var attacker = Track(new PlayerMobile());

        KillOneFrom(feeder);
        var boss = Boss(spawner);
        Assert.NotNull(boss);

        var aggression = AggressorInfo.Create(attacker, boss, false);
        boss.Aggressors.Add(aggression);
        for (var minute = 0; minute < 90; minute++)
        {
            Advance(TimeSpan.FromMinutes(1));
            aggression.Refresh();
        }

        Assert.False(boss.Deleted);

        // The fight is over; the aggressor entry expires after two minutes, the hour counts from there.
        Advance(TimeSpan.FromMinutes(59));
        Assert.False(boss.Deleted);

        Advance(TimeSpan.FromMinutes(5));
        Assert.True(boss.Deleted);
        Assert.Equal(0, spawner.KillCount);
    }

    [Fact]
    public void KillDecay_LowersTheCountStepByStep_AndACountedKillRestartsTheIdleTime()
    {
        StartClock();
        var feeder = NewFeeder();
        var spawner = NewBossSpawner(100, feeder.Guid);
        spawner.KillDecay = TimeSpan.FromMinutes(30);
        Assert.Equal(10, spawner.KillDecayStep);

        for (var i = 0; i < 25; i++)
        {
            KillOneFrom(feeder);
        }

        Assert.Equal(25, spawner.KillCount);
        AssertAbout(TimeSpan.FromMinutes(30), spawner.KillDecayRemaining);

        Advance(TimeSpan.FromMinutes(20));
        KillOneFrom(feeder);
        Assert.Equal(26, spawner.KillCount);

        Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(26, spawner.KillCount);

        Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(16, spawner.KillCount);

        Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(6, spawner.KillCount);

        Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, spawner.KillCount);
        Assert.Null(spawner.KillDecayRemaining);
    }

    [Fact]
    public void KillDecay_IsIdleWhileABossIsOut()
    {
        StartClock();
        var feeder = NewFeeder();
        var spawner = Track(new KillCountSpawner(2, 3, [feeder.Guid], "Rat"));
        spawner.MoveToWorld(BossLocation, Map.Felucca);
        spawner.KillDecay = TimeSpan.FromMinutes(30);

        KillOneFrom(feeder);
        KillOneFrom(feeder);
        KillOneFrom(feeder);
        spawner.Defrag();
        Assert.Equal(2, spawner.Spawned.Count);

        // One of the two dies: the spawner is no longer full, so kills count again while the other is out.
        Boss(spawner).Kill();
        KillOneFrom(feeder);
        Assert.Equal(1, spawner.KillCount);

        Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, spawner.KillCount);

        Boss(spawner).Kill();
        Assert.Null(Boss(spawner));

        Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, spawner.KillCount);
    }

    [Fact]
    public void ZeroTimeouts_DisableBothTimers()
    {
        StartClock();
        var feeder = NewFeeder();
        var withBoss = NewBossSpawner(1, feeder.Guid);
        var counting = Track(new KillCountSpawner(1, 3, [feeder.Guid], "Rat"));
        counting.MoveToWorld(new Point3D(1603, 1600, 0), Map.Felucca);

        KillOneFrom(feeder);
        var boss = Boss(withBoss);
        Assert.NotNull(boss);
        Assert.Equal(1, counting.KillCount);
        Assert.Null(withBoss.BossTimeoutRemaining);
        Assert.Null(counting.KillDecayRemaining);

        Advance(TimeSpan.FromHours(2));
        Assert.False(boss.Deleted);
        Assert.Equal(1, counting.KillCount);

        // Switching a running timeout off stops it.
        withBoss.BossTimeout = TimeSpan.FromMinutes(60);
        Assert.NotNull(withBoss.BossTimeoutRemaining);
        withBoss.BossTimeout = TimeSpan.Zero;
        Assert.Null(withBoss.BossTimeoutRemaining);
        counting.KillDecay = TimeSpan.FromMinutes(30);
        Assert.NotNull(counting.KillDecayRemaining);
        counting.KillDecay = TimeSpan.Zero;
        Assert.Null(counting.KillDecayRemaining);
    }

    [Fact]
    public void KillDecay_SurvivesASaveLoadRoundTrip_AndResumesWithTheTimeLeft()
    {
        StartClock();
        var feeder = NewFeeder();
        var spawner = NewBossSpawner(10, feeder.Guid);
        spawner.KillDecay = TimeSpan.FromMinutes(30);
        spawner.BossTimeout = TimeSpan.FromMinutes(60);
        KillOneFrom(feeder);
        KillOneFrom(feeder);
        Advance(TimeSpan.FromMinutes(10));

        var bytes = SpawnerBlob.Write(spawner);
        spawner.Delete();
        var loaded = Track(ReadAfterDowntime<KillCountSpawner>(bytes, (Serial)0x40001102, TimeSpan.FromHours(3)));

        Assert.Equal(TimeSpan.FromMinutes(30), loaded.KillDecay);
        Assert.Equal(TimeSpan.FromMinutes(60), loaded.BossTimeout);
        Assert.Equal(2, loaded.KillCount);
        AssertAbout(TimeSpan.FromMinutes(20), loaded.KillDecayRemaining);
        Assert.Null(loaded.BossTimeoutRemaining);

        loaded.MoveToWorld(BossLocation, Map.Felucca);
        Advance(TimeSpan.FromMinutes(19));
        Assert.Equal(2, loaded.KillCount);
        Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, loaded.KillCount);
    }

    [Fact]
    public void BossTimeout_SurvivesASaveLoadRoundTrip_AndResumesWithTheTimeLeft()
    {
        StartClock();
        var feeder = NewFeeder();
        var spawner = NewBossSpawner(1, feeder.Guid);
        spawner.BossTimeout = TimeSpan.FromMinutes(60);
        KillOneFrom(feeder);
        var boss = Boss(spawner);
        Assert.NotNull(boss);
        Advance(TimeSpan.FromMinutes(15));

        var bytes = SpawnerBlob.Write(spawner);
        // The original keeps its boss but must not race the loaded copy.
        spawner.BossTimeout = TimeSpan.Zero;
        var loaded = Track(ReadAfterDowntime<KillCountSpawner>(bytes, (Serial)0x40001103, TimeSpan.FromHours(3)));

        AssertAbout(TimeSpan.FromMinutes(45), loaded.BossTimeoutRemaining);
        Assert.Same(boss, Boss(loaded));

        // The combat sample restarts with the load: a fight after it keeps the boss past the saved deadline.
        var attacker = Track(new PlayerMobile());
        var aggression = AggressorInfo.Create(attacker, boss, false);
        boss.Aggressors.Add(aggression);
        for (var minute = 0; minute < 50; minute++)
        {
            Advance(TimeSpan.FromMinutes(1));
            aggression.Refresh();
        }

        Assert.False(boss.Deleted);
        Assert.InRange(loaded.BossTimeoutRemaining!.Value, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(60));

        // The fight ends; the entry expires two minutes later, the hour counts from the last sample that saw it.
        Advance(TimeSpan.FromMinutes(58));
        Assert.False(boss.Deleted);
        Advance(TimeSpan.FromMinutes(6));
        Assert.True(boss.Deleted);
        Assert.Null(loaded.BossTimeoutRemaining);
    }

    [Fact]
    public void ShippedOldHavenFile_CarriesTheDrelgorTriggerAndTimeouts()
    {
        var path = Path.Combine(Core.BaseDirectory, "Data", "Spawns", "custom", "trammel", "OldHaven.json");
        var dtos = JsonSerializer.Deserialize<List<SpawnerDto>>(File.ReadAllText(path), SpawnerJsonSerializer.Options);
        var dto = Assert.IsType<KillCountSpawnerDto>(Assert.Single(dtos));

        Assert.Equal(100, dto.KillsRequired);
        Assert.Equal([new Guid("1af71e9c-8891-43d9-940a-bdbd94560583")], dto.CountSpawners);
        Assert.Equal(TimeSpan.FromMinutes(60), dto.BossTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), dto.KillDecay);
    }

    [Fact]
    public void Version0Save_MigratesWithBothTimeoutsOff()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tests", "Engines", "Spawners", "Fixtures", "killcount.v0.bin");
        var loaded = Track(SpawnerBlob.Read<KillCountSpawner>(File.ReadAllBytes(path), (Serial)0x40001104));

        Assert.Equal(42, loaded.KillCount);
        Assert.Equal(100, loaded.KillsRequired);
        Assert.Equal([new Guid("1af71e9c-8891-43d9-940a-bdbd94560583")], loaded.CountedSpawnerGuids);
        Assert.Equal("Rat", Assert.Single(loaded.Entries).SpawnedName);
        Assert.Equal(TimeSpan.Zero, loaded.BossTimeout);
        Assert.Equal(TimeSpan.Zero, loaded.KillDecay);
        Assert.Null(loaded.BossTimeoutRemaining);
        Assert.Null(loaded.KillDecayRemaining);
    }
}
