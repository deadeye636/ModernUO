using System;
using System.Buffers;
using System.Buffers.Binary;
using Server;
using Server.Collections;
using Server.ContextMenus;
using Server.Network;
using Server.Tests.Network;
using Xunit;

namespace UOContent.Tests.Mobiles.AI;

internal class CountingContextMenuEntry : ContextMenuEntry
{
    public CountingContextMenuEntry(int number, bool enabled) : base(number) => Enabled = enabled;

    public int Clicks { get; private set; }

    public override void OnClick(Mobile from, IEntity target) => Clicks++;
}

internal class BankerLikeMenuItem : Item
{
    public BankerLikeMenuItem(bool bankEnabled = true) : base(0x0EED) =>
        BankEntry = new CountingContextMenuEntry(6105, bankEnabled);

    public CountingContextMenuEntry BankEntry { get; }

    public override void GetContextMenuEntries(Mobile from, ref PooledRefList<ContextMenuEntry> list)
    {
        base.GetContextMenuEntries(from, ref list);

        list.Add(new ContextMenuEntry(6123)); // Open Paperdoll
        list.Add(new ContextMenuEntry(6103)); // Buy
        list.Add(new ContextMenuEntry(6104)); // Sell
        list.Add(BankEntry);                  // Open Bankbox
    }
}

[Collection("Sequential UOContent Tests")]
public class ContextMenuEntryIndexTests
{
    private const string EnhancedClient = "67.0.117";
    private const string ClassicClient = "7.0.74.28";

    private static void WithMenu(Action<ContextMenu> test)
    {
        var m = new Mobile(World.NewMobile);
        m.DefaultMobileInit();
        var item = new BankerLikeMenuItem();

        try
        {
            test(ContextMenuSystem.CreateContextMenu(m, item));
        }
        finally
        {
            item.Delete();
            m.Delete();
        }
    }

    private static NetState CreateNetState(string version)
    {
        var ns = PacketTestUtilities.CreateTestNetState();
        ns.Version = new ClientVersion(version);
        return ns;
    }

    [Theory]
    [InlineData(120, 3)] // Open Bankbox
    [InlineData(110, 1)] // Buy
    [InlineData(111, 2)] // Sell
    public void TestEnhancedClientActionCodeResolvesToEntry(int actionCode, int expectedIndex)
    {
        using var ns = CreateNetState(EnhancedClient);
        WithMenu(menu => Assert.Equal(expectedIndex, ContextMenuSystem.GetEntryIndex(ns, menu, actionCode)));
    }

    [Fact]
    public void TestClassicClientActionCodeIsUnchanged()
    {
        using var ns = CreateNetState(ClassicClient);
        WithMenu(menu => Assert.Equal(120, ContextMenuSystem.GetEntryIndex(ns, menu, 120)));
    }

    [Fact]
    public void TestWithoutNetStateActionCodeIsUnchanged()
    {
        WithMenu(menu => Assert.Equal(120, ContextMenuSystem.GetEntryIndex(null, menu, 120)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void TestEnhancedClientEntryIndexIsUnchanged(int index)
    {
        using var ns = CreateNetState(EnhancedClient);
        WithMenu(menu => Assert.Equal(index, ContextMenuSystem.GetEntryIndex(ns, menu, index)));
    }

    [Theory]
    [InlineData(130)] // Command: Guard, not in this menu
    [InlineData(919)] // Rename, handled by the client itself
    [InlineData(0x17D8)]
    public void TestEnhancedClientCodeWithoutMatchingEntryIsUnchanged(int actionCode)
    {
        using var ns = CreateNetState(EnhancedClient);
        WithMenu(menu => Assert.Equal(actionCode, ContextMenuSystem.GetEntryIndex(ns, menu, actionCode)));
    }

    [Theory]
    [InlineData(EnhancedClient, true, 1)]
    [InlineData(EnhancedClient, false, 0)]
    [InlineData(ClassicClient, true, 0)]
    public void TestResponseWithActionCodeClicksOnlyEnabledEntry(string version, bool bankEnabled, int expectedClicks)
    {
        using var ns = CreateNetState(version);

        var m = new Mobile(World.NewMobile);
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(1000, 1000, 0), Map.Felucca);
        ns.Mobile = m;

        var item = new BankerLikeMenuItem(bankEnabled);
        item.MoveToWorld(new Point3D(1001, 1000, 0), Map.Felucca);

        try
        {
            Span<byte> buffer = stackalloc byte[6];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, item.Serial.Value);
            BinaryPrimitives.WriteUInt16BigEndian(buffer[4..], 120); // Open Bankbox

            ContextMenuSystem.ContextMenuRequest(ns, new SpanReader(buffer[..4]));
            ContextMenuSystem.ContextMenuResponse(ns, new SpanReader(buffer));

            Assert.Equal(expectedClicks, item.BankEntry.Clicks);
        }
        finally
        {
            ns.Mobile = null;
            item.Delete();
            m.Delete();
        }
    }
}
