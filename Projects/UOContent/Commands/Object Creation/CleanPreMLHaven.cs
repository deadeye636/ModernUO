using System;
using System.Collections.Generic;
using System.IO;
using Server.Engines.Spawners;
using Server.Items;
using Server.Json;
using Server.Logging;
using Server.Mobiles;
using Server.Multis;

namespace Server.Commands;

/// <summary>
/// One-off repair for an ML or later world that was decorated before the pre-ML Haven data was gated:
/// removes from Trammel what <see cref="Decorate"/> and <see cref="SignParser"/> placed from the pre-ML
/// Haven files, and applies the Haven island spawner fixes of the post-uoml/shared spawn data.
/// Only objects that match those data entries exactly are touched; running it again changes nothing.
/// </summary>
public static class CleanPreMLHaven
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(CleanPreMLHaven));

    // Mobiles a deleted pre-ML spawner left behind are searched this far from it.
    private const int OrphanRange = 12;

    // Spawners the ML+ spawn data does not place: Uzeraan's Turmoil Dryad on water, and the TownCrier and
    // escort spawners of the old Haven docks, which only the pre-SA uoml set carries.
    private static readonly (Guid Guid, Point2D Location)[] _removedSpawners =
    [
        (new Guid("044b2029-6882-4e73-90ff-e23baf6f6067"), new Point2D(3555, 2698)),
        (new Guid("8b75bcf8-7b83-4d94-8573-92aff88cd41f"), new Point2D(3652, 2619)),
        (new Guid("ce3d7ff4-6411-4a30-914a-6b2bd1b9c752"), new Point2D(3645, 2661))
    ];

    // Spawners a world imported before the importer compared Z may have lost: each stood on a tile shared
    // with another spawner of the same type, and only the last one imported survived. The spawn data gives
    // each its own tile; they are placed from there by GUID, as is the New Haven TownCrier.
    private static readonly (Guid Guid, string File)[] _placedSpawners =
    [
        (new Guid("c10e754e-66c4-432f-98a1-55619df323f4"), "post-uoml/trammel/Vendors.json"),
        (new Guid("398d0d16-59a2-4fa9-aaa1-5b86f083440a"), "post-uoml/trammel/Vendors.json"),
        (new Guid("94b1a002-db27-4710-b1b8-a959bb6de05e"), "post-uoml/trammel/Vendors.json"),
        (new Guid("f713041b-8980-4839-84cb-232cb39f710a"), "post-uoml/trammel/Vendors.json"),
        (new Guid("e76ec8dd-9238-46d2-82a6-5896e68b5431"), "post-uoml/trammel/Vendors.json"),
        (new Guid("8e30685f-b29d-4f6a-8d31-df3910837e8e"), "post-uoml/trammel/Vendors.json"),
        (new Guid("edd6b474-4629-4bf8-8d7c-53f560993456"), "post-uoml/trammel/Vendors.json"),
        (new Guid("dcd02218-4bb6-4737-a09d-1c1eb19ed14c"), "post-uoml/trammel/Vendors.json"),
        (new Guid("e316b602-c0f5-4c25-8a61-a5b5e01443c7"), "post-uoml/trammel/Vendors.json"),
        (new Guid("da916a95-fb6c-4605-866d-ebf58f55ec49"), "post-uoml/trammel/Vendors.json"),
        (new Guid("79e04477-5ab1-4276-a4fe-c37385548e20"), "post-uoml/trammel/Vendors.json"),
        (new Guid("12f15214-6492-4a29-807b-5023bc6e8184"), "post-uoml/trammel/Vendors.json"),
        (new Guid("95ed3464-8403-4c33-848f-2d419357f665"), "post-uoml/trammel/Vendors.json"),
        (new Guid("5ee08b2c-ccf7-4088-bc02-e745fe6fd7c3"), "post-uoml/trammel/TownsPeople.json")
    ];

    // The tiles the stacked spawners shared before they were split.
    private static readonly Point2D[] _stackedTiles =
    [
        new(3526, 2536),
        new(3463, 2558),
        new(3461, 2566),
        new(3446, 2606),
        new(3470, 2519)
    ];

    public static void Configure()
    {
        CommandSystem.Register("CleanPreMLHaven", AccessLevel.Developer, CleanPreMLHaven_OnCommand);
    }

    [Usage("CleanPreMLHaven [preview|apply]")]
    [Description(
        "ML and later: removes the pre-ML Haven decoration, signs and quest spawners from Trammel and applies " +
        "the Haven island spawner fixes. preview (default) only reports; both write a log under Logs/."
    )]
    private static void CleanPreMLHaven_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var mode = e.Length > 0 ? e.GetString(0) : "preview";
        var apply = mode.InsensitiveEquals("apply");

        if (!apply && !mode.InsensitiveEquals("preview"))
        {
            from.SendMessage("Usage: CleanPreMLHaven [preview|apply]");
            return;
        }

        if (!Core.ML)
        {
            from.SendMessage("CleanPreMLHaven: before ML the pre-ML Haven is the right one; nothing to do.");
            return;
        }

        var report = Run(Map.Trammel, apply);
        var path = WriteLog(report, from, apply);

        var verb = apply ? "removed" : "to remove";
        from.SendMessage(
            $"CleanPreMLHaven: {report.Decoration.Count} decoration items, {report.Signs.Count} signs, {report.Orphans.Count} orphaned mobiles and {report.RemovedSpawners.Count} spawners {verb}."
        );
        from.SendMessage(
            $"CleanPreMLHaven: {report.PlacedSpawners.Count} spawners {(apply ? "placed" : "to place")}, {report.SpawnersInPlace} in place, {report.KeptDoors.Count} doors kept in doorways, {report.Errors.Count} errors."
        );
        from.SendMessage($"CleanPreMLHaven: log written to Logs/CleanPreMLHaven/{Path.GetFileName(path)}");
    }

    public static Report Run(Map map, bool apply)
    {
        var report = new Report();

        FindPreMLDecoration(map, Path.Combine(Core.BaseDirectory, Decorate.PreMLTrammelFolder), report);
        FindPreMLSigns(map, Path.Combine(Core.BaseDirectory, SignParser.PreMLSignsFile), report);
        FindOrphans(map, report);

        if (Core.SA)
        {
            FindSpawnerFixes(map, Path.Combine(Core.BaseDirectory, "Data", "Spawns"), report);
        }
        else
        {
            report.Notes.Add("Spawner fixes skipped: the post-uoml spawn set is used from SA on.");
        }

        if (apply)
        {
            Apply(map, report);
        }

        return report;
    }

    /// <summary>
    /// Collects the world items the pre-ML cfgs in <paramref name="folder"/> generated on <paramref name="map"/>,
    /// except doors in a doorway DoorGen fills and items an active decoration set generates as well.
    /// </summary>
    public static void FindPreMLDecoration(Map map, string folder, Report report)
    {
        if (!Directory.Exists(folder))
        {
            report.Errors.Add($"Folder not found: {folder}");
            return;
        }

        var found = new List<Item>();

        foreach (var file in Directory.GetFiles(folder, "*.cfg"))
        {
            try
            {
                var lists = DecorationList.ReadAll(file);

                for (var i = 0; i < lists.Count; ++i)
                {
                    lists[i].FindGenerated(map, found);
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var active = FindActiveDecoration(map, found, report);

        for (var i = 0; i < found.Count; ++i)
        {
            var item = found[i];

            if (active.Contains(item))
            {
                report.KeptActive.Add(item);
            }
            // Decorate places every item immovable, so a movable match, or one inside a house (locked-down
            // items are immovable too), belongs to a player who happens to use the same tile.
            else if (item.Movable || BaseHouse.FindHouseAt(item) != null)
            {
                report.KeptPlayer.Add(item);
            }
            else if (item is BaseDoor door && IsDoorGenDoorway(map, ClosedLocation(door)))
            {
                report.KeptDoors.Add(item);
            }
            else
            {
                report.Decoration.Add(item);
            }
        }
    }

    private static Point3D ClosedLocation(BaseDoor door) =>
        door.Open ? new Point3D(door.X - door.Offset.X, door.Y - door.Offset.Y, door.Z - door.Offset.Z) : door.Location;

    // Items of the found set that a decoration set generated for this expansion places as well.
    private static HashSet<Item> FindActiveDecoration(Map map, List<Item> found, Report report)
    {
        var result = new HashSet<Item>();

        if (found.Count == 0)
        {
            return result;
        }

        var tiles = new HashSet<Point2D>();

        for (var i = 0; i < found.Count; ++i)
        {
            tiles.Add(new Point2D(found[i].X, found[i].Y));
        }

        var matches = new List<Item>();

        foreach (var (folder, maps) in Decorate.GetDecorationSets())
        {
            if (Array.IndexOf(maps, map) < 0)
            {
                continue;
            }

            var path = Path.Combine(Core.BaseDirectory, folder);

            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(path, "*.cfg"))
            {
                try
                {
                    var lists = DecorationList.ReadAll(file);

                    for (var i = 0; i < lists.Count; ++i)
                    {
                        if (TouchesAny(lists[i], tiles))
                        {
                            lists[i].FindGenerated(map, matches);
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        for (var i = 0; i < matches.Count; ++i)
        {
            result.Add(matches[i]);
        }

        return result;
    }

    private static bool TouchesAny(DecorationList list, HashSet<Point2D> tiles)
    {
        var entries = list.Entries;

        for (var i = 0; i < entries.Count; ++i)
        {
            var loc = entries[i].Location;

            if (tiles.Contains(new Point2D(loc.X, loc.Y)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when DoorGen's frame rule finds a doorway at <paramref name="p"/> (single door, or either leaf of a
    /// double door), so the tile is a real door of the map whatever generator filled it.
    /// </summary>
    public static bool IsDoorGenDoorway(Map map, Point3D p)
    {
        var x = p.X;
        var y = p.Y;

        return HasFrame(map, x - 1, y, p.Z, DoorGenerator.IsWestFrame) &&
               (HasFrame(map, x + 1, y, p.Z, DoorGenerator.IsEastFrame) ||
                HasFrame(map, x + 2, y, p.Z, DoorGenerator.IsEastFrame)) ||
               HasFrame(map, x - 2, y, p.Z, DoorGenerator.IsWestFrame) &&
               HasFrame(map, x + 1, y, p.Z, DoorGenerator.IsEastFrame) ||
               HasFrame(map, x, y - 1, p.Z, DoorGenerator.IsNorthFrame) &&
               (HasFrame(map, x, y + 1, p.Z, DoorGenerator.IsSouthFrame) ||
                HasFrame(map, x, y + 2, p.Z, DoorGenerator.IsSouthFrame)) ||
               HasFrame(map, x, y - 2, p.Z, DoorGenerator.IsNorthFrame) &&
               HasFrame(map, x, y + 1, p.Z, DoorGenerator.IsSouthFrame);
    }

    private static bool HasFrame(Map map, int x, int y, int z, Func<int, bool> isFrame)
    {
        foreach (var tile in map.Tiles.GetStaticTiles(x, y))
        {
            // Same tolerance Decorate uses when it treats a door as the one already standing there.
            if (isFrame(tile.ID) && Math.Abs(tile.Z - z) < 8)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects the signs SignGen placed from the pre-ML signs file: a <see cref="Sign"/> with the same graphic
    /// at the exact location, the rule SignGen uses to replace its own signs.
    /// </summary>
    public static void FindPreMLSigns(Map map, string file, Report report)
    {
        if (!File.Exists(file))
        {
            report.Errors.Add($"File not found: {file}");
            return;
        }

        var activeFile = Path.Combine(Core.BaseDirectory, "Data", "signs.cfg");
        var active = File.Exists(activeFile) ? SignParser.ReadAll(activeFile) : [];
        var entries = SignParser.ReadAll(file);

        for (var i = 0; i < entries.Count; ++i)
        {
            var e = entries[i];

            if (!PlacesOn(e, map) || IsActiveSign(active, e, map))
            {
                continue;
            }

            foreach (var item in map.GetItemsAt(e.m_Location.X, e.m_Location.Y))
            {
                if (item is Sign && item.Z == e.m_Location.Z && item.ItemID == e.m_ItemID && !report.Signs.Contains(item))
                {
                    report.Signs.Add(item);
                }
            }
        }
    }

    private static bool PlacesOn(SignParser.SignEntry e, Map map) =>
        Array.IndexOf(SignParser.GetMaps(e.m_Map) ?? [], map) >= 0;

    private static bool IsActiveSign(List<SignParser.SignEntry> active, SignParser.SignEntry e, Map map)
    {
        for (var i = 0; i < active.Count; ++i)
        {
            var a = active[i];

            if (a.m_Location == e.m_Location && a.m_ItemID == e.m_ItemID && PlacesOn(a, map))
            {
                return true;
            }
        }

        return false;
    }

    // Mobiles of the types a pre-ML spawner spawns that stand near it without belonging to any spawner,
    // e.g. after the spawner lost track of them.
    private static void FindOrphans(Map map, Report report)
    {
        for (var i = 0; i < report.Decoration.Count; ++i)
        {
            if (report.Decoration[i] is not BaseSpawner spawner)
            {
                continue;
            }

            foreach (var m in map.GetMobilesInRange(spawner.Location, OrphanRange))
            {
                if (m.Player || m is BaseCreature { Controlled: true } || !SpawnsType(spawner, m.GetType()))
                {
                    continue;
                }

                if (m.Spawner == spawner)
                {
                    report.SpawnedMobiles++;
                }
                else if (m.Spawner is not { Deleted: false } && !report.Orphans.Contains(m))
                {
                    report.Orphans.Add(m);
                }
            }
        }
    }

    private static bool SpawnsType(BaseSpawner spawner, Type type)
    {
        var entries = spawner.Entries;

        for (var i = 0; i < entries.Count; ++i)
        {
            if (type.Name.InsensitiveEquals(entries[i].SpawnedName))
            {
                return true;
            }
        }

        return false;
    }

    private static void FindSpawnerFixes(Map map, string spawnRoot, Report report)
    {
        for (var i = 0; i < _removedSpawners.Length; ++i)
        {
            var (guid, loc) = _removedSpawners[i];
            var spawner = FindSpawner(map, guid, loc);

            // Uzeraan's Turmoil also placed its Dryad spawner, so it can already be in the decoration set.
            if (spawner != null && !report.Decoration.Contains(spawner))
            {
                report.RemovedSpawners.Add(spawner);
            }
        }

        var dtosByFile = new Dictionary<string, List<SpawnerDto>>();

        for (var i = 0; i < _placedSpawners.Length; ++i)
        {
            var (guid, file) = _placedSpawners[i];

            if (!dtosByFile.TryGetValue(file, out var dtos))
            {
                var path = Path.Combine(spawnRoot, file);

                try
                {
                    dtos = JsonConfig.Deserialize<List<SpawnerDto>>(path, SpawnerJsonSerializer.Options) ?? [];
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{file}: {ex.Message}");
                    dtos = [];
                }

                dtosByFile[file] = dtos;
            }

            var dto = dtos.Find(d => d.Guid == guid);

            if (dto == null)
            {
                report.Errors.Add($"Spawner {guid} not found in {file}.");
                continue;
            }

            if (dto.Map != map)
            {
                continue;
            }

            var target = new Point2D(dto.Location.X, dto.Location.Y);
            var existing = FindSpawner(map, guid, target);

            if (existing?.Location == dto.Location)
            {
                report.SpawnersInPlace++;
                continue;
            }

            existing ??= FindSpawner(map, guid, _stackedTiles);
            report.PlacedSpawners.Add((dto, existing));
        }
    }

    private static BaseSpawner FindSpawner(Map map, Guid guid, params ReadOnlySpan<Point2D> tiles)
    {
        for (var i = 0; i < tiles.Length; ++i)
        {
            foreach (var spawner in map.GetItemsAt<BaseSpawner>(tiles[i]))
            {
                if (spawner.Guid == guid)
                {
                    return spawner;
                }
            }
        }

        return null;
    }

    private static void Apply(Map map, Report report)
    {
        for (var i = 0; i < report.Decoration.Count; ++i)
        {
            report.Decoration[i].Delete();
        }

        for (var i = 0; i < report.Signs.Count; ++i)
        {
            report.Signs[i].Delete();
        }

        for (var i = 0; i < report.Orphans.Count; ++i)
        {
            report.Orphans[i].Delete();
        }

        for (var i = 0; i < report.RemovedSpawners.Count; ++i)
        {
            report.RemovedSpawners[i].Delete();
        }

        for (var i = 0; i < report.PlacedSpawners.Count; ++i)
        {
            var (dto, existing) = report.PlacedSpawners[i];
            existing?.Delete();

            try
            {
                var spawner = dto.ToSpawner();
                spawner.MoveToWorld(dto.Location, map);
                spawner.Respawn();
            }
            catch (Exception ex)
            {
                report.Errors.Add($"Spawner {dto.Guid}: {ex.Message}");
                logger.Error(ex, "CleanPreMLHaven: failed to place spawner {Guid}", dto.Guid);
            }
        }
    }

    private static string WriteLog(Report report, Mobile from, bool apply)
    {
        var dir = Path.Combine(Core.BaseDirectory, "Logs", "CleanPreMLHaven");
        PathUtility.EnsureDirectory(dir);

        var path = Path.Combine(dir, $"{Core.Now:yyyyMMdd-HHmmss}-{(apply ? "apply" : "preview")}.log");

        using var op = new StreamWriter(path);
        op.WriteLine($"# CleanPreMLHaven {(apply ? "apply" : "preview")} by {CommandLogging.Format(from)} at {Core.Now:u}");
        op.WriteLine($"# expansion {Core.Expansion}; {(apply ? "deleted / placed" : "would delete / place")}");

        WriteItems(op, "decoration", report.Decoration);
        WriteItems(op, "sign", report.Signs);
        WriteItems(op, "kept-door-in-doorway", report.KeptDoors);
        WriteItems(op, "kept-active-decoration", report.KeptActive);
        WriteItems(op, "kept-player-item", report.KeptPlayer);

        for (var i = 0; i < report.Orphans.Count; ++i)
        {
            var m = report.Orphans[i];
            op.WriteLine($"orphan\t{m.Serial}\t{m.GetType().Name}\t{m.Location}");
        }

        op.WriteLine($"spawned-mobiles-removed-with-their-spawner\t{report.SpawnedMobiles}");
        WriteItems(op, "removed-spawner", report.RemovedSpawners);

        for (var i = 0; i < report.PlacedSpawners.Count; ++i)
        {
            var (dto, existing) = report.PlacedSpawners[i];
            var was = existing == null ? "missing" : existing.Location.ToString();
            op.WriteLine($"placed-spawner\t{dto.Guid}\t{SpawnNames(dto)}\t{was} -> {dto.Location}");
        }

        op.WriteLine($"spawners-already-in-place\t{report.SpawnersInPlace}");

        for (var i = 0; i < report.Notes.Count; ++i)
        {
            op.WriteLine($"note\t{report.Notes[i]}");
        }

        for (var i = 0; i < report.Errors.Count; ++i)
        {
            op.WriteLine($"error\t{report.Errors[i]}");
        }

        op.WriteLine(
            $"# totals: decoration {report.Decoration.Count}, signs {report.Signs.Count}, " +
            $"spawned {report.SpawnedMobiles}, orphans {report.Orphans.Count}, kept doors {report.KeptDoors.Count}, " +
            $"kept active {report.KeptActive.Count}, kept player items {report.KeptPlayer.Count}, removed spawners {report.RemovedSpawners.Count}, " +
            $"placed spawners {report.PlacedSpawners.Count}, in place {report.SpawnersInPlace}, errors {report.Errors.Count}"
        );

        return path;
    }

    private static void WriteItems(StreamWriter op, string kind, List<Item> items)
    {
        for (var i = 0; i < items.Count; ++i)
        {
            var item = items[i];
            var name = item is BaseSpawner s ? $"{item.GetType().Name}[{SpawnNames(s.Entries)}]" : item.GetType().Name;
            op.WriteLine($"{kind}\t{item.Serial}\t{name}\t0x{item.ItemID:X4}\t{item.Location}");
        }
    }

    private static string SpawnNames(SpawnerDto dto) => SpawnNames(dto.EntryView);

    private static string SpawnNames(IReadOnlyList<SpawnerEntry> entries)
    {
        if (entries == null || entries.Count == 0)
        {
            return "";
        }

        var names = new string[entries.Count];

        for (var i = 0; i < entries.Count; ++i)
        {
            names[i] = entries[i].SpawnedName;
        }

        return string.Join(",", names);
    }

    public class Report
    {
        public List<Item> Decoration { get; } = [];
        public List<Item> Signs { get; } = [];
        public List<Item> KeptDoors { get; } = [];
        public List<Item> KeptActive { get; } = [];
        public List<Item> KeptPlayer { get; } = [];
        public List<Mobile> Orphans { get; } = [];
        public int SpawnedMobiles { get; set; }
        public List<Item> RemovedSpawners { get; } = [];
        public List<(SpawnerDto Dto, BaseSpawner Existing)> PlacedSpawners { get; } = [];
        public int SpawnersInPlace { get; set; }
        public List<string> Notes { get; } = [];
        public List<string> Errors { get; } = [];
    }
}
