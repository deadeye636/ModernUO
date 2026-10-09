using System;
using System.Collections.Generic;

namespace Server.Misc;

/// <summary>The zone and rule that apply at a point, with the distance to that zone's edge.</summary>
public readonly record struct AmbienceMatch(AmbienceZone Zone, int RuleIndex, int EdgeDistance)
{
    public AmbienceRule Rule => Zone?.Rules[RuleIndex];
}

/// <summary>
/// Per-map grid of 32 x 32 tile cells. Each cell lists the zones whose rectangles touch it, highest priority first,
/// then file order. Built once per zone file and never changed afterwards.
/// </summary>
public sealed class AmbienceGrid
{
    public const int CellShift = 5;
    public const int CellSize = 1 << CellShift;

    private readonly Dictionary<Map, MapCells> _maps;

    private AmbienceGrid(Dictionary<Map, MapCells> maps) => _maps = maps;

    private sealed class MapCells
    {
        public int Width;
        public int Height;
        public int CellsX;

        // null where no zone touches the cell
        public AmbienceZone[][] Cells;
    }

    public static AmbienceGrid Build(AmbienceZone[] zones)
    {
        var lists = new Dictionary<Map, List<AmbienceZone>[]>();
        var maps = new Dictionary<Map, MapCells>();

        foreach (var zone in zones)
        {
            foreach (var map in zone.Maps)
            {
                if (!maps.TryGetValue(map, out var cells))
                {
                    cells = new MapCells
                    {
                        Width = map.Width,
                        Height = map.Height,
                        CellsX = (map.Width + CellSize - 1) >> CellShift
                    };
                    maps[map] = cells;
                    lists[map] = new List<AmbienceZone>[cells.CellsX * ((map.Height + CellSize - 1) >> CellShift)];
                }

                var cellLists = lists[map];

                foreach (var rect in zone.Rects)
                {
                    var x2 = Math.Min(rect.X2, map.Width) - 1;
                    var y2 = Math.Min(rect.Y2, map.Height) - 1;

                    for (var cy = rect.Y1 >> CellShift; cy <= y2 >> CellShift; cy++)
                    {
                        for (var cx = rect.X1 >> CellShift; cx <= x2 >> CellShift; cx++)
                        {
                            var list = cellLists[cy * cells.CellsX + cx] ??= [];

                            if (!list.Contains(zone))
                            {
                                list.Add(zone);
                            }
                        }
                    }
                }
            }
        }

        foreach (var (map, cells) in maps)
        {
            var cellLists = lists[map];
            cells.Cells = new AmbienceZone[cellLists.Length][];

            for (var i = 0; i < cellLists.Length; i++)
            {
                var list = cellLists[i];

                if (list == null)
                {
                    continue;
                }

                list.Sort(static (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Order.CompareTo(b.Order));
                cells.Cells[i] = list.ToArray();
            }
        }

        return new AmbienceGrid(maps);
    }

    /// <summary>Zones whose rectangles touch the cell of (x, y), highest priority first.</summary>
    public ReadOnlySpan<AmbienceZone> GetCandidates(Map map, int x, int y)
    {
        if (map == null || !_maps.TryGetValue(map, out var cells) || x < 0 || y < 0 || x >= cells.Width ||
            y >= cells.Height)
        {
            return ReadOnlySpan<AmbienceZone>.Empty;
        }

        return cells.Cells[(y >> CellShift) * cells.CellsX + (x >> CellShift)];
    }

    /// <summary>
    /// The highest-priority zone that holds (x, y) and has a rule for <paramref name="band"/> and
    /// <paramref name="weather"/>. A zone without a matching rule does not hide a lower one.
    /// </summary>
    public bool TryFind(Map map, int x, int y, AmbienceTimeBand band, AmbienceWeatherClass weather, out AmbienceMatch match)
    {
        var candidates = GetCandidates(map, x, y);

        for (var i = 0; i < candidates.Length; i++)
        {
            var zone = candidates[i];
            var distance = zone.EdgeDistance(x, y);

            if (distance < 0)
            {
                continue;
            }

            var rule = zone.FindRule(band, weather);

            if (rule >= 0)
            {
                match = new AmbienceMatch(zone, rule, distance);
                return true;
            }
        }

        match = default;
        return false;
    }

    /// <summary>The highest-priority zone holding (x, y), whether or not a rule matches; for the info command.</summary>
    public AmbienceZone FindZoneAt(Map map, int x, int y)
    {
        var candidates = GetCandidates(map, x, y);

        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].EdgeDistance(x, y) >= 0)
            {
                return candidates[i];
            }
        }

        return null;
    }
}
