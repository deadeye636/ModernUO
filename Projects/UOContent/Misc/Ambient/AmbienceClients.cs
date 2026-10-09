using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using Server.Logging;
using Server.Network;

namespace Server.Misc;

/// <summary>Ambience state of one session that announced support. Created by the first valid 0xBF/0xE0.</summary>
public sealed class AmbienceClientState
{
    public AmbienceClientState(uint capabilities, ushort presetTable, long now)
    {
        Capabilities = capabilities;
        PresetTable = presetTable;
        SentTick = now - AmbienceSystem.MinStepIntervalMs;
    }

    public uint Capabilities { get; }
    public ushort PresetTable { get; }

    // What the client was last told; it starts neutral, so nothing sent equals preset 0.
    public ushort SentPreset { get; internal set; }
    public byte SentStrength { get; internal set; }
    public ushort SentFade { get; internal set; }
    public long SentTick { get; internal set; }
    public int PacketsSent { get; internal set; }

    // Where the sent values came from: a change of zone or rule is a new picture, a change within one is a step.
    internal AmbienceZone SentZone;
    internal int SentRule = -1;
    internal bool SentForced;

    // Fade used when the client is sent back to neutral: that of the last rule shown.
    internal ushort FadeOut = AmbienceZones.DefaultFadeTenths;

    internal Map LastMap;
    internal Point3D LastLocation;
}

/// <summary>
/// The ambience subcommands of 0xBF: 0xE0 (client announces support, client -> server) and 0xE1 (preset, server
/// -> client). Only sessions that announced get 0xE1.
/// </summary>
public static class AmbienceClients
{
    public const int HelloSubcommand = 0xE0;
    public const int PresetSubcommand = 0xE1;

    public const byte ProtocolVersion = 1;
    public const byte MessageVersion = 1;

    public const uint CapabilityLightMood = 0x1;
    public const uint CapabilityHeightFog = 0x2;
    public const uint CapabilityShadows = 0x4;
    public const uint KnownCapabilities = CapabilityLightMood | CapabilityHeightFog | CapabilityShadows;

    public const int HelloLength = 12;
    public const int PresetLength = 12;

    // Bytes of 0xE0 after the subcommand: version (1), capabilities (4), preset table (2).
    private const int HelloPayloadLength = HelloLength - 5;

    private static readonly ILogger logger = LogFactory.GetLogger(typeof(AmbienceClients));

    private static readonly Dictionary<NetState, AmbienceClientState> _clients = [];

    public static int Count => _clients.Count;

    internal static Dictionary<NetState, AmbienceClientState> Clients => _clients;

    public static unsafe void Configure()
    {
        IncomingExtendedCommandPackets.RegisterExtended(HelloSubcommand, true, &Hello);

        // A client sending the server's own subcommand is ignored without a trace line.
        IncomingExtendedCommandPackets.RegisterExtended(PresetSubcommand, true, &IncomingExtendedCommandPackets.Empty);
    }

    public static bool TryGetState(NetState ns, out AmbienceClientState state) =>
        _clients.TryGetValue(ns, out state);

    internal static void Remove(NetState ns) => _clients.Remove(ns);

    /// <summary>
    /// 0xBF/0xE0. Every failed check drops the packet silently: no log line, no disconnect, nothing allocated.
    /// Lengths are taken from the reader, never from the payload.
    /// </summary>
    public static void Hello(NetState state, SpanReader reader)
    {
        if (reader.Remaining != HelloPayloadLength)
        {
            return;
        }

        if (reader.ReadByte() != ProtocolVersion)
        {
            return;
        }

        if (_clients.ContainsKey(state))
        {
            return;
        }

        var capabilities = reader.ReadUInt32() & KnownCapabilities;

        if (capabilities == 0)
        {
            return;
        }

        var presetTable = reader.ReadUInt16();
        var clientState = new AmbienceClientState(capabilities, presetTable, Core.TickCount);
        _clients[state] = clientState;

        // Once per session: only the first announcement gets here.
        var expected = AmbienceZones.Current.PresetTable;

        if (expected != 0 && presetTable != expected)
        {
            logger.Information(
                "Ambience: {NetState} announced preset table {ClientTable}, the zone file names table {ServerTable}; " +
                "presets are sent anyway",
                state,
                presetTable,
                expected
            );
        }

        AmbienceSystem.OnAnnounced(state, clientState);
    }

    /// <summary>Writes 0xBF/0xE1 version 1 into <paramref name="buffer"/> (at least 12 bytes).</summary>
    public static int WritePreset(Span<byte> buffer, ushort presetId, byte strength, ushort fadeTenths)
    {
        buffer[0] = 0xBF;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[1..], PresetLength);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[3..], PresetSubcommand);
        buffer[5] = MessageVersion;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[6..], presetId);
        buffer[8] = strength;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[9..], fadeTenths);
        buffer[11] = 0; // flags: no override block in version 1
        return PresetLength;
    }

    public static void SendPreset(NetState ns, ushort presetId, byte strength, ushort fadeTenths)
    {
        if (ns.CannotSendPackets())
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[PresetLength];
        WritePreset(buffer, presetId, strength, fadeTenths);
        ns.Send(buffer);
    }
}
