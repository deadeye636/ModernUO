using Server.Items;
using Server.Mobiles;
using Xunit;

namespace Server.Tests.Mobiles.AI;

[Collection("Sequential Pathfinding Tests")]
public class GoalBasedWanderTests
{
    private sealed class WandererStub : BaseCreature
    {
        public WandererStub(Serial serial) : base(serial) => Body = 0x190;
    }

    private static (WandererStub Mobile, BaseAI AI) NewWanderer(Map map, Point3D location)
    {
        var mobile = new WandererStub(World.NewMobile);
        mobile.DefaultMobileInit();
        mobile.MoveToWorld(location, map);
        var ai = new AnimalAI(mobile);
        ai.AITimer.Stop();
        return (mobile, ai);
    }

    [Fact]
    public void WanderGoal_UsesConsecutiveDirectSteps_WithGentleTurns()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        var start = new Point3D(1500, 1600, z);
        var (mobile, ai) = NewWanderer(map, start);
        var target = new Point3D(1505, 1600, z);
        mobile.Direction = Direction.North;
        ai.SetWanderTargetForTesting(target);

        var previousDirection = mobile.Direction & Direction.Mask;
        var moves = 0;

        for (var i = 0; i < 30 && ai.HasWanderTargetForTesting; i++)
        {
            var previousLocation = mobile.Location;
            ai.NextMove = 0;
            ai.ContinueGoalBasedWanderForTesting();

            if (mobile.Location == previousLocation)
            {
                continue;
            }

            moves++;
            var direction = mobile.Direction & Direction.Mask;
            var delta = System.Math.Abs((int)direction - (int)previousDirection);
            delta = System.Math.Min(delta, 8 - delta);
            Assert.InRange(delta, 0, 1);
            previousDirection = direction;
        }

        Assert.Equal(target.X, mobile.X);
        Assert.Equal(target.Y, mobile.Y);
        Assert.False(ai.HasWanderTargetForTesting);
        Assert.InRange(moves, 5, 12);
        Assert.Null(ai.Path);

        mobile.Delete();
    }

    [Fact]
    public void WanderGoal_DoesNotLeaveHomeRange()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        var home = new Point3D(1500, 1600, z);
        var (mobile, ai) = NewWanderer(map, home);
        mobile.Home = home;
        mobile.RangeHome = 2;
        mobile.Direction = Direction.East;
        ai.SetWanderTargetForTesting(new Point3D(1510, 1600, z));

        for (var i = 0; i < 40 && ai.HasWanderTargetForTesting; i++)
        {
            ai.NextMove = 0;
            ai.ContinueGoalBasedWanderForTesting();
        }

        Assert.InRange(System.Math.Abs(mobile.X - home.X), 0, mobile.RangeHome);
        Assert.InRange(System.Math.Abs(mobile.Y - home.Y), 0, mobile.RangeHome);
        Assert.False(ai.HasWanderTargetForTesting);
        Assert.Null(ai.Path);

        mobile.Delete();
    }

    [Fact]
    public void WanderGoal_TurnsTowardAnEscapeOutsideTheForwardOctants()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        var start = new Point3D(1500, 1600, z);
        var (mobile, ai) = NewWanderer(map, start);
        var northDoor = new DarkWoodDoor(DoorFacing.WestCW);
        var northEastDoor = new DarkWoodDoor(DoorFacing.WestCW);
        var northWestDoor = new DarkWoodDoor(DoorFacing.WestCW);
        northDoor.MoveToWorld(new Point3D(1500, 1599, z), map);
        northEastDoor.MoveToWorld(new Point3D(1501, 1599, z), map);
        northWestDoor.MoveToWorld(new Point3D(1499, 1599, z), map);

        mobile.Direction = Direction.North;
        ai.SetWanderTargetForTesting(new Point3D(1500, 1596, z));

        ai.NextMove = 0;
        ai.ContinueGoalBasedWanderForTesting();
        Assert.Equal(start, mobile.Location);
        Assert.Equal(Direction.Right, mobile.Direction & Direction.Mask);

        ai.NextMove = 0;
        ai.ContinueGoalBasedWanderForTesting();
        Assert.Equal(new Point3D(1501, 1600, z), mobile.Location);
        Assert.Equal(Direction.East, mobile.Direction & Direction.Mask);

        northDoor.Delete();
        northEastDoor.Delete();
        northWestDoor.Delete();
        mobile.Delete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmobilizedWanderer_NeitherMovesNorTurns(bool paralyze)
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        var start = new Point3D(1500, 1600, z);
        var (mobile, ai) = NewWanderer(map, start);

        // Open ground is enough: every direct step fails on the bad state, so the escape
        // search runs and is what would turn the creature.
        try
        {
            mobile.Direction = Direction.North;
            if (paralyze)
            {
                mobile.Paralyzed = true;
            }
            else
            {
                mobile.Frozen = true;
            }

            ai.SetWanderTargetForTesting(new Point3D(1500, 1596, z));

            for (var i = 0; i < 4; i++)
            {
                ai.NextMove = 0;
                ai.ContinueGoalBasedWanderForTesting();
                Assert.Equal(start, mobile.Location);
                Assert.Equal(Direction.North, mobile.Direction & Direction.Mask);
            }

            Assert.True(ai.HasWanderTargetForTesting);
        }
        finally
        {
            mobile.Delete();
        }
    }

    [Fact]
    public void OpenDoor_AtTheDestinationHeight_IsABarrier_FromAboveIt()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        // Standing 22 above the ground: the step east drops to z, where the door frame is.
        var start = new Point3D(1500, 1600, z + 22);
        var doorway = new Point3D(1501, 1600, z);
        var (mobile, ai) = NewWanderer(map, start);
        var door = new DarkWoodDoor(DoorFacing.WestCW);
        door.MoveToWorld(doorway, map);
        door.Open = true;

        try
        {
            mobile.Direction = Direction.East;

            // The drop itself is legal, so only the doorway rule can refuse it.
            Assert.True(mobile.CheckMovement(Direction.East, out var dropZ));
            Assert.Equal(z, dropZ);

            ai.SetWanderTargetForTesting(new Point3D(1504, 1600, z));
            ai.NextMove = 0;
            ai.ContinueGoalBasedWanderForTesting();

            Assert.False(mobile.X == doorway.X && mobile.Y == doorway.Y, "the wanderer must not step into the doorway");
        }
        finally
        {
            door.Delete();
            mobile.Delete();
        }
    }

    [Fact]
    public void OpenDoor_FarAboveTheDestination_IsNotABarrier()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        // The door frame is level with the wanderer but 30 above the tile it steps onto.
        var start = new Point3D(1500, 1600, z + 22);
        var (mobile, ai) = NewWanderer(map, start);
        var door = new DarkWoodDoor(DoorFacing.WestCW);
        door.MoveToWorld(new Point3D(1501, 1600, z + 30), map);
        door.Open = true;

        try
        {
            mobile.Direction = Direction.East;
            ai.SetWanderTargetForTesting(new Point3D(1504, 1600, z));
            ai.NextMove = 0;
            ai.ContinueGoalBasedWanderForTesting();

            Assert.Equal(new Point3D(1501, 1600, z), mobile.Location);
        }
        finally
        {
            door.Delete();
            mobile.Delete();
        }
    }

    [Fact]
    public void OpenDoor_RemainsAnIdleWanderBarrier()
    {
        var map = Map.Maps[1];
        Assert.NotNull(map);
        map.GetAverageZ(1500, 1600, out _, out var z, out _);
        var start = new Point3D(1500, 1600, z);
        var doorway = new Point3D(1501, 1600, z);
        var (mobile, ai) = NewWanderer(map, start);
        var door = new DarkWoodDoor(DoorFacing.WestCW);
        door.MoveToWorld(doorway, map);
        door.Open = true;

        try
        {
            mobile.Direction = Direction.East;
            ai.SetWanderTargetForTesting(new Point3D(1504, 1600, z));
            ai.NextMove = 0;
            ai.ContinueGoalBasedWanderForTesting();

            Assert.NotEqual(doorway, mobile.Location);
            Assert.True(door.Open);
            Assert.Null(ai.Path);
        }
        finally
        {
            door.Delete();
            mobile.Delete();
        }
    }
}
