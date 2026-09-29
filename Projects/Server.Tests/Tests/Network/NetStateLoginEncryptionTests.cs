using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Server.Network;
using Xunit;

namespace Server.Tests.Network;

[Collection("Sequential Server Tests")]
public class NetStateLoginEncryptionTests : IDisposable
{
    private const string EncryptionModeSetting = "network.encryptionMode";

    // The low byte 0xCB makes the first keystream byte 0x61, so the encrypted 0x80 goes out as 0xE1 (Client Type)
    private const uint InfoPacketSeed = 0x0A0B0CCB;

    private const byte PingPacketId = 0x73;

    private const byte ClientTypePacketId = 0xE1;

    // A plain 0xE1 (Client Type) packet, the info packet id the seed above turns an encrypted login into
    private static readonly byte[] _clientTypePacket = [ClientTypePacketId, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03];

    private static string _lastLoginUsername;
    private static int _clientTypeCount;

    public NetStateLoginEncryptionTests()
    {
        RegisterHandlers();
        _lastLoginUsername = null;
        _clientTypeCount = 0;

        SetEncryptionMode(EncryptionMode.Both);
    }

    public void Dispose() => SetEncryptionMode(EncryptionMode.None);

    private static void SetEncryptionMode(EncryptionMode mode)
    {
        ServerConfiguration.SetSetting(EncryptionModeSetting, mode);
        EncryptionManager.Configure();
    }

    private static unsafe void RegisterHandlers()
    {
        if (IncomingPackets.GetHandler(0x80) == null)
        {
            IncomingPackets.Register(0x80, &RecordLogin, 62, outgameOnly: true);
        }

        if (IncomingPackets.GetHandler(PingPacketId) == null)
        {
            IncomingPackets.Register(PingPacketId, 2, false, &NoOp);
        }

        if (IncomingPackets.GetHandler(ClientTypePacketId) == null)
        {
            IncomingPackets.Register(ClientTypePacketId, &RecordClientType);
        }
    }

    private static void RecordLogin(NetState state, SpanReader reader) => _lastLoginUsername = reader.ReadAscii(30);

    private static void RecordClientType(NetState state, SpanReader reader) => _clientTypeCount++;

    private static NetState CreateAwaitingLogin(out Socket client)
    {
        var ns = PacketTestUtilities.CreateTestNetState(out client);
        ns.Seed = (int)InfoPacketSeed;
        ns.Seeded = true;
        ns.Version = new ClientVersion(67, 0, 117, 0);
        ns._protocolState = NetState.ProtocolState.LoginServer_AwaitingLogin;
        return ns;
    }

    private static void NoOp(NetState state, SpanReader reader)
    {
    }

    private static byte[] CreateLogin(string username, string password)
    {
        var packet = new byte[62];
        packet[0] = 0x80;
        Encoding.ASCII.GetBytes(username, packet.AsSpan(1, 30));
        Encoding.ASCII.GetBytes(password, packet.AsSpan(31, 30));
        packet[61] = 0xFF;
        return packet;
    }

    private static void SliceUntil(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done() && deadline.ElapsedMilliseconds < 5000)
        {
            NetState.Slice();
            Thread.Sleep(5);
        }

        Assert.True(done());
    }

    private static void SliceFor(int milliseconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            NetState.Slice();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void EncryptedLogin_FirstByteMatchesAnInfoPacket_IsDecryptedAndProcessed()
    {
        var version = new ClientVersion(67, 0, 117, 0);
        var login = CreateLogin("account", "password");
        new LoginEncryption(InfoPacketSeed, LoginKeys.GetKeys(version)).ClientDecrypt(login);
        Assert.Equal(0xE1, login[0]);
        Assert.True(IncomingPackets.IsInfoPacket(login[0]));

        var ns = PacketTestUtilities.CreateTestNetState(out var client);
        try
        {
            ns.Seed = (int)InfoPacketSeed;
            ns.Seeded = true;
            ns.Version = version;
            ns._protocolState = NetState.ProtocolState.LoginServer_AwaitingLogin;

            // A partial login must be held, not parsed as the info packet its first byte spells
            client.Send(login.AsSpan(0, 3));
            SliceFor(100);
            Assert.True(ns.Running);
            Assert.Equal(NetState.ProtocolState.LoginServer_AwaitingLogin, ns._protocolState);
            Assert.Null(_lastLoginUsername);

            client.Send(login.AsSpan(3));
            SliceUntil(() => _lastLoginUsername != null);

            Assert.Equal("account", _lastLoginUsername);
            Assert.True(ns.Running);
            Assert.Equal(NetState.ProtocolState.LoginServer_AwaitingServerSelect, ns._protocolState);
        }
        finally
        {
            ns.Dispose();
            client.Close();
        }
    }

    [Fact]
    public void InfoPacketBeforeUnencryptedLogin_IsStillHandled()
    {
        var login = CreateLogin("account", "password");
        var data = new byte[2 + login.Length];
        data[0] = PingPacketId;
        login.CopyTo(data, 2);

        var ns = PacketTestUtilities.CreateTestNetState(out var client);
        try
        {
            ns.Seed = (int)InfoPacketSeed;
            ns.Seeded = true;
            ns.Version = new ClientVersion(67, 0, 117, 0);
            ns._protocolState = NetState.ProtocolState.LoginServer_AwaitingLogin;

            client.Send(data);
            SliceUntil(() => _lastLoginUsername != null);

            Assert.Equal("account", _lastLoginUsername);
            Assert.True(ns.Running);
            Assert.Equal(NetState.ProtocolState.LoginServer_AwaitingServerSelect, ns._protocolState);
        }
        finally
        {
            ns.Dispose();
            client.Close();
        }
    }

    [Fact]
    public void InfoPacketOtherThanTheSeedsFirstByte_IsHandledWithoutWaiting()
    {
        var ns = CreateAwaitingLogin(out var client);
        try
        {
            client.Send([PingPacketId, 0x01]);
            SliceUntil(() => ns._socket.RecvBuffer.ReadableBytes == 0 && ns._receivedData);

            Assert.True(ns.Running);
            Assert.Equal(NetState.ProtocolState.LoginServer_AwaitingLogin, ns._protocolState);
        }
        finally
        {
            ns.Dispose();
            client.Close();
        }
    }

    [Fact]
    public void InfoPacketMatchingTheSeedsFirstByte_ThatDoesNotDecrypt_IsHeldThenHandledAsInfoPacket()
    {
        var login = CreateLogin("account", "password");
        var data = new byte[_clientTypePacket.Length + login.Length];
        _clientTypePacket.CopyTo(data, 0);
        login.CopyTo(data, _clientTypePacket.Length);
        Assert.False(LoginEncryption.TryDecrypt(new ClientVersion(67, 0, 117, 0), InfoPacketSeed, data, out _));

        var ns = CreateAwaitingLogin(out var client);
        try
        {
            client.Send(_clientTypePacket);
            SliceFor(100);
            Assert.True(ns.Running);
            Assert.Equal(_clientTypePacket.Length, ns._socket.RecvBuffer.ReadableBytes);
            Assert.Equal(0, _clientTypeCount);

            client.Send(login);
            SliceUntil(() => _lastLoginUsername != null);

            Assert.Equal(1, _clientTypeCount);
            Assert.Equal("account", _lastLoginUsername);
            Assert.True(ns.Running);
            Assert.Equal(NetState.ProtocolState.LoginServer_AwaitingServerSelect, ns._protocolState);
        }
        finally
        {
            ns.Dispose();
            client.Close();
        }
    }

    [Fact]
    public void UnencryptedOnlyMode_HandlesTheSeedsInfoPacketWithoutWaiting()
    {
        SetEncryptionMode(EncryptionMode.Unencrypted);

        var ns = CreateAwaitingLogin(out var client);
        try
        {
            client.Send(_clientTypePacket);
            SliceUntil(() => _clientTypeCount == 1);

            Assert.True(ns.Running);
            Assert.Equal(0, ns._socket.RecvBuffer.ReadableBytes);
            Assert.Equal(NetState.ProtocolState.LoginServer_AwaitingLogin, ns._protocolState);
        }
        finally
        {
            ns.Dispose();
            client.Close();
        }
    }
}
