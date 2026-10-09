using System.Collections.Generic;
using System.Globalization;

namespace Server.Misc;

/// <summary>Staff commands of the ambience zones: info, reload of the zone file, and a forced preset for tests.</summary>
public static class AmbienceCommands
{
    private const int MaxErrorLines = 10;

    public static void Configure()
    {
        CommandSystem.Register("AmbienceInfo", AccessLevel.GameMaster, AmbienceInfo_OnCommand);
        CommandSystem.Register("AmbienceReload", AccessLevel.GameMaster, AmbienceReload_OnCommand);
        CommandSystem.Register("AmbienceForce", AccessLevel.GameMaster, AmbienceForce_OnCommand);
    }

    [Usage("AmbienceInfo")]
    [Description("Shows the ambience zone, rule, preset and strength at your location and what your client was sent.")]
    internal static void AmbienceInfo_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var set = AmbienceZones.Current;
        var map = from.Map;
        var location = from.Location;

        if (map == null || map == Map.Internal)
        {
            from.SendMessage("You are not on a map.");
            return;
        }

        var band = AmbienceSystem.GetBand(map, location.X, location.Y);
        var weather = AmbienceSystem.GetWeather(map, location);

        from.SendMessage(
            $"Ambience: {set.Zones.Length} zones loaded, preset table {set.PresetTable}; time band {band}, weather {weather}."
        );

        var zone = set.Grid.FindZoneAt(map, location.X, location.Y);
        var result = AmbienceSystem.Resolve(set, map, location.X, location.Y, band, weather);

        if (result.Forced)
        {
            from.SendMessage(
                $"Forced: {set.GetPresetName(result.PresetId)} ({result.PresetId}) at strength {result.Strength}."
            );
        }
        else if (result.Zone != null)
        {
            from.SendMessage(
                $"Zone {result.Zone.Id}, rule {result.RuleIndex + 1}: {set.GetPresetName(result.PresetId)} ({result.PresetId}), strength {result.Strength} of {result.FullStrength}, {result.EdgeDistance} tiles from the edge, feather {result.Zone.Feather}."
            );
        }
        else if (zone != null)
        {
            from.SendMessage($"Zone {zone.Id}: no rule matches {band} and {weather}, nothing is shown.");
        }
        else
        {
            from.SendMessage("No ambience zone here.");
        }

        var ns = from.NetState;

        if (ns != null && AmbienceClients.TryGetState(ns, out var state))
        {
            from.SendMessage(
                $"Your client announced ambience (capabilities 0x{state.Capabilities:X}, preset table {state.PresetTable}); last sent: {set.GetPresetName(state.SentPreset)} ({state.SentPreset}) at strength {state.SentStrength}, fade {state.SentFade / 10.0:0.0} s, {state.PacketsSent} packets."
            );
        }
        else
        {
            from.SendMessage("Your client did not announce ambience support; it is sent nothing.");
        }
    }

    [Usage("AmbienceReload")]
    [Description("Reads the ambience zone file again. On any error the zones in use stay and the errors are listed.")]
    internal static void AmbienceReload_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        var errors = new List<string>();

        if (AmbienceZones.Reload(errors))
        {
            var set = AmbienceZones.Current;
            from.SendMessage($"Ambience zones reloaded: {set.Zones.Length} zones, preset table {set.PresetTable}.");
            return;
        }

        from.SendMessage($"Ambience zone file rejected with {errors.Count} errors; the zones in use stay.");

        for (var i = 0; i < errors.Count && i < MaxErrorLines; i++)
        {
            from.SendMessage(errors[i]);
        }

        if (errors.Count > MaxErrorLines)
        {
            from.SendMessage($"... and {errors.Count - MaxErrorLines} more, see the server log.");
        }
    }

    [Usage("AmbienceForce <preset name|id> [strength 0-1] | AmbienceForce auto")]
    [Description(
        "Shows one preset to every client that announced ambience support, wherever they are, for tests and " +
        "pictures. 'auto' returns to the zones. Not saved."
    )]
    internal static void AmbienceForce_OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        const string usage = "Usage: AmbienceForce <preset name|id> [strength 0-1] | AmbienceForce auto";

        if (e.Length == 0)
        {
            if (AmbienceSystem.Forced is var (preset, strength))
            {
                from.SendMessage(
                    $"Ambience is forced to {AmbienceZones.Current.GetPresetName(preset)} ({preset}) at strength {strength}."
                );
            }
            else
            {
                from.SendMessage("Ambience follows the zones.");
            }

            from.SendMessage(usage);
            return;
        }

        var first = e.GetString(0);

        if (first.InsensitiveEquals("auto"))
        {
            AmbienceSystem.ClearForce();
            from.SendMessage("Ambience follows the zones again.");
            return;
        }

        if (!TryParsePreset(first, out var presetId))
        {
            from.SendMessage($"Unknown preset '{first}'.");
            from.SendMessage(usage);
            return;
        }

        var value = 1.0;

        if (e.Length > 1 &&
            (!double.TryParse(e.GetString(1), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
             !(value is >= 0.0 and <= 1.0)))
        {
            from.SendMessage("Strength must be 0 to 1, e.g. 0.5.");
            return;
        }

        var strengthByte = (byte)System.Math.Round(value * 255.0);
        AmbienceSystem.Force(presetId, strengthByte);
        from.SendMessage(
            $"Ambience forced to {AmbienceZones.Current.GetPresetName(presetId)} ({presetId}) at strength {strengthByte}."
        );
    }

    private static bool TryParsePreset(string value, out ushort presetId)
    {
        if (AmbienceZones.Current.Presets.TryGetValue(value, out presetId))
        {
            return true;
        }

        return ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out presetId);
    }
}
