using System;
using System.Collections.Generic;
using Server.Collections;
using Server.Network;

namespace Server.Misc;

/// <summary>
/// Staff override of the weather on a facet (packet 0x65). While a facet is forced, <see cref="Weather"/> sends
/// nothing there and every player on the facet gets the forced weather, including players who arrive or log in
/// later. Not saved.
/// </summary>
public static class AmbientWeather
{
    public const byte Rain = 0;
    public const byte FierceStorm = 1;
    public const byte Snow = 2;
    public const byte StormBrewing = 3;
    public const byte None = 0xFF;

    // Clients draw at most 70 particles.
    public const int MaxIntensity = 70;

    private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(5.0);

    // Clients drop a weather effect six minutes after it started and ignore a repeat of the running type,
    // so the forced weather is reset and sent again before that.
    private const long ResendAfterMs = 5 * 60 * 1000;

    private static readonly Dictionary<Map, ForcedWeather> _forced = [];
    private static readonly Dictionary<NetState, SentWeather> _sent = [];
    private static TimerExecutionToken _timerToken;
    private static int _generation;

    public readonly record struct ForcedWeather(byte Type, byte Intensity);

    private sealed class SentWeather
    {
        public Map Map;
        public ForcedWeather Weather;
        public long Tick;
        public int Generation;
    }

    public static void Configure()
    {
        CommandSystem.Register("Weather", AccessLevel.GameMaster, Weather_OnCommand);
    }

    public static bool IsForced(Map map) => map != null && _forced.Count > 0 && _forced.ContainsKey(map);

    public static bool TryGetForced(Map map, out ForcedWeather weather)
    {
        if (map != null && _forced.TryGetValue(map, out weather))
        {
            return true;
        }

        weather = default;
        return false;
    }

    public static string GetName(byte type) =>
        type switch
        {
            Rain         => "rain",
            FierceStorm  => "storm",
            Snow         => "snow",
            StormBrewing => "brewing",
            None         => "none",
            _            => type.ToString()
        };

    /// <summary>Parses rain, storm, snow, brewing, none or 0-3.</summary>
    public static bool TryParseType(string value, out byte type)
    {
        type = None;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();

        if (value.InsensitiveEquals("rain"))
        {
            type = Rain;
        }
        else if (value.InsensitiveEquals("storm"))
        {
            type = FierceStorm;
        }
        else if (value.InsensitiveEquals("snow"))
        {
            type = Snow;
        }
        else if (value.InsensitiveEquals("brewing"))
        {
            type = StormBrewing;
        }
        else if (value.InsensitiveEquals("none") || value.InsensitiveEquals("clear"))
        {
            type = None;
        }
        else if (byte.TryParse(value, out var number) && number <= StormBrewing)
        {
            type = number;
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>Forces <paramref name="type"/> on <paramref name="map"/>; <see cref="None"/> forces clear skies.</summary>
    public static void Force(Map map, byte type, int intensity)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (type > StormBrewing && type != None)
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(intensity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intensity, MaxIntensity);

        _forced[map] = new ForcedWeather(type, type == None ? (byte)0 : (byte)intensity);

        if (!_timerToken.Running)
        {
            Timer.StartTimer(_refreshInterval, _refreshInterval, Refresh, out _timerToken);
        }

        Refresh();
    }

    /// <summary>
    /// Hands <paramref name="map"/> back to <see cref="Weather"/>: its players are cleared now and get the regular
    /// weather from its next tick on.
    /// </summary>
    public static bool Release(Map map)
    {
        if (map == null || !_forced.Remove(map))
        {
            return false;
        }

        Refresh();
        return true;
    }

    internal static void Refresh()
    {
        var generation = ++_generation;
        var now = Core.TickCount;

        foreach (var ns in NetState.Instances)
        {
            var map = ns.Mobile?.Map;
            _sent.TryGetValue(ns, out var sent);

            if (map != null && _forced.TryGetValue(map, out var weather))
            {
                if (sent == null)
                {
                    _sent[ns] = sent = new SentWeather();
                    Send(ns, weather);
                    sent.Tick = now;
                }
                else if (sent.Map != map || sent.Weather != weather || now - sent.Tick >= ResendAfterMs)
                {
                    Send(ns, weather);
                    sent.Tick = now;
                }
                else if (weather.Type != None)
                {
                    // Teleports, rejected steps and death reset the client's weather. A bare repeat brings it
                    // back there and is ignored by a client that still shows it.
                    ns.SendWeather(weather.Type, weather.Intensity, 0);
                }

                sent.Map = map;
                sent.Weather = weather;
                sent.Generation = generation;
            }
            else if (sent != null)
            {
                _sent.Remove(ns);
                ns.SendWeather(None, 0, 0);
            }
        }

        if (_sent.Count > 0)
        {
            using var gone = PooledRefList<NetState>.Create();

            foreach (var (ns, sent) in _sent)
            {
                if (sent.Generation != generation)
                {
                    gone.Add(ns);
                }
            }

            for (var i = 0; i < gone.Count; i++)
            {
                _sent.Remove(gone[i]);
            }
        }

        if (_forced.Count == 0 && _sent.Count == 0)
        {
            _timerToken.Cancel();
        }
    }

    private static void Send(NetState ns, ForcedWeather weather)
    {
        // "None" first resets the client, so a type it already shows starts again with the new intensity.
        ns.SendWeather(None, 0, 0);

        if (weather.Type != None)
        {
            ns.SendWeather(weather.Type, weather.Intensity, 0);
        }
    }

    [Usage("Weather [rain|storm|snow|brewing|none|0-3 [intensity 0-70]|auto] [map]")]
    [Description(
        "Forces the weather on a facet (default: your facet) for all its players, intensity defaults to 70. " +
        "'none' forces clear skies, 'auto' hands the facet back to the regular weather. Not saved."
    )]
    internal static void Weather_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        const string usage = "Usage: Weather <rain|storm|snow|brewing|none|0-3> [intensity 0-70] [map] | Weather auto [map]";

        if (e.Length == 0)
        {
            if (AmbientSeason.TryGetTargetMap(e, 0, out var current))
            {
                if (TryGetForced(current, out var forced))
                {
                    from.SendMessage($"Weather on {current} is forced: {GetName(forced.Type)} {forced.Intensity}.");
                }
                else
                {
                    from.SendMessage($"Weather on {current} follows the regular weather.");
                }
            }

            from.SendMessage(usage);
            return;
        }

        var first = e.GetString(0);

        if (first.InsensitiveEquals("auto"))
        {
            if (!AmbientSeason.TryGetTargetMap(e, 1, out var released))
            {
                return;
            }

            if (Release(released))
            {
                from.SendMessage($"Weather on {released} follows the regular weather again.");
            }
            else
            {
                from.SendMessage($"Weather on {released} was not forced.");
            }

            return;
        }

        if (!TryParseType(first, out var type))
        {
            from.SendMessage(usage);
            return;
        }

        var intensity = MaxIntensity;
        var mapIndex = 1;

        if (e.Length > 1 && int.TryParse(e.GetString(1), out var parsed))
        {
            if (parsed is < 0 or > MaxIntensity)
            {
                from.SendMessage($"Intensity must be 0 to {MaxIntensity}.");
                return;
            }

            intensity = parsed;
            mapIndex = 2;
        }

        if (!AmbientSeason.TryGetTargetMap(e, mapIndex, out var map))
        {
            return;
        }

        Force(map, type, intensity);

        if (type == None)
        {
            from.SendMessage($"Weather on {map} forced to clear skies.");
        }
        else
        {
            from.SendMessage($"Weather on {map} forced to {GetName(type)} {intensity}.");
        }
    }
}
