using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Server.Json;
using Server.Logging;
using Server.Text;

namespace Server.Misc;

public enum AmbienceTimeBand
{
    Night,
    Dawn,
    Day,
    Dusk
}

public enum AmbienceWeatherClass
{
    Clear,
    Rain,
    Snow
}

/// <summary>A rectangle of an ambience zone. X2 and Y2 are exclusive, as for regions.</summary>
public readonly record struct AmbienceRect(int X1, int Y1, int X2, int Y2)
{
    public bool Contains(int x, int y) => x >= X1 && x < X2 && y >= Y1 && y < Y2;

    /// <summary>Tiles from (x, y) to the outermost row or column of this rectangle; 0 on that row or column.</summary>
    public int EdgeDistance(int x, int y) => Math.Min(Math.Min(x - X1, X2 - 1 - x), Math.Min(y - Y1, Y2 - 1 - y));
}

public sealed class AmbienceRule
{
    public const int AllTimes = (1 << 4) - 1;
    public const int AllWeathers = (1 << 3) - 1;

    public int TimeMask { get; init; } = AllTimes;
    public int WeatherMask { get; init; } = AllWeathers;
    public string PresetName { get; init; }
    public ushort PresetId { get; init; }

    /// <summary>0-1.</summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>Tenths of a second.</summary>
    public ushort FadeTenths { get; init; } = AmbienceZones.DefaultFadeTenths;

    public bool Matches(AmbienceTimeBand band, AmbienceWeatherClass weather) =>
        (TimeMask & (1 << (int)band)) != 0 && (WeatherMask & (1 << (int)weather)) != 0;
}

public sealed class AmbienceZone
{
    public string Id { get; init; }
    public Map[] Maps { get; init; }
    public int Priority { get; init; }

    /// <summary>Tiles over which the strength ramps from 0 at the zone edge to full inside; 0 = no ramp.</summary>
    public int Feather { get; init; }

    /// <summary>Position in the file; breaks priority ties, the zone listed first wins.</summary>
    public int Order { get; init; }

    public AmbienceRect[] Rects { get; init; }
    public AmbienceRule[] Rules { get; init; }

    /// <summary>
    /// Tiles from (x, y) to the edge of the zone, or -1 outside. With several rectangles the largest distance of the
    /// rectangles holding the point counts, so rectangles meant to merge should overlap by at least the feather.
    /// </summary>
    public int EdgeDistance(int x, int y)
    {
        var best = -1;

        for (var i = 0; i < Rects.Length; i++)
        {
            ref readonly var rect = ref Rects[i];

            if (rect.Contains(x, y))
            {
                best = Math.Max(best, rect.EdgeDistance(x, y));
            }
        }

        return best;
    }

    /// <summary>Index of the first rule matching band and weather, or -1.</summary>
    public int FindRule(AmbienceTimeBand band, AmbienceWeatherClass weather)
    {
        for (var i = 0; i < Rules.Length; i++)
        {
            if (Rules[i].Matches(band, weather))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>A validated, immutable zone file with its lookup grid. Replaced as a whole on reload.</summary>
public sealed class AmbienceZoneSet
{
    public static readonly AmbienceZoneSet Empty = new(0, new Dictionary<string, ushort>(), []);

    private readonly Dictionary<ushort, string> _presetNames = [];

    public AmbienceZoneSet(int presetTable, Dictionary<string, ushort> presets, AmbienceZone[] zones)
    {
        PresetTable = presetTable;
        Presets = presets;
        Zones = zones;

        foreach (var (name, id) in presets)
        {
            _presetNames[id] = name;
        }

        Grid = AmbienceGrid.Build(zones);
    }

    /// <summary>Version of the client preset table the names refer to; 0 for the empty set.</summary>
    public int PresetTable { get; }

    public IReadOnlyDictionary<string, ushort> Presets { get; }
    public AmbienceZone[] Zones { get; }
    public AmbienceGrid Grid { get; }

    public string GetPresetName(ushort id) =>
        id == 0 ? "none" : _presetNames.TryGetValue(id, out var name) ? name : id.ToString();

    public AmbienceZone FindZone(string id)
    {
        for (var i = 0; i < Zones.Length; i++)
        {
            if (Zones[i].Id.InsensitiveEquals(id))
            {
                return Zones[i];
            }
        }

        return null;
    }
}

/// <summary>
/// Loads <c>Data/Ambience/custom/ambience-zones.json</c>. The file is validated as a whole: a file with any error
/// is rejected, at startup the server then runs without zones, on reload the zones in use stay.
/// </summary>
public static class AmbienceZones
{
    public const int SchemaVersion = 1;
    public const int MaxFeather = 32;
    public const double MaxFadeSeconds = 60.0;
    public const ushort DefaultFadeTenths = 40;

    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AmbienceZones));

    private static string _filePath;

    // Resolved on first use: Core.BaseDirectory is only valid once the application assembly is set.
    public static string FilePath
    {
        get => _filePath ??= Path.Combine(Core.BaseDirectory, "Data", "Ambience", "custom", "ambience-zones.json");
        set => _filePath = value;
    }

    public static AmbienceZoneSet Current { get; private set; } = AmbienceZoneSet.Empty;

    public static void Initialize()
    {
        // Initialize, not Configure: map names are resolved, and the maps are registered during Configure.
        var errors = new List<string>();

        if (TryLoadFile(FilePath, out var set, errors))
        {
            Current = set;

            if (set != AmbienceZoneSet.Empty)
            {
                logger.Information("Ambience: {Count} zones loaded", set.Zones.Length);
            }

            return;
        }

        foreach (var error in errors)
        {
            logger.Warning("Ambience: {Error}", error);
        }

        logger.Warning("Ambience: zone file rejected, running without ambience zones");
    }

    /// <summary>Reads the file again and swaps it in when it validates; otherwise the zones in use stay.</summary>
    public static bool Reload(List<string> errors)
    {
        if (!TryLoadFile(FilePath, out var set, errors))
        {
            foreach (var error in errors)
            {
                logger.Warning("Ambience reload: {Error}", error);
            }

            return false;
        }

        Current = set;
        logger.Information("Ambience: {Count} zones reloaded", set.Zones.Length);
        return true;
    }

    /// <summary>Swaps in a set directly (tests, tools).</summary>
    public static void SetCurrent(AmbienceZoneSet set) => Current = set ?? AmbienceZoneSet.Empty;

    /// <summary>A missing file is valid and gives the empty set.</summary>
    public static bool TryLoadFile(string path, out AmbienceZoneSet set, List<string> errors)
    {
        if (!File.Exists(path))
        {
            set = AmbienceZoneSet.Empty;
            return true;
        }

        string text;

        try
        {
            text = File.ReadAllText(path, TextEncoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"cannot read {Path.GetFileName(path)}: {e.Message}");
            set = null;
            return false;
        }

        return TryParse(text, out set, errors);
    }

    public static bool TryParse(string json, out AmbienceZoneSet set, List<string> errors)
    {
        set = null;
        ZoneFileDto dto;

        try
        {
            dto = JsonSerializer.Deserialize<ZoneFileDto>(json, JsonConfig.DefaultOptions);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or FormatException or InvalidOperationException)
        {
            errors.Add(e.Message);
            return false;
        }

        if (dto == null)
        {
            errors.Add("the file is empty");
            return false;
        }

        var start = errors.Count;

        if (dto.Version != SchemaVersion)
        {
            errors.Add($"version must be {SchemaVersion}");
        }

        if (dto.PresetTable is not (>= 1 and <= ushort.MaxValue))
        {
            errors.Add("presetTable must be 1-65535");
        }

        var presets = new Dictionary<string, ushort>(StringComparer.Ordinal);

        if (dto.Presets == null)
        {
            errors.Add("presets is missing");
        }
        else
        {
            var ids = new HashSet<int>();

            foreach (var (name, id) in dto.Presets)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add("presets: a name is empty");
                }
                else if (id is < 1 or > ushort.MaxValue)
                {
                    errors.Add($"presets: '{name}' has ID {id}, must be 1-65535 (0 is reserved for no preset)");
                }
                else if (!ids.Add(id))
                {
                    errors.Add($"presets: ID {id} of '{name}' is used twice");
                }
                else
                {
                    presets[name] = (ushort)id;
                }
            }
        }

        var zones = new List<AmbienceZone>();

        if (dto.Zones == null)
        {
            errors.Add("zones is missing");
        }
        else
        {
            var zoneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < dto.Zones.Count; i++)
            {
                var zone = ParseZone(dto.Zones[i], i, presets, zoneIds, errors);

                if (zone != null)
                {
                    zones.Add(zone);
                }
            }
        }

        if (errors.Count > start)
        {
            return false;
        }

        set = new AmbienceZoneSet(dto.PresetTable!.Value, presets, zones.ToArray());
        return true;
    }

    private static AmbienceZone ParseZone(
        ZoneDto dto, int index, Dictionary<string, ushort> presets, HashSet<string> zoneIds, List<string> errors
    )
    {
        if (dto == null)
        {
            errors.Add($"zones[{index}] is null");
            return null;
        }

        var where = string.IsNullOrWhiteSpace(dto.Id) ? $"zones[{index}]" : $"zones[{index}] '{dto.Id}'";
        var start = errors.Count;

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            errors.Add($"{where}: id is empty");
        }
        else if (!zoneIds.Add(dto.Id))
        {
            errors.Add($"{where}: id is used twice");
        }

        var maps = new List<Map>();

        if (dto.Maps is not { Count: > 0 })
        {
            errors.Add($"{where}: maps needs at least one map");
        }
        else
        {
            foreach (var name in dto.Maps)
            {
                if (name == null || !Map.TryParse(name, null, out var map) || map == null || map == Map.Internal)
                {
                    errors.Add($"{where}: unknown map '{name}'");
                }
                else if (maps.Contains(map))
                {
                    errors.Add($"{where}: map '{name}' is listed twice");
                }
                else
                {
                    maps.Add(map);
                }
            }
        }

        var feather = dto.Feather ?? 0;

        if (feather is < 0 or > MaxFeather)
        {
            errors.Add($"{where}: feather must be 0-{MaxFeather}");
        }

        var rects = new List<AmbienceRect>();

        if (dto.Rects is not { Count: > 0 })
        {
            errors.Add($"{where}: rects needs at least one rectangle");
        }
        else
        {
            for (var i = 0; i < dto.Rects.Count; i++)
            {
                var r = dto.Rects[i];
                var rect = new AmbienceRect(r.Start.X, r.Start.Y, r.End.X, r.End.Y);

                if (rect.X2 <= rect.X1 || rect.Y2 <= rect.Y1)
                {
                    errors.Add($"{where}: rects[{i}] is empty, x2 and y2 are exclusive and must exceed x1 and y1");
                    continue;
                }

                if (rect.X1 < 0 || rect.Y1 < 0)
                {
                    errors.Add($"{where}: rects[{i}] has a negative coordinate");
                    continue;
                }

                foreach (var map in maps)
                {
                    if (rect.X2 > map.Width || rect.Y2 > map.Height)
                    {
                        errors.Add($"{where}: rects[{i}] reaches outside {map} ({map.Width} x {map.Height})");
                    }
                }

                rects.Add(rect);
            }
        }

        var rules = new List<AmbienceRule>();

        if (dto.Rules is not { Count: > 0 })
        {
            errors.Add($"{where}: rules needs at least one rule");
        }
        else
        {
            for (var i = 0; i < dto.Rules.Count; i++)
            {
                var rule = ParseRule(dto.Rules[i], $"{where}: rules[{i}]", presets, errors);

                if (rule != null)
                {
                    rules.Add(rule);
                }
            }
        }

        if (errors.Count > start)
        {
            return null;
        }

        return new AmbienceZone
        {
            Id = dto.Id,
            Maps = maps.ToArray(),
            Priority = dto.Priority ?? 0,
            Feather = feather,
            Order = index,
            Rects = rects.ToArray(),
            Rules = rules.ToArray()
        };
    }

    private static AmbienceRule ParseRule(
        RuleDto dto, string where, Dictionary<string, ushort> presets, List<string> errors
    )
    {
        if (dto == null)
        {
            errors.Add($"{where} is null");
            return null;
        }

        var start = errors.Count;
        var timeMask = AmbienceRule.AllTimes;
        var weatherMask = AmbienceRule.AllWeathers;

        if (dto.Time != null)
        {
            timeMask = 0;

            if (dto.Time.Count == 0)
            {
                errors.Add($"{where}: time is empty; leave it out to match any time");
            }

            foreach (var name in dto.Time)
            {
                if (TryParseBand(name, out var band))
                {
                    timeMask |= 1 << (int)band;
                }
                else
                {
                    errors.Add($"{where}: unknown time '{name}' (night, dawn, day, dusk)");
                }
            }
        }

        if (dto.Weather != null)
        {
            weatherMask = 0;

            if (dto.Weather.Count == 0)
            {
                errors.Add($"{where}: weather is empty; leave it out to match any weather");
            }

            foreach (var name in dto.Weather)
            {
                if (TryParseWeather(name, out var weather))
                {
                    weatherMask |= 1 << (int)weather;
                }
                else
                {
                    errors.Add($"{where}: unknown weather '{name}' (clear, rain, snow)");
                }
            }
        }

        ushort presetId = 0;

        if (string.IsNullOrWhiteSpace(dto.Preset))
        {
            errors.Add($"{where}: preset is missing");
        }
        else if (!presets.TryGetValue(dto.Preset, out presetId))
        {
            errors.Add($"{where}: preset '{dto.Preset}' is not in presets");
        }

        var strength = dto.Strength ?? 1.0;

        if (!(strength is >= 0.0 and <= 1.0))
        {
            errors.Add($"{where}: strength must be 0-1");
        }

        var fade = dto.Fade ?? DefaultFadeTenths / 10.0;

        if (!(fade is >= 0.0 and <= MaxFadeSeconds))
        {
            errors.Add($"{where}: fade must be 0-{MaxFadeSeconds:0} seconds");
        }

        if (errors.Count > start)
        {
            return null;
        }

        return new AmbienceRule
        {
            TimeMask = timeMask,
            WeatherMask = weatherMask,
            PresetName = dto.Preset,
            PresetId = presetId,
            Strength = strength,
            FadeTenths = (ushort)Math.Round(fade * 10.0)
        };
    }

    public static bool TryParseBand(string name, out AmbienceTimeBand band)
    {
        (var known, band) = name switch
        {
            "night" => (true, AmbienceTimeBand.Night),
            "dawn"  => (true, AmbienceTimeBand.Dawn),
            "day"   => (true, AmbienceTimeBand.Day),
            "dusk"  => (true, AmbienceTimeBand.Dusk),
            _       => (false, default)
        };

        return known;
    }

    public static bool TryParseWeather(string name, out AmbienceWeatherClass weather)
    {
        (var known, weather) = name switch
        {
            "clear" => (true, AmbienceWeatherClass.Clear),
            "rain"  => (true, AmbienceWeatherClass.Rain),
            "snow"  => (true, AmbienceWeatherClass.Snow),
            _       => (false, default)
        };

        return known;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ZoneFileDto
    {
        [JsonPropertyName("version")]
        public int? Version { get; set; }

        [JsonPropertyName("presetTable")]
        public int? PresetTable { get; set; }

        [JsonPropertyName("presets")]
        public Dictionary<string, int> Presets { get; set; }

        [JsonPropertyName("zones")]
        public List<ZoneDto> Zones { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ZoneDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("maps")]
        public List<string> Maps { get; set; }

        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        [JsonPropertyName("feather")]
        public int? Feather { get; set; }

        // Same converter as the Area of regions.json, so {"x1", "y1", "x2", "y2"} reads the same way.
        [JsonPropertyName("rects")]
        public List<Rectangle3D> Rects { get; set; }

        [JsonPropertyName("rules")]
        public List<RuleDto> Rules { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class RuleDto
    {
        [JsonPropertyName("time")]
        public List<string> Time { get; set; }

        [JsonPropertyName("weather")]
        public List<string> Weather { get; set; }

        [JsonPropertyName("preset")]
        public string Preset { get; set; }

        [JsonPropertyName("strength")]
        public double? Strength { get; set; }

        /// <summary>Seconds.</summary>
        [JsonPropertyName("fade")]
        public double? Fade { get; set; }
    }
}
