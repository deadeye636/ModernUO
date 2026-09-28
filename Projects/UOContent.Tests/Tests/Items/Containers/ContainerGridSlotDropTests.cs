using System;
using System.Buffers;
using System.Buffers.Binary;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Tests.Network;
using Xunit;

namespace UOContent.Tests;

[Collection("Sequential UOContent Tests")]
public class ContainerGridSlotDropTests
{
    private const string EnhancedClient = "67.0.117";
    private const string ClassicClient = "7.0.74.28";

    // Clear of the coordinates other UOContent tests use.
    private static readonly Point3D Location = new(5200, 700, 0);

    private static void SendDrop(NetState ns, Serial dest, byte grid)
    {
        Span<byte> buffer = stackalloc byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, 0);             // serial, ignored
        BinaryPrimitives.WriteInt16BigEndian(buffer[4..], 44);        // x
        BinaryPrimitives.WriteInt16BigEndian(buffer[6..], 65);        // y
        buffer[8] = 0;                                                // z
        buffer[9] = grid;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[10..], dest.Value);

        IncomingItemPackets.DropReq(ns, new SpanReader(buffer));
    }

    private static void RunDrop(string version, Func<Container, Serial> dest, byte grid, Action<Container, Item> check)
    {
        using var ns = PacketTestUtilities.CreateTestNetState();
        ns.Version = new ClientVersion(version);
        Assert.True(ns.ContainerGridLines);

        var player = new PlayerMobile(World.NewMobile);
        player.DefaultMobileInit();
        player.MoveToWorld(Location, Map.Felucca);
        player.AddItem(new Backpack());
        ns.Mobile = player;

        try
        {
            var pack = player.Backpack;
            pack.DropItem(new Item(0x1F13)); // cell 0
            var moved = new Item(0x1F13);
            pack.DropItem(moved); // cell 1

            player.Lift(moved, 1, out var rejected, out var reason);
            Assert.False(rejected, reason.ToString());
            Assert.Same(moved, player.Holding);

            SendDrop(ns, dest(pack), grid);

            check(pack, moved);
            Assert.Equal(Item.NoGridSlot, player.GridSlotRequest);
        }
        finally
        {
            ns.Mobile = null;
            player.Holding?.Delete();
            player.Delete();
        }
    }

    [Fact]
    public void EnhancedClient_FreeRequestedCell_IsUsed() =>
        RunDrop(EnhancedClient, pack => pack.Serial, 7, (pack, moved) =>
        {
            Assert.Same(pack, moved.Parent);
            Assert.Equal(7, moved.GridSlot);
        });

    [Fact]
    public void EnhancedClient_TakenRequestedCell_FallsBackToLastCell() =>
        RunDrop(EnhancedClient, pack => pack.Serial, 0, (pack, moved) =>
        {
            Assert.Same(pack, moved.Parent);
            Assert.Equal(1, moved.GridSlot);
        });

    [Fact]
    public void EnhancedClient_NoCell_KeepsLastCell() =>
        RunDrop(EnhancedClient, pack => pack.Serial, Item.NoGridSlot, (pack, moved) =>
        {
            Assert.Same(pack, moved.Parent);
            Assert.Equal(1, moved.GridSlot);
        });

    [Fact]
    public void ClassicClient_GridByteIsIgnored() =>
        RunDrop(ClassicClient, pack => pack.Serial, 7, (pack, moved) =>
        {
            Assert.Same(pack, moved.Parent);
            Assert.Equal(1, moved.GridSlot);
        });

    [Fact]
    public void EnhancedClient_BouncedDrop_KeepsOldCell() =>
        // A target that does not exist bounces the item back into the backpack.
        RunDrop(EnhancedClient, _ => (Serial)0x7FFFFF00, 7, (pack, moved) =>
        {
            Assert.Same(pack, moved.Parent);
            Assert.Equal(1, moved.GridSlot);
        });

    private static PlayerMobile NewPlayer(NetState ns)
    {
        var player = new PlayerMobile(World.NewMobile);
        player.DefaultMobileInit();
        player.MoveToWorld(Location, Map.Felucca);
        player.AddItem(new Backpack());
        ns.Mobile = player;
        return player;
    }

    [Fact]
    public void PartialLift_RestKeepsStackCell()
    {
        using var ns = PacketTestUtilities.CreateTestNetState();
        ns.Version = new ClientVersion(EnhancedClient);
        var player = NewPlayer(ns);

        try
        {
            var pack = player.Backpack;
            pack.DropItem(new Item(0x1F13)); // cell 0
            var gold = new Gold(10);
            pack.DropItem(gold); // cell 1
            gold.GridSlot = 5;

            player.Lift(gold, 4, out var rejected, out var reason);
            Assert.False(rejected, reason.ToString());
            Assert.Same(gold, player.Holding);

            Item rest = null;
            foreach (var item in pack.Items)
            {
                if (item is Gold)
                {
                    rest = item;
                }
            }

            Assert.NotNull(rest);
            Assert.Equal(6, rest.Amount);
            Assert.Equal(5, rest.GridSlot);
        }
        finally
        {
            ns.Mobile = null;
            player.Holding?.Delete();
            player.Delete();
        }
    }

    [Theory]
    [InlineData(3, 1)] // the bag's own cell in the backpack: meant for the outer grid, ignored
    [InlineData(4, 4)] // any other cell is a cell of the bag's grid
    public void DropOntoBag_OuterCellIsNotUsedInsideTheBag(byte grid, byte expected)
    {
        using var ns = PacketTestUtilities.CreateTestNetState();
        ns.Version = new ClientVersion(EnhancedClient);
        var player = NewPlayer(ns);

        try
        {
            var pack = player.Backpack;
            pack.DropItem(new Item(0x1F13)); // cell 0
            var moved = new Item(0x1F13);
            pack.DropItem(moved); // cell 1
            var bag = new Bag();
            pack.DropItem(bag);
            bag.GridSlot = 3;

            player.Lift(moved, 1, out var rejected, out var reason);
            Assert.False(rejected, reason.ToString());

            SendDrop(ns, bag.Serial, grid);

            Assert.Same(bag, moved.Parent);
            Assert.Equal(expected, moved.GridSlot);
        }
        finally
        {
            ns.Mobile = null;
            player.Holding?.Delete();
            player.Delete();
        }
    }

    [Fact]
    public void EquipLightFromStack_RestKeepsStackCell()
    {
        using var ns = PacketTestUtilities.CreateTestNetState();
        ns.Version = new ClientVersion(EnhancedClient);
        var player = NewPlayer(ns);

        try
        {
            var pack = player.Backpack;
            pack.DropItem(new Item(0x1F13)); // cell 0
            var candles = new Candle { Amount = 5 };
            pack.DropItem(candles); // cell 1
            candles.GridSlot = 6;

            Assert.True(player.EquipItem(candles));
            Assert.Same(player, candles.Parent);
            Assert.Equal(1, candles.Amount);

            Item rest = null;
            foreach (var item in pack.Items)
            {
                if (item is Candle)
                {
                    rest = item;
                }
            }

            Assert.NotNull(rest);
            Assert.Equal(4, rest.Amount);
            Assert.Equal(6, rest.GridSlot);
        }
        finally
        {
            ns.Mobile = null;
            player.Delete();
        }
    }
}
