using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Server.Collections;
using Server.Network;

namespace Server.ContextMenus;

public static class ContextMenuSystem
{
    private static readonly Dictionary<Mobile, ContextMenu> _menus = [];

    public static unsafe void Configure()
    {
        IncomingExtendedCommandPackets.RegisterExtended(0x13, true, &ContextMenuRequest);
        IncomingExtendedCommandPackets.RegisterExtended(0x15, true, &ContextMenuResponse);
    }

    public static ContextMenu CreateContextMenu(Mobile from, IEntity target)
    {
        if (target?.Deleted != false)
        {
            return new ContextMenu(from, target, []);
        }

        var list = PooledRefList<ContextMenuEntry>.Create();

        if (target is Mobile mobile)
        {
            if (mobile.CanPaperdollBeOpenedBy(from))
            {
                list.Add(new PaperdollEntry());
            }

            mobile.GetContextMenuEntries(from, ref list);
        }
        else if (target is Item item)
        {
            item.GetContextMenuEntries(from, ref list);
        }

        var entries = list.ToArray();
        list.Dispose();

        return new ContextMenu(from, target, entries);
    }

    public static void ContextMenuResponse(NetState state, SpanReader reader)
    {
        var from = state.Mobile;

        if (from == null || !_menus.Remove(from, out var menu) || from != menu.From)
        {
            return;
        }

        var entity = World.FindEntity((Serial)reader.ReadUInt32());

        if (entity == null || entity != menu.Target || !from.CanSee(entity))
        {
            return;
        }

        Point3D p;

        if (entity is Mobile)
        {
            p = entity.Location;
        }
        else if (entity is Item item)
        {
            p = item.GetWorldLocation();
        }
        else
        {
            return;
        }

        var index = GetEntryIndex(state, menu, reader.ReadUInt16());

        if (index >= menu.Entries.Length)
        {
            return;
        }

        var e = menu.Entries[index];

        var range = e.Range;

        if (range == -1)
        {
            range = 18;
        }

        if (e.Enabled && from.InRange(p, range))
        {
            e.OnClick(from, entity);
        }
    }

    /// <summary>
    ///     Resolves the index a client answered a context menu with to an entry index.
    ///     The Enhanced Client's shortcut buttons (target window, health bar, player actions, double-click on a
    ///     banker or vendor) request the menu hidden and answer with a fixed action code instead of the entry index.
    /// </summary>
    /// <returns>The entry index, or <paramref name="index" /> unchanged when it is not such an action code.</returns>
    public static int GetEntryIndex(NetState state, ContextMenu menu, int index)
    {
        if (index < menu.Entries.Length || state?.IsEnhancedClient != true)
        {
            return index;
        }

        var number = GetEnhancedClientActionNumber(index);

        if (number == 0)
        {
            return index;
        }

        var entries = menu.Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Number == number)
            {
                return i;
            }
        }

        return index;
    }

    // Action codes from ContextMenu.DefaultValues in the Enhanced Client's default UI (ContextMenu.lua),
    // mapped to the cliloc of the entry they stand for. 520 (paperdoll) is never sent by the default UI, and
    // 622 (unpack transfer crate) and 701 (load shuriken) have no context menu entry here.
    private static int GetEnhancedClientActionNumber(int actionCode) =>
        actionCode switch
        {
            110 => 3006103, // Buy
            111 => 3006104, // Sell
            120 => 3006105, // Open Bankbox
            130 => 3006107, // Command: Guard
            131 => 3006108, // Command: Follow
            134 => 3006111, // Command: Kill
            135 => 3006114, // Command: Stay
            137 => 3006112, // Command: Stop
            301 => 3006130, // Tame
            303 => 3006146, // Talk
            308 => 3006157, // Cancel Protection
            320 => 1113797, // Enable PvP Warning
            403 => 3006152, // Bulk Order Info
            404 => 3006154, // View Quest Log
            405 => 3006155, // Cancel Quest
            406 => 3006156, // Quest Conversation
            416 => 1114299, // Open Item Insurance Menu
            418 => 3006201, // Toggle Item Insurance
            419 => 1152294, // Bribe
            602 => 3006205, // Release Co-Ownership
            604 => 3006207, // Leave House
            801 => 3006169, // Toggle Quest Item
            810 => 3000197, // Add Party Member
            811 => 3000198, // Remove Party Member
            820 => 3006168, // Siege Bless Item
            915 => 1049594, // Loyalty Rating
            918 => 1115022, // Open Titles Menu
            1010 => 1152531, // Void Pool
            1013 => 1154112, // Allow Trades
            1014 => 1154113, // Refuse Trades
            _ => 0
        };

    public static void ContextMenuRequest(NetState state, SpanReader reader)
    {
        var from = state.Mobile;
        var target = World.FindEntity((Serial)reader.ReadUInt32());

        if (from == null || target == null || from.Map != target.Map || !from.CanSee(target))
        {
            return;
        }

        var item = target as Item;

        var checkLocation = item?.GetWorldLocation() ?? target.Location;
        if (!(Utility.InUpdateRange(from.Location, checkLocation) && from.CheckContextMenuDisplay(target)))
        {
            return;
        }

        var c = CreateContextMenu(from, target);

        if (c.Entries.Length <= 0)
        {
            return;
        }

        if (item?.RootParent is Mobile mobile && mobile != from && mobile.AccessLevel >= from.AccessLevel)
        {
            for (var i = 0; i < c.Entries.Length; ++i)
            {
                var entry = c.Entries[i];
                if (!entry.NonLocalUse)
                {
                    entry.Enabled = false;
                }
            }
        }

        _menus[from] = c;
        state.SendDisplayContextMenu(c);
    }

    public static void SendDisplayContextMenu(this NetState ns, ContextMenu menu)
    {
        if (ns == null || menu == null)
        {
            return;
        }

        // The Enhanced Client only parses the cliloc-based format 2.
        var newCommand = ns.IsEnhancedClient || (ns.NewHaven && menu.RequiresNewPacket);

        var entries = menu.Entries;
        var entriesLength = (byte)entries.Length;
        var maxLength = 12 + entriesLength * 8;

        var writer = new SpanWriter(stackalloc byte[maxLength]);
        writer.Write((byte)0xBF);                        // Packet ID
        writer.Seek(2, SeekOrigin.Current);              // Length
        writer.Write((short)0x14);                       // Subpacket
        writer.Write((short)(newCommand ? 0x02 : 0x01)); // Command

        var target = menu.Target;
        writer.Write(target.Serial);
        writer.Write(entriesLength);

        var p = target switch
        {
            Mobile _  => target.Location,
            Item item => item.GetWorldLocation(),
            _         => Point3D.Zero
        };

        for (var i = 0; i < entriesLength; ++i)
        {
            var e = entries[i];

            var range = e.Range;

            if (range == -1)
            {
                range = Core.GlobalUpdateRange;
            }

            var flags = e.Flags;
            if (!(e.Enabled && menu.From.InRange(p, range)))
            {
                flags |= CMEFlags.Disabled;
            }

            if (newCommand)
            {
                writer.Write(e.Number);
                writer.Write((short)i);
                writer.Write((short)flags);
            }
            else
            {

                writer.Write((short)i);
                writer.Write((ushort)(e.Number - 3000000));

                var color = e.Color & 0xFFFF;

                if (color != 0xFFFF)
                {
                    flags |= CMEFlags.Colored;
                }

                writer.Write((short)flags);

                if ((flags & CMEFlags.Colored) != 0)
                {
                    writer.Write((short)color);
                }
            }
        }

        writer.WritePacketLength();
        ns.Send(writer.Span);
    }
}
