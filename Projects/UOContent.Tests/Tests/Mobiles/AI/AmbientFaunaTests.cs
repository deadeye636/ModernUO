using System;
using System.Collections.Generic;
using Server;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests.Mobiles.AI;

// Covers the ambient-fauna behaviour: birds carry the shy SkittishAI and cats the StalkAI, the
// two are mutual enemies through OppositionGroup.BirdsAndCats, a wild bird backs off from a
// nearby player (and, only up close, from a predator), and a wild cat pounces prey it has crept
// up on while leaving tamed quarry alone.
[Collection("Sequential Pathfinding Tests")]
public class AmbientFaunaTests : IDisposable
{
    private readonly List<Mobile> _created = new();

    public void Dispose()
    {
        foreach (var m in _created)
        {
            m?.Delete();
        }

        _created.Clear();
    }

    private sealed class PlayerStub : Mobile
    {
        public PlayerStub() => Body = 0x190;
    }

    private T Track<T>(T m) where T : Mobile
    {
        _created.Add(m);
        return m;
    }

    private T Spawn<T>(Map map, Point3D loc) where T : BaseCreature, new()
    {
        var bc = new T();
        bc.MoveToWorld(loc, map);
        bc.AIObject.AITimer?.Stop();
        _created.Add(bc);
        return bc;
    }

    private PlayerStub SpawnPlayer(Map map, Point3D loc)
    {
        var p = new PlayerStub { Player = true };
        p.DefaultMobileInit();
        p.MoveToWorld(loc, map);
        _created.Add(p);
        return p;
    }

    [Fact]
    public void Birds_Use_SkittishAI_And_Cats_Use_StalkAI()
    {
        Assert.IsType<SkittishAI>(Track(new Bird()).AIObject);
        Assert.IsType<SkittishAI>(Track(new TropicalBird()).AIObject);
        Assert.IsType<StalkAI>(Track(new Cat()).AIObject);
    }

    [Fact]
    public void OppositionGroup_Makes_Cats_And_Birds_Mutual_Enemies()
    {
        var cat = Track(new Cat());
        var bird = Track(new Bird());
        var tropical = Track(new TropicalBird());

        var group = OppositionGroup.BirdsAndCats;

        Assert.True(group.IsEnemy(cat, bird));
        Assert.True(group.IsEnemy(bird, cat));
        Assert.True(group.IsEnemy(cat, tropical));

        // Same side: never enemies of their own kind.
        Assert.False(group.IsEnemy(bird, tropical));
        Assert.False(group.IsEnemy(bird, bird));
        Assert.False(group.IsEnemy(cat, cat));
    }

    [Theory]
    [InlineData(3, true)]  // a player a few tiles off alarms the bird
    [InlineData(6, true)]  // still inside the player-alarm range
    [InlineData(9, true)]  // right at the edge of the player-alarm range
    public void WildBird_BacksOff_FromApproachingPlayer(int distance, bool expectBackoff)
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var bird = Spawn<Bird>(map, new Point3D(1500, 1600, (sbyte)z));
        var player = SpawnPlayer(map, new Point3D(1500 - distance, 1600, (sbyte)z));

        bird.AIObject.DoActionWander();

        if (expectBackoff)
        {
            Assert.Equal(ActionType.Backoff, bird.AIObject.Action);
            Assert.Equal(player, bird.FocusMob);
        }
        else
        {
            Assert.Equal(ActionType.Wander, bird.AIObject.Action);
        }
    }

    [Theory]
    [InlineData(3, true)]  // a predator this close spooks the bird
    [InlineData(6, false)] // beyond the short predator-alarm range: a stalking cat goes unnoticed
    public void WildBird_OnlyNoticesPredator_UpClose(int distance, bool expectBackoff)
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var bird = Spawn<Bird>(map, new Point3D(1500, 1600, (sbyte)z));
        var cat = Spawn<Cat>(map, new Point3D(1500 - distance, 1600, (sbyte)z));

        bird.AIObject.DoActionWander();

        if (expectBackoff)
        {
            Assert.Equal(ActionType.Backoff, bird.AIObject.Action);
            Assert.Equal(cat, bird.FocusMob);
        }
        else
        {
            Assert.Equal(ActionType.Wander, bird.AIObject.Action);
        }
    }

    [Fact]
    public void WildCat_Pounces_PreyWithinPounceRange()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var cat = Spawn<Cat>(map, new Point3D(1500, 1600, (sbyte)z));
        var bird = Spawn<Bird>(map, new Point3D(1497, 1600, (sbyte)z)); // 3 tiles: inside pounce range

        cat.AIObject.DoActionWander();

        Assert.Equal(ActionType.Combat, cat.AIObject.Action);
        Assert.Equal(bird, cat.Combatant);
    }

    [Fact]
    public void WildCat_DoesNotPounce_DistantPrey()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var cat = Spawn<Cat>(map, new Point3D(1500, 1600, (sbyte)z));
        Spawn<Bird>(map, new Point3D(1492, 1600, (sbyte)z)); // 8 tiles: seen, but out of pounce range

        cat.AIObject.DoActionWander();

        Assert.Equal(ActionType.Wander, cat.AIObject.Action);
        Assert.Null(cat.Combatant);
    }

    [Fact]
    public void WildCat_IgnoresNonPrey()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var cat = Spawn<Cat>(map, new Point3D(1500, 1600, (sbyte)z));
        Spawn<Rat>(map, new Point3D(1498, 1600, (sbyte)z)); // not in the opposition group

        cat.AIObject.DoActionWander();

        Assert.Equal(ActionType.Wander, cat.AIObject.Action);
        Assert.Null(cat.Combatant);
    }

    [Fact]
    public void TamedCat_DoesNotHuntPrey()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var owner = SpawnPlayer(map, new Point3D(1510, 1600, (sbyte)z));
        var cat = Spawn<Cat>(map, new Point3D(1500, 1600, (sbyte)z));
        cat.SetControlMaster(owner);

        var bird = Spawn<Bird>(map, new Point3D(1497, 1600, (sbyte)z)); // within pounce range

        cat.AIObject.DoActionWander();

        Assert.NotEqual(bird, cat.Combatant);
    }

    [Fact]
    public void TamedBird_DoesNotFlee()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);

        var owner = SpawnPlayer(map, new Point3D(1510, 1600, (sbyte)z));
        var bird = Spawn<Bird>(map, new Point3D(1500, 1600, (sbyte)z));
        bird.SetControlMaster(owner);

        SpawnPlayer(map, new Point3D(1497, 1600, (sbyte)z)); // a stranger right next to it

        bird.AIObject.DoActionWander();

        Assert.NotEqual(ActionType.Backoff, bird.AIObject.Action);
    }
}
