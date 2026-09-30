using System.Net;
using System.Net.Sockets;
using Server.Network;
using Xunit;

namespace Server.Tests.Network;

public class NetStateAcceptTests
{
    private static readonly IPEndPoint[] _threeListeners =
    [
        new(IPAddress.Loopback, 2593),
        new(IPAddress.Loopback, 2594),
        new(IPAddress.Loopback, 2595)
    ];

    [Theory]
    [InlineData(2593, 0)]
    [InlineData(2594, 1)]
    [InlineData(2595, 2)]
    public void FindListenerIndex_ReturnsTheListenerOfTheConnectionsPort(int port, int expected)
    {
        var local = new IPEndPoint(IPAddress.Loopback, port);

        Assert.Equal(expected, NetState.FindListenerIndex(_threeListeners, local));
    }

    [Fact]
    public void FindListenerIndex_TracesAnAcceptedSocketToItsListener()
    {
        var listeners = new TcpListener[3];
        var endPoints = new IPEndPoint[3];

        try
        {
            for (var i = 0; i < listeners.Length; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
                endPoints[i] = (IPEndPoint)listeners[i].LocalEndpoint;
            }

            // More connections on one listener than the server keeps accepts pending for on it
            for (var i = 0; i < 40; i++)
            {
                using var client = new TcpClient();
                client.Connect(endPoints[0]);
                using var accepted = listeners[0].AcceptSocket();

                var local = SocketHelper.GetLocalEndPoint(accepted.Handle);
                Assert.Equal(0, NetState.FindListenerIndex(endPoints, local));
            }

            using (var client = new TcpClient())
            {
                client.Connect(endPoints[2]);
                using var accepted = listeners[2].AcceptSocket();

                var local = SocketHelper.GetLocalEndPoint(accepted.Handle);
                Assert.Equal(2, NetState.FindListenerIndex(endPoints, local));
            }
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener?.Stop();
            }
        }
    }

    [Fact]
    public void FindListenerIndex_MatchesAWildcardListenerByPort()
    {
        IPEndPoint[] listeners =
        [
            new(IPAddress.Any, 2593),
            new(IPAddress.IPv6Any, 2594)
        ];

        Assert.Equal(0, NetState.FindListenerIndex(listeners, new IPEndPoint(IPAddress.Parse("192.0.2.5"), 2593)));
        Assert.Equal(1, NetState.FindListenerIndex(listeners, new IPEndPoint(IPAddress.IPv6Loopback, 2594)));
        Assert.Equal(
            1,
            NetState.FindListenerIndex(listeners, new IPEndPoint(IPAddress.Parse("::ffff:127.0.0.1"), 2594))
        );
    }

    [Fact]
    public void FindListenerIndex_PrefersTheWildcardOfTheSocketsFamily()
    {
        // An IPv6-only [::] and 0.0.0.0 can share a port; either order in the configuration must work
        IPEndPoint[] v6First =
        [
            new(IPAddress.IPv6Any, 2593),
            new(IPAddress.Any, 2593)
        ];
        IPEndPoint[] v4First =
        [
            new(IPAddress.Any, 2593),
            new(IPAddress.IPv6Any, 2593)
        ];

        var v4 = new IPEndPoint(IPAddress.Parse("192.0.2.5"), 2593);
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::5"), 2593);

        Assert.Equal(1, NetState.FindListenerIndex(v6First, v4));
        Assert.Equal(0, NetState.FindListenerIndex(v6First, v6));
        Assert.Equal(0, NetState.FindListenerIndex(v4First, v4));
        Assert.Equal(1, NetState.FindListenerIndex(v4First, v6));
    }

    [Fact]
    public void FindListenerIndex_PrefersTheExactAddressOverAWildcard()
    {
        IPEndPoint[] listeners =
        [
            new(IPAddress.Any, 2593),
            new(IPAddress.Loopback, 2593)
        ];

        Assert.Equal(1, NetState.FindListenerIndex(listeners, new IPEndPoint(IPAddress.Loopback, 2593)));
        Assert.Equal(0, NetState.FindListenerIndex(listeners, new IPEndPoint(IPAddress.Parse("192.0.2.5"), 2593)));
    }

    [Fact]
    public void FindListenerIndex_ReturnsMinusOneWhenNothingMatches()
    {
        Assert.Equal(-1, NetState.FindListenerIndex(_threeListeners, new IPEndPoint(IPAddress.Loopback, 2596)));
        Assert.Equal(-1, NetState.FindListenerIndex(_threeListeners, new IPEndPoint(IPAddress.Parse("192.0.2.5"), 2593)));
        Assert.Equal(-1, NetState.FindListenerIndex(_threeListeners, null));
        Assert.Equal(-1, NetState.FindListenerIndex([], new IPEndPoint(IPAddress.Loopback, 2593)));
    }

    [Fact]
    public void SelectReplacementListener_KeepsAnAcceptedConnectionsListener()
    {
        uint cursor = 0;

        // Forty accepts on the first of three listeners must all be replaced there
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(0, NetState.SelectReplacementListener(0, 3, ref cursor));
        }

        Assert.Equal(2, NetState.SelectReplacementListener(2, 3, ref cursor));
        Assert.Equal(0u, cursor);
    }

    [Fact]
    public void SelectReplacementListener_RotatesWhenTheListenerIsUnknown()
    {
        uint cursor = 0;

        Assert.Equal(0, NetState.SelectReplacementListener(-1, 3, ref cursor));
        Assert.Equal(1, NetState.SelectReplacementListener(-1, 3, ref cursor));
        Assert.Equal(2, NetState.SelectReplacementListener(-1, 3, ref cursor));
        Assert.Equal(0, NetState.SelectReplacementListener(-1, 3, ref cursor));

        // An index from a listener set that has since shrunk counts as unknown
        Assert.Equal(1, NetState.SelectReplacementListener(5, 3, ref cursor));
    }

    [Fact]
    public void SelectReplacementListener_SurvivesCursorWraparound()
    {
        var cursor = uint.MaxValue;

        Assert.Equal((int)(uint.MaxValue % 3), NetState.SelectReplacementListener(-1, 3, ref cursor));
        Assert.Equal(0, NetState.SelectReplacementListener(-1, 3, ref cursor));
    }
}
