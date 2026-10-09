using System;
using Server.Collections;
using Server.Items;
using Server.Network;

namespace Server.Misc;

/// <summary>What a player should see now: preset, strength and the fade of its rule.</summary>
public readonly record struct AmbienceResult(
    AmbienceZone Zone,
    int RuleIndex,
    bool Forced,
    ushort PresetId,
    byte Strength,
    byte FullStrength,
    ushort FadeTenths,
    int EdgeDistance
)
{
    public static readonly AmbienceResult None = new(null, -1, false, 0, 0, 0, AmbienceZones.DefaultFadeTenths, -1);

    /// <summary>The preset ID on the wire: strength 0 is sent as "no preset".</summary>
    public ushort WirePreset => Strength == 0 ? (ushort)0 : PresetId;

    public byte WireStrength => WirePreset == 0 ? (byte)0 : Strength;
}

/// <summary>
/// Resolves the ambience of every announced session once per second and sends 0xBF/0xE1 when it changed.
/// Sessions that did not announce are never looked at.
/// </summary>
public static class AmbienceSystem
{
    public const int TickMs = 1000;

    // A tick may run a little early; feather steps keep about one second apart.
    public const int MinStepIntervalMs = 900;

    // Inside the feather band a strength change is sent only from this size on (about 6 %), or at its ends.
    public const int FeatherStep = 16;

    // A move further than this within one tick is a teleport, gate or death: the picture changes anyway.
    public const int TeleportDistance = 18;

    public const ushort TeleportFadeTenths = 5;
    public const ushort StepFadeTenths = 10;

    private static TimerExecutionToken _timerToken;

    /// <summary>Set by [AmbienceForce: every announced session shows this preset instead of its zone.</summary>
    public static (ushort PresetId, byte Strength)? Forced { get; private set; }

    public static void Force(ushort presetId, byte strength)
    {
        Forced = (presetId, strength);
        Tick();
    }

    public static void ClearForce()
    {
        Forced = null;
        Tick();
    }

    public static AmbienceTimeBand GetBand(int hours) =>
        hours switch
        {
            < 4  => AmbienceTimeBand.Night,
            < 6  => AmbienceTimeBand.Dawn,
            < 22 => AmbienceTimeBand.Day,
            _    => AmbienceTimeBand.Dusk
        };

    /// <summary>The band of the time <see cref="LightCycle"/> uses here; a [GlobalLight level does not change it.</summary>
    public static AmbienceTimeBand GetBand(Map map, int x, int y)
    {
        if (!AmbientTime.TryGetOverride(out var hours, out _))
        {
            Clock.GetTime(map, x, y, out hours, out int _);
        }

        return GetBand(hours);
    }

    /// <summary>Rain and fierce storm are rain, snow is snow, storm brewing and none are clear.</summary>
    public static AmbienceWeatherClass ClassifyForced(byte type) =>
        type switch
        {
            AmbientWeather.Rain or AmbientWeather.FierceStorm => AmbienceWeatherClass.Rain,
            AmbientWeather.Snow                                => AmbienceWeatherClass.Snow,
            _                                                  => AmbienceWeatherClass.Clear
        };

    public static AmbienceWeatherClass GetWeather(Map map, Point3D p)
    {
        if (AmbientWeather.TryGetForced(map, out var forced))
        {
            return ClassifyForced(forced.Type);
        }

        if (!Weather.TryGetPrecipitation(map, p, out var snow))
        {
            return AmbienceWeatherClass.Clear;
        }

        return snow ? AmbienceWeatherClass.Snow : AmbienceWeatherClass.Rain;
    }

    public static AmbienceResult Resolve(
        AmbienceZoneSet set, Map map, int x, int y, AmbienceTimeBand band, AmbienceWeatherClass weather
    )
    {
        if (Forced is var (forcedPreset, forcedStrength))
        {
            return new AmbienceResult(null, -1, true, forcedPreset, forcedStrength, forcedStrength, StepFadeTenths, -1);
        }

        if (!set.Grid.TryFind(map, x, y, band, weather, out var match))
        {
            return AmbienceResult.None;
        }

        var rule = match.Rule;
        var feather = match.Zone.Feather;
        var edge = feather > 0 ? Math.Min(1.0, match.EdgeDistance / (double)feather) : 1.0;

        return new AmbienceResult(
            match.Zone,
            match.RuleIndex,
            false,
            rule.PresetId,
            ToByte(rule.Strength * edge),
            ToByte(rule.Strength),
            rule.FadeTenths,
            match.EdgeDistance
        );
    }

    public static AmbienceResult Resolve(Mobile m)
    {
        var map = m.Map;

        if (map == null || map == Map.Internal)
        {
            return AmbienceResult.None;
        }

        var location = m.Location;

        // Band and weather cost a clock and a weather lookup; most players stand in no zone.
        if (Forced == null && AmbienceZones.Current.Grid.GetCandidates(map, location.X, location.Y).IsEmpty)
        {
            return AmbienceResult.None;
        }

        return Resolve(
            AmbienceZones.Current,
            map,
            location.X,
            location.Y,
            GetBand(map, location.X, location.Y),
            GetWeather(map, location)
        );
    }

    private static byte ToByte(double strength) => (byte)Math.Clamp(Math.Round(strength * 255.0), 0, 255);

    /// <summary>
    /// Decides whether <paramref name="result"/> goes to the client now and with which fade, and records it as sent.
    /// A new zone, rule or preset goes out at once with the rule's fade; a strength step within the same rule (the
    /// feather ramp) only when it moved by <see cref="FeatherStep"/> or reached either end, at most once a second.
    /// </summary>
    public static bool Evaluate(
        AmbienceClientState state, in AmbienceResult result, Map map, Point3D location, long now, out ushort fade
    )
    {
        var teleported = state.LastMap != null && (state.LastMap != map ||
                                                   Math.Abs(location.X - state.LastLocation.X) > TeleportDistance ||
                                                   Math.Abs(location.Y - state.LastLocation.Y) > TeleportDistance);
        state.LastMap = map;
        state.LastLocation = location;

        var preset = result.WirePreset;
        var strength = result.WireStrength;
        // A forced preset or a changed preset is always a new picture; only the ramp of one rule is a step.
        var sameSource = !result.Forced && !state.SentForced && result.Zone == state.SentZone &&
                         result.RuleIndex == state.SentRule &&
                         (preset == state.SentPreset || preset == 0 || state.SentPreset == 0);

        if (preset == state.SentPreset && strength == state.SentStrength)
        {
            state.SentZone = result.Zone;
            state.SentRule = result.RuleIndex;
            state.SentForced = result.Forced;

            if (preset != 0)
            {
                state.FadeOut = result.FadeTenths;
            }

            fade = 0;
            return false;
        }

        if (sameSource && !teleported)
        {
            var reachedEnd = strength == 0 || strength == result.FullStrength;

            if (!reachedEnd && Math.Abs(strength - state.SentStrength) < FeatherStep ||
                now - state.SentTick < MinStepIntervalMs)
            {
                fade = 0;
                return false;
            }

            fade = StepFadeTenths;
        }
        else if (teleported)
        {
            fade = TeleportFadeTenths;
        }
        else
        {
            fade = preset != 0 ? result.FadeTenths : state.FadeOut;
        }

        Record(state, result, now, fade);
        return true;
    }

    private static void Record(AmbienceClientState state, in AmbienceResult result, long now, ushort fade)
    {
        state.SentPreset = result.WirePreset;
        state.SentStrength = result.WireStrength;
        state.SentFade = fade;
        state.SentTick = now;
        state.SentZone = result.Zone;
        state.SentRule = result.RuleIndex;
        state.SentForced = result.Forced;
        state.PacketsSent++;

        if (result.WirePreset != 0)
        {
            state.FadeOut = result.FadeTenths;
        }
    }

    /// <summary>First resolve after the announcement: sent at once (fade 0) unless the player stands in no mood.</summary>
    internal static void OnAnnounced(NetState ns, AmbienceClientState state)
    {
        EnsureTimer();

        var m = ns.Mobile;

        if (m?.Deleted != false || m.Map == null)
        {
            return;
        }

        var result = Resolve(m);
        var now = Core.TickCount;

        state.LastMap = m.Map;
        state.LastLocation = m.Location;

        if (result.WirePreset == 0)
        {
            state.SentZone = result.Zone;
            state.SentRule = result.RuleIndex;
            state.SentForced = result.Forced;
            return;
        }

        Record(state, result, now, 0);
        AmbienceClients.SendPreset(ns, state.SentPreset, state.SentStrength, 0);
    }

    private static void EnsureTimer()
    {
        if (!_timerToken.Running)
        {
            var interval = TimeSpan.FromMilliseconds(TickMs);
            Timer.StartTimer(interval, interval, Tick, out _timerToken);
        }
    }

    internal static void Tick()
    {
        var clients = AmbienceClients.Clients;

        if (clients.Count == 0)
        {
            _timerToken.Cancel();
            return;
        }

        var now = Core.TickCount;
        using var gone = PooledRefList<NetState>.Create();

        foreach (var (ns, state) in clients)
        {
            if (!NetState.Instances.Contains(ns))
            {
                gone.Add(ns);
                continue;
            }

            var m = ns.Mobile;

            if (m?.Deleted != false || m.Map == null || m.Map == Map.Internal)
            {
                continue;
            }

            var result = Resolve(m);

            if (Evaluate(state, result, m.Map, m.Location, now, out var fade))
            {
                AmbienceClients.SendPreset(ns, state.SentPreset, state.SentStrength, fade);
            }
        }

        for (var i = 0; i < gone.Count; i++)
        {
            AmbienceClients.Remove(gone[i]);
        }
    }
}
