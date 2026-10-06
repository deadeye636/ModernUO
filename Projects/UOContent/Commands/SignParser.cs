using System;
using System.Collections.Generic;
using System.IO;
using Server.Collections;
using Server.Items;
using Server.Network;

namespace Server.Commands
{
    public static class SignParser
    {
        // Signs of the pre-ML Haven (Trammel); Haven island was rebuilt as New Haven in ML.
        public const string PreMLSignsFile = "Data/signs-preml.cfg";

        public static void Configure()
        {
            CommandSystem.Register("SignGen", AccessLevel.Developer, SignGen_OnCommand);
        }

        [Usage("SignGen")]
        [Description("Generates world/shop signs on all facets.")]
        public static void SignGen_OnCommand(CommandEventArgs c)
        {
            Parse(c.Mobile);
        }

        public static void Parse(Mobile from)
        {
            var cfg = Path.Combine(Core.BaseDirectory, "Data/signs.cfg");

            if (File.Exists(cfg))
            {
                from.SendMessage("Generating signs, please wait.");

                NetState.FlushAll();

                Generate(ReadAll(cfg));

                var preML = Path.Combine(Core.BaseDirectory, PreMLSignsFile);

                if (!Core.ML && File.Exists(preML))
                {
                    Generate(ReadAll(preML));
                }

                from.SendMessage("Sign generating complete.");
            }
            else
            {
                from.SendMessage($"{cfg} not found!");
            }
        }

        public static List<SignEntry> ReadAll(string path)
        {
            var list = new List<SignEntry>();

            using var ip = new StreamReader(path);
            string line;

            while ((line = ip.ReadLine()) != null)
            {
                var split = line.Split(' ');

                var e = new SignEntry(
                    line[(split[0].Length + 1 + split[1].Length + 1 + split[2].Length + 1 +
                          split[3].Length + 1 + split[4].Length + 1)..],
                    new Point3D(Utility.ToInt32(split[2]), Utility.ToInt32(split[3]), Utility.ToInt32(split[4])),
                    Utility.ToInt32(split[1]),
                    Utility.ToInt32(split[0])
                );

                list.Add(e);
            }

            return list;
        }

        public static Map[] GetMaps(int mapIndex) =>
            mapIndex switch
            {
                0 => [Map.Felucca, Map.Trammel],
                1 => [Map.Felucca],
                2 => [Map.Trammel],
                3 => [Map.Ilshenar],
                4 => [Map.Malas],
                5 => [Map.Tokuno],
                _ => null
            };

        private static void Generate(List<SignEntry> list)
        {
            for (var i = 0; i < list.Count; ++i)
            {
                var e = list[i];
                var maps = GetMaps(e.m_Map);

                for (var j = 0; maps?.Length > j; ++j)
                {
                    Add_Static(e.m_ItemID, e.m_Location, maps[j], e.m_Text);
                }
            }
        }

        public static void Add_Static(int itemID, Point3D location, Map map, string name)
        {
            using var queue = PooledRefQueue<Item>.Create();
            foreach (var item in map.GetItemsAt(location))
            {
                if (item is Sign && item.Z == location.Z && item.ItemID == itemID)
                {
                    queue.Enqueue(item);
                }
            }

            while (queue.Count > 0)
            {
                queue.Dequeue().Delete();
            }

            Item sign;

            if (name.StartsWithOrdinal("#"))
            {
                sign = new LocalizedSign(itemID, Utility.ToInt32(name.AsSpan()[1..]));
            }
            else
            {
                sign = new Sign(itemID) { Name = name };
            }

            if (map == Map.Malas)
            {
                sign.Hue = location.X switch
                {
                    >= 965 when location.Y >= 502 && location.X <= 1012 && location.Y <= 537  => 0x47E,
                    >= 1960 when location.Y >= 1278 && location.X < 2106 && location.Y < 1413 => 0x44E,
                    _                                                                         => sign.Hue
                };
            }

            sign.MoveToWorld(location, map);
        }

        public class SignEntry
        {
            public readonly int m_ItemID;
            public readonly Point3D m_Location;
            public readonly int m_Map;
            public readonly string m_Text;

            public SignEntry(string text, Point3D pt, int itemID, int mapLoc)
            {
                m_Text = text;
                m_Location = pt;
                m_ItemID = itemID;
                m_Map = mapLoc;
            }
        }
    }
}
