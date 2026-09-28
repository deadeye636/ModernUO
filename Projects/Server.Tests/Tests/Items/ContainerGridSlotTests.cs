using System;
using System.Buffers.Binary;
using Server.Items;
using Server.Network;
using Server.Tests.Network;
using Xunit;

namespace Server.Tests;

[Collection("Sequential Server Tests")]
public class ContainerGridSlotTests
{
    private class StackableItem : Item
    {
        public StackableItem() => Stackable = true;

        public StackableItem(Serial serial) : base(serial) => Stackable = true;
    }

    private static byte[] SerializeItem(Item item)
    {
        var writer = new BufferWriter(new byte[256], true);
        item.Serialize(writer);
        return writer.Buffer[..(int)writer.Position];
    }

    private static Container NewContainer(int maxItems = 125)
    {
        var cont = new Container(World.NewItem) { MaxItems = maxItems };
        cont.Map = Map.Felucca;
        return cont;
    }

    private static Item AddNew(Container cont)
    {
        var item = new Item(World.NewItem);
        cont.AddItem(item);
        return item;
    }

    [Fact]
    public void NewItem_HasNoSlot()
    {
        var item = new Item(World.NewItem);
        Assert.Equal(Item.NoGridSlot, item.GridSlot);
        item.Delete();
    }

    [Theory]
    [InlineData(Item.NoGridSlot)]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(124)]
    public void Serialize_RoundTripsSlot(byte slot)
    {
        var item = new Item(World.NewItem) { GridSlot = slot };
        var restored = new Item((Serial)0x7ffff200u);

        try
        {
            var bytes = SerializeItem(item);
            Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(bytes));

            restored.Deserialize(new BufferReader(bytes));
            Assert.Equal(slot, restored.GridSlot);
        }
        finally
        {
            item.Delete();
            restored.Delete();
        }
    }

    [Fact]
    public void Serialize_WithoutSlot_WritesNoExtraByte()
    {
        var without = new Item(World.NewItem);
        var with = new Item(World.NewItem) { GridSlot = 3 };

        try
        {
            Assert.Equal(SerializeItem(without).Length + 1, SerializeItem(with).Length);
        }
        finally
        {
            without.Delete();
            with.Delete();
        }
    }

    [Fact]
    public void Deserialize_Version11_LoadsWithoutSlot()
    {
        var item = new Item(World.NewItem) { Hue = 0x21 };
        var restored = new Item((Serial)0x7ffff201u);

        try
        {
            // A v11 record has the same layout as a v12 record without the grid flag.
            var bytes = SerializeItem(item);
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 11);

            restored.Deserialize(new BufferReader(bytes));

            Assert.Equal(Item.NoGridSlot, restored.GridSlot);
            Assert.Equal(0x21, restored.Hue);
        }
        finally
        {
            item.Delete();
            restored.Delete();
        }
    }

    [Fact]
    public void AddItem_AssignsLowestFreeSlots()
    {
        var cont = NewContainer();

        try
        {
            var a = AddNew(cont);
            var b = AddNew(cont);
            var c = AddNew(cont);

            Assert.Equal(0, a.GridSlot);
            Assert.Equal(1, b.GridSlot);
            Assert.Equal(2, c.GridSlot);
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void AddItem_PrefersItsLastSlotWhenFree()
    {
        var cont = NewContainer();
        var other = NewContainer();

        try
        {
            AddNew(cont);
            var item = new Item(World.NewItem) { GridSlot = 9 };
            cont.AddItem(item);
            Assert.Equal(9, item.GridSlot);

            // Taken last slot: lowest free instead.
            var second = new Item(World.NewItem) { GridSlot = 9 };
            cont.AddItem(second);
            Assert.Equal(1, second.GridSlot);

            // Moving to another container keeps the cell if it is free there.
            other.AddItem(item);
            Assert.Equal(9, item.GridSlot);
        }
        finally
        {
            cont.Delete();
            other.Delete();
        }
    }

    [Fact]
    public void AddItem_FullGrid_GetsNoSlot()
    {
        var cont = NewContainer(3);

        try
        {
            AddNew(cont);
            AddNew(cont);
            AddNew(cont);
            var overflow = AddNew(cont);

            Assert.Equal(3, cont.GridSize);
            Assert.Equal(Item.NoGridSlot, overflow.GridSlot);
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void GridSize_IsCappedAt125()
    {
        var cont = NewContainer(500);
        var unlimited = NewContainer(0);

        try
        {
            Assert.Equal(125, cont.GridSize);
            Assert.Equal(125, unlimited.GridSize);
        }
        finally
        {
            cont.Delete();
            unlimited.Delete();
        }
    }

    [Fact]
    public void Setter_InContainer_MovesOnlyToFreeCell()
    {
        var cont = NewContainer();

        try
        {
            var a = AddNew(cont);
            var b = AddNew(cont);

            b.GridSlot = 40;
            Assert.Equal(40, b.GridSlot);

            b.GridSlot = 0; // taken by a
            Assert.Equal(40, b.GridSlot);

            b.GridSlot = 200; // outside the grid
            Assert.Equal(40, b.GridSlot);

            Assert.Equal(0, a.GridSlot);
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void RemovalPaths_FreeTheSlot()
    {
        var cont = NewContainer();
        var other = NewContainer();

        try
        {
            var deleted = AddNew(cont);  // 0
            var moved = AddNew(cont);    // 1
            var reparented = AddNew(cont); // 2
            AddNew(cont);                // 3

            deleted.Delete();
            Assert.Equal(0, AddNew(cont).GridSlot);

            moved.MoveToWorld(new Point3D(10, 10, 0), Map.Felucca);
            Assert.Equal(1, AddNew(cont).GridSlot);

            other.AddItem(reparented);
            Assert.Equal(2, AddNew(cont).GridSlot);

            moved.Delete();
        }
        finally
        {
            cont.Delete();
            other.Delete();
        }
    }

    private static (Container cont, StackableItem stack) NewStackInSlot5()
    {
        var cont = NewContainer();
        AddNew(cont); // 0
        var stack = new StackableItem(World.NewItem) { Amount = 10 };
        cont.AddItem(stack); // 1
        stack.GridSlot = 5;
        return (cont, stack);
    }

    [Fact]
    public void LiftItemDupe_OriginalKeepsSlot_NewItemGetsFreeSlot()
    {
        // Stealing, vendor buy/sell and corpse instancing keep the original in the container and hand the new item on.
        var (cont, stack) = NewStackInSlot5();

        try
        {
            var split = Mobile.LiftItemDupe(stack, 6);

            Assert.Same(cont, split.Parent);
            Assert.Equal(5, stack.GridSlot);
            Assert.Equal(1, split.GridSlot);
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void LiftItemDupe_StealingPattern_StolenPartLeaves()
    {
        // Stealing.cs: stolen = LiftItemDupe(toSteal, toSteal.Amount - amount), then moved to the thief.
        var (cont, stack) = NewStackInSlot5();
        var thiefPack = NewContainer();

        try
        {
            var stolen = Mobile.LiftItemDupe(stack, stack.Amount - 3);
            thiefPack.AddItem(stolen);

            Assert.Equal(7, stack.Amount);
            Assert.Equal(5, stack.GridSlot);
            Assert.Same(thiefPack, stolen.Parent);
            Assert.Equal(1, AddNew(cont).GridSlot); // the split's temporary cell is free again
        }
        finally
        {
            cont.Delete();
            thiefPack.Delete();
        }
    }

    [Fact]
    public void LiftItemDupe_VendorPattern_BoughtPartLeaves()
    {
        // BaseVendor.cs: item = LiftItemDupe(resp.Item, resp.Item.Amount - amount); cont.DropItem(item).
        var (cont, stack) = NewStackInSlot5();
        var buyerPack = NewContainer();

        try
        {
            var bought = Mobile.LiftItemDupe(stack, stack.Amount - 4);
            buyerPack.DropItem(bought);

            Assert.Equal(5, stack.GridSlot);
            Assert.Same(buyerPack, bought.Parent);
            Assert.Equal(1, bought.GridSlot); // its cell from the split, free in the buyer's pack
        }
        finally
        {
            cont.Delete();
            buyerPack.Delete();
        }
    }

    [Fact]
    public void LiftItemDupe_CorpsePattern_BothPartsStayWithDistinctSlots()
    {
        // Corpse.cs instancing: every split stays in the corpse next to the original.
        var (cont, stack) = NewStackInSlot5();

        try
        {
            var first = Mobile.LiftItemDupe(stack, stack.Amount - 3);
            var second = Mobile.LiftItemDupe(stack, stack.Amount - 3);

            Assert.Equal(5, stack.GridSlot);
            Assert.Equal(1, first.GridSlot);
            Assert.Equal(2, second.GridSlot);
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void SwapGridSlot_LiftPattern_RestKeepsStackCell()
    {
        // Mobile.Lift: the original is lifted, the new item is the rest that stays.
        var (cont, stack) = NewStackInSlot5();

        try
        {
            var rest = Mobile.LiftItemDupe(stack, 4);
            rest.SwapGridSlot(stack);

            Assert.Equal(5, rest.GridSlot);
            Assert.Equal(1, stack.GridSlot);
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void Container_RoundTrip_KeepsSlotAndCodegenData()
    {
        // Container data is written by the generator after base.Serialize; the new byte must not shift it.
        var cont = new Container(World.NewItem) { MaxItems = 50, GumpID = 0x3C, LiftOverride = true, GridSlot = 7 };
        var restored = new Container((Serial)0x7ffff202u);

        try
        {
            var writer = new BufferWriter(new byte[512], true);
            cont.Serialize(writer);
            var bytes = writer.Buffer[..(int)writer.Position];

            restored.Deserialize(new BufferReader(bytes));

            Assert.Equal(7, restored.GridSlot);
            Assert.Equal(50, restored.MaxItems);
            Assert.Equal(0x3C, restored.GumpID);
            Assert.True(restored.LiftOverride);
        }
        finally
        {
            cont.Delete();
            restored.Delete();
        }
    }

    [Fact]
    public void StackWith_TargetKeepsSlot()
    {
        var cont = NewContainer();
        var dropped = new StackableItem(World.NewItem) { Amount = 3 };

        try
        {
            AddNew(cont);
            var target = new StackableItem(World.NewItem) { Amount = 2 };
            cont.AddItem(target);
            target.GridSlot = 12;

            Assert.True(target.StackWith(null, dropped, false));
            Assert.Equal(5, target.Amount);
            Assert.Equal(12, target.GridSlot);
            Assert.True(dropped.Deleted);
        }
        finally
        {
            cont.Delete();
            dropped.Delete();
        }
    }

    [Fact]
    public void AssignGridSlots_FillsMissingAndDuplicates()
    {
        var cont = NewContainer(4);

        try
        {
            var a = AddNew(cont);
            var b = AddNew(cont);
            var c = AddNew(cont);
            var d = AddNew(cont);
            var e = AddNew(cont);

            // Saved state as an old save or a broken one leaves it: none, duplicates, out of range.
            a._gridSlot = 2;
            b._gridSlot = Item.NoGridSlot;
            c._gridSlot = 2;
            d._gridSlot = 77;
            e._gridSlot = Item.NoGridSlot;

            var changed = cont.AssignGridSlots();

            Assert.Equal(2, a.GridSlot);
            Assert.Equal(0, b.GridSlot);
            Assert.Equal(1, c.GridSlot);
            Assert.Equal(3, d.GridSlot);
            Assert.Equal(Item.NoGridSlot, e.GridSlot);
            Assert.Equal(3, changed);

            Assert.Equal(0, cont.AssignGridSlots());
        }
        finally
        {
            cont.Delete();
        }
    }

    [Fact]
    public void ContainerPackets_SendTheSlot()
    {
        var cont = NewContainer();

        var m = new Mobile((Serial)0x1);
        m.DefaultMobileInit();
        m.AccessLevel = AccessLevel.Administrator;
        m.Map = Map.Felucca;

        try
        {
            AddNew(cont);
            var item = AddNew(cont);
            item.GridSlot = 42;

            using var ns = PacketTestUtilities.CreateTestNetState();
            ns.ProtocolChanges |= ProtocolChanges.ContainerGridLines;
            ns.SendContainerContentUpdate(item);
            var update = ns.SendBuffer.GetReadSpan();
            Assert.Equal(0x25, update[0]);
            Assert.Equal(42, update[14]);

            using var ns2 = PacketTestUtilities.CreateTestNetState();
            ns2.ProtocolChanges |= ProtocolChanges.ContainerGridLines;
            ns2.SendContainerContent(m, cont);
            var content = ns2.SendBuffer.GetReadSpan();
            Assert.Equal(0x3C, content[0]);
            Assert.Equal(0, content[5 + 13]);
            Assert.Equal(42, content[5 + 20 + 13]);
        }
        finally
        {
            cont.Delete();
            m.Delete();
        }
    }
}
