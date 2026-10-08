using System;
using System.Globalization;
using Server.Network;

namespace Server.Misc;

/// <summary>
/// Staff override of the time of day that drives the outdoor light cycle. <see cref="LightCycle.ComputeLevelFor"/>
/// reads it in place of the game clock, so the level follows the same night, dawn and dusk curve. A level set
/// with [GlobalLight still wins; regions such as dungeons still apply their own light on top. Clocks, the
/// moon phase and NPC talk keep the real game time. Not saved.
/// </summary>
public static class AmbientTime
{
    private const int None = -1;

    private static int _minuteOfDay = None;

    public static bool IsActive => _minuteOfDay != None;

    public static bool TryGetOverride(out int hours, out int minutes)
    {
        if (_minuteOfDay == None)
        {
            hours = 0;
            minutes = 0;
            return false;
        }

        hours = _minuteOfDay / 60;
        minutes = _minuteOfDay % 60;
        return true;
    }

    public static void Configure()
    {
        CommandSystem.Register("TimeOfDay", AccessLevel.GameMaster, TimeOfDay_OnCommand);
    }

    /// <summary>Parses "hh:mm", "hh" (24-hour clock) or "auto" (<paramref name="auto"/> set).</summary>
    public static bool TryParse(string value, out int hours, out int minutes, out bool auto)
    {
        hours = 0;
        minutes = 0;
        auto = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();

        if (span.InsensitiveEquals("auto"))
        {
            auto = true;
            return true;
        }

        var colon = span.IndexOf(':');
        var hourPart = colon < 0 ? span : span[..colon];
        var minutePart = colon < 0 ? "0".AsSpan() : span[(colon + 1)..];

        if (hourPart.Length is 0 or > 2 || minutePart.Length is 0 or > 2 ||
            !int.TryParse(hourPart, NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
            !int.TryParse(minutePart, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
            hours > 23 || minutes > 59)
        {
            hours = 0;
            minutes = 0;
            return false;
        }

        return true;
    }

    public static void Set(int hours, int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hours);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hours, 23);
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 59);

        _minuteOfDay = hours * 60 + minutes;
        PushLight();
    }

    public static void Clear()
    {
        _minuteOfDay = None;
        PushLight();
    }

    // Same push as LightCycle.LevelOverride; players whose level did not change get nothing.
    private static void PushLight()
    {
        foreach (var ns in NetState.Instances)
        {
            ns.Mobile?.CheckLightLevels(false);
        }
    }

    [Usage("TimeOfDay [hh:mm|auto]")]
    [Description(
        "Overrides the time of day used by the outdoor light cycle on all facets and updates players at once. " +
        "'auto' returns to the game clock. A [GlobalLight level takes precedence. Not saved."
    )]
    internal static void TimeOfDay_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;

        if (e.Length == 0)
        {
            if (TryGetOverride(out var h, out var m))
            {
                from.SendMessage($"Time of day is set to {h:D2}:{m:D2}; light level here: {LightHere(from)}.");
            }
            else
            {
                from.SendMessage($"Time of day follows the game clock; light level here: {LightHere(from)}.");
            }

            from.SendMessage("Usage: TimeOfDay <hh:mm|auto>");
            WarnGlobalLight(from);
            return;
        }

        if (!TryParse(e.GetString(0), out var hours, out var minutes, out var auto))
        {
            from.SendMessage("Usage: TimeOfDay <hh:mm|auto>, 24-hour clock, e.g. TimeOfDay 23:30");
            return;
        }

        if (auto)
        {
            Clear();
            from.SendMessage($"Time of day follows the game clock again; light level here: {LightHere(from)}.");
        }
        else
        {
            Set(hours, minutes);
            from.SendMessage($"Time of day set to {hours:D2}:{minutes:D2}; light level here: {LightHere(from)}.");
        }

        WarnGlobalLight(from);
    }

    // The level the caller sees, after regions such as dungeons altered the outdoor level.
    private static int LightHere(Mobile from)
    {
        from.ComputeLightLevels(out var global, out _);
        return global;
    }

    private static void WarnGlobalLight(Mobile from)
    {
        if (LightCycle.LevelOverride > int.MinValue)
        {
            from.SendMessage(
                $"[GlobalLight {LightCycle.LevelOverride} is active and takes precedence; clear it with [GlobalLight."
            );
        }
    }
}
