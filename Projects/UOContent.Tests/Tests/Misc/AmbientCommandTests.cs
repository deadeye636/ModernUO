using System;
using System.Collections.Generic;
using Server;
using Server.Misc;
using Server.Mobiles;
using Server.Network;
using Server.Tests.Network;
using Xunit;

namespace UOContent.Tests.Misc;

[Collection("Sequential UOContent Tests")]
public class AmbientCommandTests
{
    private readonly List<(NetState State, Mobile Mobile)> _clients = [];
    private readonly List<Mobile> _mobiles = [];

    private (NetState State, PlayerMobile Mobile) CreateClient(Map map, Point3D location)
    {
        var ns = PacketTestUtilities.CreateTestNetState();
        ns.Account = new MockAccount();

        var m = new PlayerMobile(World.NewMobile);
        m.DefaultMobileInit();
        ns.Mobile = m;
        m.NetState = ns;
        m.MoveToWorld(location, map);

        _clients.Add((ns, m));
        return (ns, m);
    }

    private Mobile CreateStaff()
    {
        var m = new Mobile(World.NewMobile);
        m.DefaultMobileInit();
        m.AccessLevel = AccessLevel.GameMaster;
        _mobiles.Add(m);
        return m;
    }

    private void Cleanup()
    {
        foreach (var (state, mobile) in _clients)
        {
            state.Mobile = null;
            state.Dispose();
            mobile.Delete();
        }

        foreach (var m in _mobiles)
        {
            m.Delete();
        }

        _clients.Clear();
        _mobiles.Clear();
    }

    private static int Mark(NetState ns) => ns.SendBuffer.GetReadSpan().Length;

    private static byte[] Since(NetState ns, int mark) => ns.SendBuffer.GetReadSpan()[mark..].ToArray();

    private static void Run(CommandEventHandler handler, Mobile from, params string[] args) =>
        handler(new CommandEventArgs(from, "test", string.Join(' ', args), args));

    private static bool ContainsSequence(byte[] data, params byte[] sequence) =>
        data.AsSpan().IndexOf(sequence) >= 0;

    [Theory]
    [InlineData("spring", 0)]
    [InlineData("Summer", 1)]
    [InlineData("fall", 2)]
    [InlineData("autumn", 2)]
    [InlineData("WINTER", 3)]
    [InlineData("desolation", 4)]
    [InlineData("4", 4)]
    [InlineData("0", 0)]
    [InlineData("auto", AmbientSeason.Auto)]
    public void Season_Parse_Valid(string value, int expected)
    {
        Assert.True(AmbientSeason.TryParse(value, out var season));
        Assert.Equal(expected, season);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("monsoon")]
    public void Season_Parse_Invalid(string value) => Assert.False(AmbientSeason.TryParse(value, out _));

    [Fact]
    public void Season_CommandSetsMapAndSendsToItsPlayers_AutoRestores()
    {
        var map = Map.Trammel;
        var original = map.Season;

        try
        {
            var here = CreateClient(map, new Point3D(1000, 1000, 0));
            var elsewhere = CreateClient(Map.Felucca, new Point3D(1000, 1000, 0));
            var staff = CreateStaff();

            var markHere = Mark(here.State);
            var markElsewhere = Mark(elsewhere.State);

            Run(AmbientSeason.Season_OnCommand, staff, "winter", "trammel");

            Assert.Equal(3, map.Season);
            Assert.True(AmbientSeason.IsOverridden(map));
            Assert.Equal(original, AmbientSeason.GetDefault(map));
            Assert.Equal(new byte[] { 0xBC, 3, 1 }, Since(here.State, markHere));
            Assert.Empty(Since(elsewhere.State, markElsewhere));

            Run(AmbientSeason.Season_OnCommand, staff, "desolation", "trammel");
            Assert.Equal(4, map.Season);
            Assert.Equal(original, AmbientSeason.GetDefault(map));

            markHere = Mark(here.State);
            Run(AmbientSeason.Season_OnCommand, staff, "auto", "trammel");

            Assert.Equal(original, map.Season);
            Assert.False(AmbientSeason.IsOverridden(map));
            Assert.Equal(new byte[] { 0xBC, (byte)original, 1 }, Since(here.State, markHere));
        }
        finally
        {
            AmbientSeason.Restore(map);
            map.Season = original;
            Cleanup();
        }
    }

    [Fact]
    public void Season_CommandRejectsBadInput()
    {
        var map = Map.Trammel;
        var original = map.Season;

        try
        {
            var staff = CreateStaff();

            Run(AmbientSeason.Season_OnCommand, staff, "monsoon", "trammel");
            Run(AmbientSeason.Season_OnCommand, staff, "winter", "nowhere");
            Run(AmbientSeason.Season_OnCommand, staff, "winter", "internal");
            Run(AmbientSeason.Season_OnCommand, staff, "winter"); // staff is on no facet

            Assert.Equal(original, map.Season);
            Assert.False(AmbientSeason.IsOverridden(map));
            Assert.False(AmbientSeason.IsOverridden(Map.Internal));
        }
        finally
        {
            Cleanup();
        }
    }

    [Theory]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("5:07", 5, 7)]
    [InlineData("7", 7, 0)]
    [InlineData(" 12:30 ", 12, 30)]
    public void TimeOfDay_Parse_Valid(string value, int hours, int minutes)
    {
        Assert.True(AmbientTime.TryParse(value, out var h, out var m, out var auto));
        Assert.False(auto);
        Assert.Equal(hours, h);
        Assert.Equal(minutes, m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("-1:00")]
    [InlineData("12:")]
    [InlineData(":30")]
    [InlineData("noon")]
    [InlineData("123:00")]
    public void TimeOfDay_Parse_Invalid(string value) => Assert.False(AmbientTime.TryParse(value, out _, out _, out _));

    [Fact]
    public void TimeOfDay_Parse_Auto()
    {
        Assert.True(AmbientTime.TryParse("AUTO", out _, out _, out var auto));
        Assert.True(auto);
    }

    [Theory]
    [InlineData(2, 0, LightCycle.NightLevel)]
    [InlineData(5, 0, (LightCycle.NightLevel + LightCycle.DayLevel) / 2)]
    [InlineData(12, 0, LightCycle.DayLevel)]
    [InlineData(23, 0, (LightCycle.NightLevel + LightCycle.DayLevel) / 2)]
    public void TimeOfDay_OverrideDrivesLightCycle(int hours, int minutes, int expected)
    {
        var m = CreateStaff();

        try
        {
            AmbientTime.Set(hours, minutes);
            Assert.Equal(expected, LightCycle.ComputeLevelFor(m));
        }
        finally
        {
            AmbientTime.Clear();
            Cleanup();
        }
    }

    [Fact]
    public void TimeOfDay_GlobalLightTakesPrecedence()
    {
        var m = CreateStaff();

        try
        {
            AmbientTime.Set(2, 0);
            LightCycle.LevelOverride = 3;
            Assert.Equal(3, LightCycle.ComputeLevelFor(m));

            LightCycle.LevelOverride = int.MinValue;
            Assert.Equal(LightCycle.NightLevel, LightCycle.ComputeLevelFor(m));
        }
        finally
        {
            LightCycle.LevelOverride = int.MinValue;
            AmbientTime.Clear();
            Cleanup();
        }
    }

    [Fact]
    public void TimeOfDay_CommandSetsAndClears_PushesLightAtOnce()
    {
        try
        {
            var client = CreateClient(Map.Trammel, new Point3D(1000, 1000, 0));
            var staff = CreateStaff();

            Run(AmbientTime.TimeOfDay_OnCommand, staff, "02:00");
            Assert.True(AmbientTime.TryGetOverride(out var h, out var m));
            Assert.Equal((2, 0), (h, m));

            var mark = Mark(client.State);
            Run(AmbientTime.TimeOfDay_OnCommand, staff, "12:00");
            Assert.True(ContainsSequence(Since(client.State, mark), 0x4F, LightCycle.DayLevel));

            mark = Mark(client.State);
            Run(AmbientTime.TimeOfDay_OnCommand, staff, "01:00");
            Assert.True(ContainsSequence(Since(client.State, mark), 0x4F, LightCycle.NightLevel));

            // Same level again: nothing is resent.
            mark = Mark(client.State);
            Run(AmbientTime.TimeOfDay_OnCommand, staff, "02:30");
            Assert.Empty(Since(client.State, mark));

            Run(AmbientTime.TimeOfDay_OnCommand, staff, "25:00");
            Assert.True(AmbientTime.TryGetOverride(out h, out m));
            Assert.Equal((2, 30), (h, m));

            Run(AmbientTime.TimeOfDay_OnCommand, staff, "auto");
            Assert.False(AmbientTime.IsActive);
        }
        finally
        {
            AmbientTime.Clear();
            Cleanup();
        }
    }

    [Theory]
    [InlineData("rain", AmbientWeather.Rain)]
    [InlineData("Storm", AmbientWeather.FierceStorm)]
    [InlineData("snow", AmbientWeather.Snow)]
    [InlineData("brewing", AmbientWeather.StormBrewing)]
    [InlineData("none", AmbientWeather.None)]
    [InlineData("clear", AmbientWeather.None)]
    [InlineData("2", AmbientWeather.Snow)]
    public void Weather_ParseType_Valid(string value, byte expected)
    {
        Assert.True(AmbientWeather.TryParseType(value, out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("254")]
    [InlineData("hail")]
    public void Weather_ParseType_Invalid(string value) => Assert.False(AmbientWeather.TryParseType(value, out _));

    [Fact]
    public void Weather_ForceSendsToMapPlayers_ReleaseClears()
    {
        var map = Map.Trammel;

        try
        {
            var here = CreateClient(map, new Point3D(1000, 1000, 0));
            var elsewhere = CreateClient(Map.Felucca, new Point3D(1000, 1000, 0));

            var markHere = Mark(here.State);
            var markElsewhere = Mark(elsewhere.State);

            AmbientWeather.Force(map, AmbientWeather.Snow, 40);

            Assert.True(AmbientWeather.IsForced(map));
            Assert.False(AmbientWeather.IsForced(Map.Felucca));
            Assert.Equal(new byte[] { 0x65, 0xFF, 0, 0, 0x65, 2, 40, 0 }, Since(here.State, markHere));
            Assert.Empty(Since(elsewhere.State, markElsewhere));

            // A refresh within the client's weather lifetime repeats the bare type without a reset, so a client
            // whose weather was reset by a teleport gets it back.
            markHere = Mark(here.State);
            AmbientWeather.Refresh();
            Assert.Equal(new byte[] { 0x65, 2, 40, 0 }, Since(here.State, markHere));

            // Forcing the same weather again does the same.
            markHere = Mark(here.State);
            AmbientWeather.Force(map, AmbientWeather.Snow, 40);
            Assert.Equal(new byte[] { 0x65, 2, 40, 0 }, Since(here.State, markHere));

            // A changed setting reaches the player at once.
            markHere = Mark(here.State);
            AmbientWeather.Force(map, AmbientWeather.Rain, 10);
            Assert.Equal(new byte[] { 0x65, 0xFF, 0, 0, 0x65, 0, 10, 0 }, Since(here.State, markHere));

            // A player who leaves the facet is cleared, one who arrives gets the forced weather.
            here.Mobile.MoveToWorld(new Point3D(1000, 1000, 0), Map.Felucca);
            elsewhere.Mobile.MoveToWorld(new Point3D(1000, 1000, 0), map);
            markHere = Mark(here.State);
            markElsewhere = Mark(elsewhere.State);
            AmbientWeather.Refresh();
            Assert.Equal(new byte[] { 0x65, 0xFF, 0, 0 }, Since(here.State, markHere));
            Assert.Equal(new byte[] { 0x65, 0xFF, 0, 0, 0x65, 0, 10, 0 }, Since(elsewhere.State, markElsewhere));

            markElsewhere = Mark(elsewhere.State);
            Assert.True(AmbientWeather.Release(map));
            Assert.False(AmbientWeather.IsForced(map));
            Assert.Equal(new byte[] { 0x65, 0xFF, 0, 0 }, Since(elsewhere.State, markElsewhere));
            Assert.False(AmbientWeather.Release(map));
        }
        finally
        {
            AmbientWeather.Release(map);
            Cleanup();
        }
    }

    [Fact]
    public void Weather_CommandParsesTypeIntensityAndMap()
    {
        try
        {
            var staff = CreateStaff();

            Run(AmbientWeather.Weather_OnCommand, staff, "rain", "30", "trammel");
            Assert.True(AmbientWeather.TryGetForced(Map.Trammel, out var weather));
            Assert.Equal(new AmbientWeather.ForcedWeather(AmbientWeather.Rain, 30), weather);

            Run(AmbientWeather.Weather_OnCommand, staff, "snow", "felucca");
            Assert.True(AmbientWeather.TryGetForced(Map.Felucca, out weather));
            Assert.Equal(new AmbientWeather.ForcedWeather(AmbientWeather.Snow, AmbientWeather.MaxIntensity), weather);

            Run(AmbientWeather.Weather_OnCommand, staff, "none", "malas");
            Assert.True(AmbientWeather.TryGetForced(Map.Malas, out weather));
            Assert.Equal(new AmbientWeather.ForcedWeather(AmbientWeather.None, 0), weather);

            Run(AmbientWeather.Weather_OnCommand, staff, "rain", "71", "ilshenar");
            Run(AmbientWeather.Weather_OnCommand, staff, "hail", "ilshenar");
            Assert.False(AmbientWeather.IsForced(Map.Ilshenar));

            Run(AmbientWeather.Weather_OnCommand, staff, "auto", "trammel");
            Assert.False(AmbientWeather.IsForced(Map.Trammel));
            Assert.True(AmbientWeather.IsForced(Map.Felucca));
        }
        finally
        {
            AmbientWeather.Release(Map.Trammel);
            AmbientWeather.Release(Map.Felucca);
            AmbientWeather.Release(Map.Malas);
            AmbientWeather.Release(Map.Ilshenar);
            Cleanup();
        }
    }
}
