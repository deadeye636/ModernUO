using System;
using System.Collections.Generic;
using Server.Network;

namespace Server.Misc;

/// <summary>
/// Staff control of a facet's season at runtime. Changes <see cref="Map.Season"/> and sends packet 0xBC to the
/// players on that facet at once; the engine itself sends the season only at login and on a facet change.
/// Facets are not saved, so a change lasts until the next restart.
/// </summary>
public static class AmbientSeason
{
    public const int Auto = -1;
    public const int MaxSeason = 4;

    private static readonly string[] _names = ["spring", "summer", "fall", "winter", "desolation"];

    // The season each facet had before its first change; holds only facets that were changed.
    private static readonly Dictionary<Map, int> _defaults = [];

    public static void Configure()
    {
        CommandSystem.Register("Season", AccessLevel.GameMaster, Season_OnCommand);
    }

    public static string GetName(int season) =>
        season >= 0 && season < _names.Length ? _names[season] : season.ToString();

    public static bool IsOverridden(Map map) => map != null && _defaults.ContainsKey(map);

    public static int GetDefault(Map map) => _defaults.TryGetValue(map, out var season) ? season : map.Season;

    /// <summary>Parses a season name, its number (0-4) or "auto" (<see cref="Auto"/>).</summary>
    public static bool TryParse(string value, out int season)
    {
        season = Auto;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();

        if (value.InsensitiveEquals("auto"))
        {
            return true;
        }

        if (value.InsensitiveEquals("autumn"))
        {
            season = 2;
            return true;
        }

        for (var i = 0; i < _names.Length; i++)
        {
            if (value.InsensitiveEquals(_names[i]))
            {
                season = i;
                return true;
            }
        }

        if (int.TryParse(value, out var number) && number is >= 0 and <= MaxSeason)
        {
            season = number;
            return true;
        }

        return false;
    }

    /// <summary>Sets the season of <paramref name="map"/> and sends it to its players. Returns the players reached.</summary>
    public static int SetSeason(Map map, int season)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfNegative(season);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(season, MaxSeason);

        _defaults.TryAdd(map, map.Season);
        map.Season = season;

        return SendToPlayers(map);
    }

    /// <summary>Restores the season <paramref name="map"/> was loaded with. Returns the players reached.</summary>
    public static int Restore(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (!_defaults.Remove(map, out var season))
        {
            return 0;
        }

        map.Season = season;
        return SendToPlayers(map);
    }

    private static int SendToPlayers(Map map)
    {
        var count = 0;

        foreach (var ns in NetState.Instances)
        {
            var m = ns.Mobile;

            if (m?.Map != map)
            {
                continue;
            }

            ns.SendSeasonChange((byte)m.GetSeason(), true);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Resolves the facet a command acts on: the argument at <paramref name="index"/> when given, else the
    /// caller's facet. The internal facet is refused.
    /// </summary>
    internal static bool TryGetTargetMap(CommandEventArgs e, int index, out Map map)
    {
        if (e.Length > index)
        {
            if (!Map.TryParse(e.GetString(index), null, out map) || map == Map.Internal)
            {
                e.Mobile.SendMessage($"Unknown facet '{e.GetString(index)}'.");
                map = null;
                return false;
            }

            return true;
        }

        map = e.Mobile.Map;

        if (map == null || map == Map.Internal)
        {
            e.Mobile.SendMessage("You are not on a facet; name one.");
            map = null;
            return false;
        }

        return true;
    }

    [Usage("Season [spring|summer|fall|winter|desolation|0-4|auto] [map]")]
    [Description(
        "Sets the season of a facet (default: your facet) and sends it to its players at once. " +
        "'auto' restores the configured season. Not saved: a restart restores the configured seasons."
    )]
    internal static void Season_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (e.Length == 0)
        {
            if (TryGetTargetMap(e, 0, out var current))
            {
                var state = IsOverridden(current) ? "set by staff" : "configured";
                from.SendMessage(
                    $"Season of {current}: {GetName(current.Season)} ({state}; configured: {GetName(GetDefault(current))})."
                );
            }

            from.SendMessage("Usage: Season <spring|summer|fall|winter|desolation|0-4|auto> [map]");
            return;
        }

        if (!TryParse(e.GetString(0), out var season))
        {
            from.SendMessage("Usage: Season <spring|summer|fall|winter|desolation|0-4|auto> [map]");
            return;
        }

        if (!TryGetTargetMap(e, 1, out var map))
        {
            return;
        }

        if (season == Auto)
        {
            if (!IsOverridden(map))
            {
                from.SendMessage($"Season of {map} was not changed; it is {GetName(map.Season)}.");
                return;
            }

            var restored = Restore(map);
            from.SendMessage($"Season of {map} restored to {GetName(map.Season)} ({restored} players updated).");
            return;
        }

        var reached = SetSeason(map, season);
        from.SendMessage($"Season of {map} set to {GetName(season)} ({reached} players updated).");
    }
}
