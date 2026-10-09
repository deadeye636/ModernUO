using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Server;
using Server.Misc;
using Server.Mobiles;
using Server.Network;
using Server.Tests.Network;
using Xunit;
using Weather = Server.Misc.Weather;

namespace UOContent.Tests.Misc;

[Collection("Sequential UOContent Tests")]
public class AmbienceZoneFileTests
{
    private const string Presets = """
        "version": 1,
        "presetTable": 1,
        "presets": { "night": 1, "dawn": 2, "other": 7 }
        """;

    private static string File(string zones) => $$"""{ {{Presets}}, "zones": [ {{zones}} ] }""";

    private static string Zone(
        string id = "a", string maps = "\"Trammel\"", int priority = 0, int feather = 0,
        string rects = """{ "x1": 100, "y1": 100, "x2": 200, "y2": 200 }""",
        string rules = """{ "preset": "night" }"""
    ) =>
        $$"""
          { "id": "{{id}}", "maps": [{{maps}}], "priority": {{priority}}, "feather": {{feather}},
            "rects": [{{rects}}], "rules": [{{rules}}] }
          """;

    internal static AmbienceZoneSet Parse(string json)
    {
        var errors = new List<string>();
        Assert.True(AmbienceZones.TryParse(json, out var set, errors), string.Join("; ", errors));
        Assert.Empty(errors);
        return set;
    }

    internal static AmbienceZoneSet ParseZones(params string[] zones) => Parse(File(string.Join(",", zones)));

    internal static string MakeZone(
        string id, int priority, int feather, int x1, int y1, int x2, int y2, string rules
    ) => Zone(id, "\"Trammel\"", priority, feather, $$"""{ "x1": {{x1}}, "y1": {{y1}}, "x2": {{x2}}, "y2": {{y2}} }""", rules);

    [Fact]
    public void ShippedZoneFile_Validates()
    {
        var errors = new List<string>();
        var path = Path.Combine(Core.BaseDirectory, "Data", "Ambience", "custom", "ambience-zones.json");

        Assert.True(System.IO.File.Exists(path), path);
        Assert.True(AmbienceZones.TryLoadFile(path, out var set, errors), string.Join("; ", errors));
        Assert.Equal(1, set.PresetTable);
        Assert.Equal(6, set.Presets.Count);
        Assert.NotNull(set.FindZone("britain-graveyard"));
        Assert.NotNull(set.FindZone("huntsmans-forest"));
        Assert.NotNull(set.FindZone("britain-swamp"));
    }

    [Fact]
    public void MissingFile_IsEmptySet()
    {
        var errors = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"ambience-missing-{Guid.NewGuid():N}.json");

        Assert.True(AmbienceZones.TryLoadFile(path, out var set, errors));
        Assert.Same(AmbienceZoneSet.Empty, set);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidFile_ParsesAllFields()
    {
        var set = Parse(
            File(
                Zone(
                    "graves",
                    "\"Trammel\", \"felucca\"",
                    priority: 50,
                    feather: 4,
                    rules: """
                           { "time": ["night", "dusk"], "preset": "night" },
                           { "time": ["dawn"], "weather": ["rain", "snow"], "preset": "dawn", "strength": 0.5, "fade": 6 }
                           """
                )
            )
        );

        var zone = Assert.Single(set.Zones);
        Assert.Equal("graves", zone.Id);
        Assert.Equal(new[] { Map.Trammel, Map.Felucca }, zone.Maps);
        Assert.Equal(50, zone.Priority);
        Assert.Equal(4, zone.Feather);
        Assert.Equal(new AmbienceRect(100, 100, 200, 200), Assert.Single(zone.Rects));

        Assert.Equal(1, zone.Rules[0].PresetId);
        Assert.Equal(1.0, zone.Rules[0].Strength);
        Assert.Equal(AmbienceZones.DefaultFadeTenths, zone.Rules[0].FadeTenths);
        Assert.True(zone.Rules[0].Matches(AmbienceTimeBand.Dusk, AmbienceWeatherClass.Snow));
        Assert.False(zone.Rules[0].Matches(AmbienceTimeBand.Day, AmbienceWeatherClass.Clear));

        Assert.Equal(2, zone.Rules[1].PresetId);
        Assert.Equal(60, zone.Rules[1].FadeTenths);
        Assert.True(zone.Rules[1].Matches(AmbienceTimeBand.Dawn, AmbienceWeatherClass.Rain));
        Assert.False(zone.Rules[1].Matches(AmbienceTimeBand.Dawn, AmbienceWeatherClass.Clear));
        Assert.Equal("dawn", set.GetPresetName(2));
        Assert.Equal("none", set.GetPresetName(0));
    }

    public static TheoryData<string, string> InvalidFiles() => new()
    {
        { "not json", "{ \"version\": 1, " },
        { "empty", "null" },
        { "version", """{ "version": 2, "presetTable": 1, "presets": {}, "zones": [] }""" },
        { "no version", """{ "presetTable": 1, "presets": {}, "zones": [] }""" },
        { "table 0", """{ "version": 1, "presetTable": 0, "presets": {}, "zones": [] }""" },
        { "table too big", """{ "version": 1, "presetTable": 65536, "presets": {}, "zones": [] }""" },
        { "no presets", """{ "version": 1, "presetTable": 1, "zones": [] }""" },
        { "preset id 0", """{ "version": 1, "presetTable": 1, "presets": { "a": 0 }, "zones": [] }""" },
        { "preset id too big", """{ "version": 1, "presetTable": 1, "presets": { "a": 65536 }, "zones": [] }""" },
        { "preset id twice", """{ "version": 1, "presetTable": 1, "presets": { "a": 1, "b": 1 }, "zones": [] }""" },
        { "no zones", """{ "version": 1, "presetTable": 1, "presets": {} }""" },
        { "unknown key", """{ "version": 1, "presetTable": 1, "presets": {}, "zones": [], "zonez": [] }""" },
        { "unknown zone key", File(Zone().Replace("\"feather\"", "\"fether\"")) },
        { "unknown rule key", File(Zone(rules: """{ "preset": "night", "fdae": 2 }""")) },
        { "empty id", File(Zone(id: " ")) },
        { "id twice", File(Zone("a") + "," + Zone("A")) },
        { "no maps", File(Zone(maps: "")) },
        { "unknown map", File(Zone(maps: "\"Atlantis\"")) },
        { "internal map", File(Zone(maps: "\"Internal\"")) },
        { "map twice", File(Zone(maps: "\"Trammel\", \"trammel\"")) },
        { "feather negative", File(Zone(feather: -1)) },
        { "feather too big", File(Zone(feather: 33)) },
        { "no rects", File(Zone(rects: "")) },
        { "empty rect", File(Zone(rects: """{ "x1": 100, "y1": 100, "x2": 100, "y2": 200 }""")) },
        { "reversed rect", File(Zone(rects: """{ "x1": 200, "y1": 100, "x2": 100, "y2": 200 }""")) },
        { "negative rect", File(Zone(rects: """{ "x1": -5, "y1": 100, "x2": 100, "y2": 200 }""")) },
        { "rect outside map", File(Zone(rects: """{ "x1": 7000, "y1": 100, "x2": 7169, "y2": 200 }""")) },
        { "rect bad key", File(Zone(rects: """{ "x1": 1, "y1": 1, "x3": 5, "y2": 5 }""")) },
        { "no rules", File(Zone(rules: "")) },
        { "unknown time", File(Zone(rules: """{ "time": ["noon"], "preset": "night" }""")) },
        { "empty time", File(Zone(rules: """{ "time": [], "preset": "night" }""")) },
        { "unknown weather", File(Zone(rules: """{ "weather": ["fog"], "preset": "night" }""")) },
        { "no preset", File(Zone(rules: """{ "time": ["night"] }""")) },
        { "unknown preset", File(Zone(rules: """{ "preset": "volcano" }""")) },
        { "strength too big", File(Zone(rules: """{ "preset": "night", "strength": 1.5 }""")) },
        { "strength negative", File(Zone(rules: """{ "preset": "night", "strength": -0.1 }""")) },
        { "fade too long", File(Zone(rules: """{ "preset": "night", "fade": 61 }""")) },
        { "wrong type", File(Zone(rules: """{ "preset": "night", "strength": "high" }""")) }
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void InvalidFile_IsRejectedAsAWhole(string name, string json)
    {
        var errors = new List<string>();

        Assert.False(AmbienceZones.TryParse(json, out var set, errors), name);
        Assert.Null(set);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void OneBadZone_RejectsTheWholeFile_AndListsEveryError()
    {
        var errors = new List<string>();
        var json = File(Zone("good") + "," + Zone("bad", feather: 40, rules: """{ "preset": "volcano" }"""));

        Assert.False(AmbienceZones.TryParse(json, out _, errors));
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("'bad'", e));
    }
}

[Collection("Sequential UOContent Tests")]
public class AmbienceGridTests
{
    private const string Always = """{ "preset": "night" }""";

    [Fact]
    public void Lookup_UsesRegionEdgeRule_X2Y2Exclusive()
    {
        var set = AmbienceZoneFileTests.ParseZones(AmbienceZoneFileTests.MakeZone("a", 0, 0, 100, 100, 200, 200, Always));
        var grid = set.Grid;

        Assert.True(Find(grid, 100, 100, out _));
        Assert.True(Find(grid, 199, 199, out _));
        Assert.False(Find(grid, 200, 150, out _));
        Assert.False(Find(grid, 150, 200, out _));
        Assert.False(Find(grid, 99, 150, out _));
    }

    [Fact]
    public void Lookup_CoversEveryCellOfALargeRect_AndOnlyItsMaps()
    {
        var set = AmbienceZoneFileTests.ParseZones(AmbienceZoneFileTests.MakeZone("a", 0, 0, 1024, 2144, 1280, 2304, Always));
        var grid = set.Grid;

        for (var x = 1024; x < 1280; x += 7)
        {
            for (var y = 2144; y < 2304; y += 5)
            {
                Assert.True(Find(grid, x, y, out _));
            }
        }

        Assert.True(Find(grid, 1279, 2303, out _));
        Assert.False(grid.TryFind(Map.Felucca, 1100, 2200, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear, out _));
        Assert.False(grid.TryFind(null, 1100, 2200, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear, out _));
        Assert.False(Find(grid, -1, 2200, out _));
        Assert.False(Find(grid, 100000, 2200, out _));
    }

    [Fact]
    public void Overlap_HigherPriorityWins_EqualPriorityTheFirstListed()
    {
        var set = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone("low", 10, 0, 100, 100, 200, 200, Always),
            AmbienceZoneFileTests.MakeZone("high", 20, 0, 150, 150, 250, 250, Always),
            AmbienceZoneFileTests.MakeZone("high-second", 20, 0, 150, 150, 250, 250, Always)
        );

        Assert.True(Find(set.Grid, 120, 120, out var m));
        Assert.Equal("low", m.Zone.Id);

        Assert.True(Find(set.Grid, 160, 160, out m));
        Assert.Equal("high", m.Zone.Id);

        Assert.True(Find(set.Grid, 240, 240, out m));
        Assert.Equal("high", m.Zone.Id);
    }

    [Fact]
    public void ZoneWithoutMatchingRule_DoesNotHideALowerZone()
    {
        var set = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone("low", 10, 0, 100, 100, 200, 200, Always),
            AmbienceZoneFileTests.MakeZone("night-only", 20, 0, 100, 100, 200, 200, """{ "time": ["night"], "preset": "dawn" }""")
        );

        Assert.True(set.Grid.TryFind(Map.Trammel, 150, 150, AmbienceTimeBand.Night, AmbienceWeatherClass.Clear, out var m));
        Assert.Equal("night-only", m.Zone.Id);

        Assert.True(set.Grid.TryFind(Map.Trammel, 150, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear, out m));
        Assert.Equal("low", m.Zone.Id);
        Assert.Equal("night-only", set.Grid.FindZoneAt(Map.Trammel, 150, 150).Id);
    }

    [Fact]
    public void Rules_FirstMatchWins()
    {
        var set = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone(
                "a",
                0,
                0,
                100,
                100,
                200,
                200,
                """
                { "time": ["night", "dusk"], "preset": "night" },
                { "time": ["dawn"], "preset": "dawn" },
                { "weather": ["rain"], "preset": "night", "strength": 0.5 }
                """
            )
        );

        Assert.True(set.Grid.TryFind(Map.Trammel, 150, 150, AmbienceTimeBand.Night, AmbienceWeatherClass.Rain, out var m));
        Assert.Equal(0, m.RuleIndex);
        Assert.True(set.Grid.TryFind(Map.Trammel, 150, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Rain, out m));
        Assert.Equal(2, m.RuleIndex);
        Assert.False(set.Grid.TryFind(Map.Trammel, 150, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear, out _));
    }

    [Theory]
    [InlineData(100, 150, 0)]
    [InlineData(101, 150, 1)]
    [InlineData(102, 150, 2)]
    [InlineData(104, 150, 4)]
    [InlineData(150, 150, 49)]
    [InlineData(199, 150, 0)]
    [InlineData(150, 197, 2)]
    public void EdgeDistance_CountsTilesToTheOutermostRow(int x, int y, int expected)
    {
        var zone = AmbienceZoneFileTests.ParseZones(AmbienceZoneFileTests.MakeZone("a", 0, 4, 100, 100, 200, 200, Always))
            .Zones[0];

        Assert.Equal(expected, zone.EdgeDistance(x, y));
    }

    [Fact]
    public void EdgeDistance_OfOverlappingRects_TakesTheLargest()
    {
        var zone = AmbienceZoneFileTests.ParseZones(
                """
                { "id": "u", "maps": ["Trammel"], "feather": 8,
                  "rects": [{ "x1": 100, "y1": 100, "x2": 200, "y2": 200 }, { "x1": 190, "y1": 100, "x2": 300, "y2": 200 }],
                  "rules": [{ "preset": "night" }] }
                """
            )
            .Zones[0];

        Assert.Equal(9, zone.EdgeDistance(199, 150));
        Assert.Equal(-1, zone.EdgeDistance(300, 150));
    }

    private static bool Find(AmbienceGrid grid, int x, int y, out AmbienceMatch match) =>
        grid.TryFind(Map.Trammel, x, y, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear, out match);
}

[Collection("Sequential UOContent Tests")]
public class AmbienceResolveTests
{
    [Theory]
    [InlineData(0, AmbienceTimeBand.Night)]
    [InlineData(3, AmbienceTimeBand.Night)]
    [InlineData(4, AmbienceTimeBand.Dawn)]
    [InlineData(5, AmbienceTimeBand.Dawn)]
    [InlineData(6, AmbienceTimeBand.Day)]
    [InlineData(21, AmbienceTimeBand.Day)]
    [InlineData(22, AmbienceTimeBand.Dusk)]
    [InlineData(23, AmbienceTimeBand.Dusk)]
    public void Band_FollowsTheLightCycleBands(int hours, AmbienceTimeBand expected) =>
        Assert.Equal(expected, AmbienceSystem.GetBand(hours));

    [Theory]
    [InlineData(AmbientWeather.Rain, AmbienceWeatherClass.Rain)]
    [InlineData(AmbientWeather.FierceStorm, AmbienceWeatherClass.Rain)]
    [InlineData(AmbientWeather.Snow, AmbienceWeatherClass.Snow)]
    [InlineData(AmbientWeather.StormBrewing, AmbienceWeatherClass.Clear)]
    [InlineData(AmbientWeather.None, AmbienceWeatherClass.Clear)]
    public void ForcedWeather_Classifies(byte type, AmbienceWeatherClass expected) =>
        Assert.Equal(expected, AmbienceSystem.ClassifyForced(type));

    [Theory]
    [InlineData(100, 0)]
    [InlineData(101, 64)]
    [InlineData(102, 128)]
    [InlineData(103, 191)]
    [InlineData(104, 255)]
    [InlineData(150, 255)]
    public void Feather_RampsStrengthFromTheEdge(int x, byte expected)
    {
        var set = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone("a", 0, 4, 100, 100, 200, 200, """{ "preset": "night" }""")
        );

        var result = AmbienceSystem.Resolve(set, Map.Trammel, x, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear);

        Assert.Equal(expected, result.Strength);
        Assert.Equal(255, result.FullStrength);
        Assert.Equal(expected == 0 ? 0 : 1, result.WirePreset);
    }

    [Fact]
    public void RuleStrength_ScalesTheFeather_AndNoFeatherIsFullAtTheEdge()
    {
        var set = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone("half", 0, 4, 100, 100, 200, 200, """{ "preset": "night", "strength": 0.5 }"""),
            AmbienceZoneFileTests.MakeZone("hard", 0, 0, 300, 100, 400, 200, """{ "preset": "dawn", "fade": 2.5 }""")
        );

        var half = AmbienceSystem.Resolve(set, Map.Trammel, 102, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear);
        Assert.Equal(64, half.Strength);
        Assert.Equal(128, half.FullStrength);

        var hard = AmbienceSystem.Resolve(set, Map.Trammel, 300, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear);
        Assert.Equal(255, hard.Strength);
        Assert.Equal(2, hard.PresetId);
        Assert.Equal(25, hard.FadeTenths);

        var outside = AmbienceSystem.Resolve(set, Map.Trammel, 250, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear);
        Assert.Equal(AmbienceResult.None, outside);
    }
}

[Collection("Sequential UOContent Tests")]
public class AmbienceEvaluateTests
{
    private static readonly AmbienceZoneSet _set = AmbienceZoneFileTests.ParseZones(
        AmbienceZoneFileTests.MakeZone("a", 0, 16, 100, 100, 200, 200, """{ "preset": "night", "fade": 3 }"""),
        AmbienceZoneFileTests.MakeZone("b", 0, 0, 300, 100, 400, 200, """{ "preset": "dawn", "fade": 7 }""")
    );

    private const long Start = 1_000_000;

    private static AmbienceResult At(int x) =>
        AmbienceSystem.Resolve(_set, Map.Trammel, x, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear);

    private static bool Step(AmbienceClientState s, int x, long now, out ushort fade) =>
        AmbienceSystem.Evaluate(s, At(x), Map.Trammel, new Point3D(x, 150, 0), now, out fade);

    [Fact]
    public void NoChange_SendsNothing()
    {
        var s = new AmbienceClientState(1, 1, Start);

        Assert.False(Step(s, 50, Start, out _));
        Assert.False(Step(s, 60, Start + 1000, out _));
        Assert.Equal(0, s.PacketsSent);
    }

    [Fact]
    public void EnteringAZone_SendsAtOnceWithTheRuleFade_LeavingFadesOutWithIt()
    {
        var s = new AmbienceClientState(1, 1, Start);

        Assert.False(Step(s, 290, Start, out _));
        Assert.True(Step(s, 300, Start + 100, out var fade));
        Assert.Equal(2, s.SentPreset);
        Assert.Equal(255, s.SentStrength);
        Assert.Equal(70, fade);

        Assert.True(Step(s, 299, Start + 200, out fade));
        Assert.Equal(0, s.SentPreset);
        Assert.Equal(0, s.SentStrength);
        Assert.Equal(70, fade);
    }

    [Fact]
    public void Feather_SendsStepsOf16OrMore_AtMostOncePerSecond_WithOneSecondFade()
    {
        var s = new AmbienceClientState(1, 1, Start);
        var now = Start;

        // feather 16: 16 strength per tile
        Assert.True(Step(s, 101, now, out var fade));
        Assert.Equal(16, s.SentStrength);
        Assert.Equal(30, fade);

        now += 1000;
        Assert.True(Step(s, 102, now, out fade));
        Assert.Equal(32, s.SentStrength);
        Assert.Equal(AmbienceSystem.StepFadeTenths, fade);

        // the next tile is a full step but comes too soon
        Assert.False(Step(s, 103, now + 500, out _));
        Assert.True(Step(s, 103, now + 1000, out _));
        Assert.Equal(48, s.SentStrength);
    }

    [Fact]
    public void Feather_SmallStepsAccumulate_AndTheEndsAreAlwaysSent()
    {
        var zone = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone("wide", 0, 32, 100, 100, 200, 200, """{ "preset": "night" }""")
        );
        var s = new AmbienceClientState(1, 1, Start);
        var now = Start;

        bool StepWide(int x) =>
            AmbienceSystem.Evaluate(
                s,
                AmbienceSystem.Resolve(zone, Map.Trammel, x, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear),
                Map.Trammel,
                new Point3D(x, 150, 0),
                now += 1000,
                out _
            );

        // 255 / 32 = about 8 per tile
        Assert.True(StepWide(102));
        var first = s.SentStrength;
        Assert.False(StepWide(103));
        Assert.True(StepWide(104));
        Assert.True(s.SentStrength - first >= AmbienceSystem.FeatherStep);

        // walking out: the last step to 0 is below 16 but is the end of the ramp
        Assert.True(StepWide(102));
        Assert.True(StepWide(100));
        Assert.Equal(0, s.SentPreset);
        Assert.Equal(0, s.SentStrength);
    }

    [Fact]
    public void ForcedPresetChange_IsSentAtOnce()
    {
        var s = new AmbienceClientState(1, 1, Start);
        var first = new AmbienceResult(null, -1, true, 2, 128, 128, AmbienceSystem.StepFadeTenths, -1);
        var here = new Point3D(50, 150, 0);

        Assert.True(AmbienceSystem.Evaluate(s, first, Map.Trammel, here, Start, out _));
        Assert.True(AmbienceSystem.Evaluate(s, first with { PresetId = 3 }, Map.Trammel, here, Start + 100, out var fade));
        Assert.Equal(3, s.SentPreset);
        Assert.Equal(AmbienceSystem.StepFadeTenths, fade);

        Assert.True(
            AmbienceSystem.Evaluate(s, first with { PresetId = 3, Strength = 120 }, Map.Trammel, here, Start + 200, out _)
        );
        Assert.Equal(120, s.SentStrength);
    }

    [Fact]
    public void NeighbourZoneWithTheSamePreset_SendsNothing_ButItsFadeIsUsedOnLeaving()
    {
        var set = AmbienceZoneFileTests.ParseZones(
            AmbienceZoneFileTests.MakeZone("x", 0, 0, 100, 100, 200, 200, """{ "preset": "night", "fade": 3 }"""),
            AmbienceZoneFileTests.MakeZone("y", 0, 0, 200, 100, 300, 200, """{ "preset": "night", "fade": 9 }""")
        );
        var s = new AmbienceClientState(1, 1, Start);
        var now = Start;

        bool Walk(int x, out ushort fade) =>
            AmbienceSystem.Evaluate(
                s,
                AmbienceSystem.Resolve(set, Map.Trammel, x, 150, AmbienceTimeBand.Day, AmbienceWeatherClass.Clear),
                Map.Trammel,
                new Point3D(x, 150, 0),
                now += 1000,
                out fade
            );

        Assert.True(Walk(199, out var fade));
        Assert.Equal(30, fade);
        for (var x = 200; x < 300; x += 10)
        {
            Assert.False(Walk(x, out _));
        }

        Assert.True(Walk(300, out fade));
        Assert.Equal(0, s.SentPreset);
        Assert.Equal(90, fade);
    }

    [Fact]
    public void Teleport_UsesTheShortFade()
    {
        var s = new AmbienceClientState(1, 1, Start);

        Assert.False(Step(s, 250, Start, out _));
        Assert.True(Step(s, 350, Start + 1000, out var fade));
        Assert.Equal(AmbienceSystem.TeleportFadeTenths, fade);
    }
}

[Collection("Sequential UOContent Tests")]
public class AmbienceProtocolTests : IDisposable
{
    private readonly List<(NetState State, Mobile Mobile)> _clients = [];
    private readonly AmbienceZoneSet _previous = AmbienceZones.Current;

    public AmbienceProtocolTests()
    {
        AmbienceClients.Configure();
        AmbienceZones.SetCurrent(
            AmbienceZoneFileTests.ParseZones(
                AmbienceZoneFileTests.MakeZone("a", 0, 0, 1000, 1000, 1100, 1100, """{ "preset": "other", "fade": 2 }""")
            )
        );
    }

    public void Dispose()
    {
        foreach (var (state, mobile) in _clients)
        {
            AmbienceClients.Remove(state);
            state.Mobile = null;
            state.Dispose();
            mobile.Delete();
        }

        AmbienceSystem.ClearForce();
        AmbienceZones.SetCurrent(_previous);
        AmbientTime.Clear();
    }

    private NetState CreateClient(Point3D location)
    {
        var ns = PacketTestUtilities.CreateTestNetState();
        ns.Account = new MockAccount();

        var m = new PlayerMobile(World.NewMobile);
        m.DefaultMobileInit();
        ns.Mobile = m;
        m.NetState = ns;
        m.MoveToWorld(location, Map.Trammel);

        _clients.Add((ns, m));
        return ns;
    }

    private static int Mark(NetState ns) => ns.SendBuffer.GetReadSpan().Length;

    private static byte[] Since(NetState ns, int mark) => ns.SendBuffer.GetReadSpan()[mark..].ToArray();

    private static byte[] HelloPacket(byte version = 1, uint capabilities = 1, ushort table = 1) =>
    [
        0xBF, 0x00, 0x0C, 0x00, 0xE0, version,
        (byte)(capabilities >> 24), (byte)(capabilities >> 16), (byte)(capabilities >> 8), (byte)capabilities,
        (byte)(table >> 8), (byte)table
    ];

    // As the network layer hands 0xBF to its handler: a reader limited to the packet, after id and length.
    private static void Receive(NetState ns, byte[] packet) =>
        IncomingExtendedCommandPackets.ExtendedCommand(ns, new SpanReader(packet.AsSpan(3)));

    private static void AssertConnected(NetState ns) => Assert.Equal(string.Empty, ns._disconnectReason);

    [Fact]
    public void Preset_HasTheExactVersion1Layout()
    {
        Span<byte> buffer = stackalloc byte[AmbienceClients.PresetLength];
        var length = AmbienceClients.WritePreset(buffer, 0x0102, 0x80, 600);

        Assert.Equal(12, length);
        Assert.Equal(
            new byte[] { 0xBF, 0x00, 0x0C, 0x00, 0xE1, 0x01, 0x01, 0x02, 0x80, 0x02, 0x58, 0x00 },
            buffer.ToArray()
        );
    }

    [Fact]
    public void Subcommands_AreRegistered_IngameOnly()
    {
        var hello = IncomingExtendedCommandPackets.GetExtendedHandler(0xE0);
        var preset = IncomingExtendedCommandPackets.GetExtendedHandler(0xE1);

        Assert.NotNull(hello);
        Assert.NotNull(preset);
        Assert.True(hello.InGameOnly);
        Assert.True(preset.InGameOnly);
    }

    [Fact]
    public void Hello_InAZone_RegistersAndSendsThePresetAtOnceWithoutFade()
    {
        AmbientTime.Set(12, 0);
        var ns = CreateClient(new Point3D(1050, 1050, 0));
        var mark = Mark(ns);

        Receive(ns, HelloPacket(capabilities: 0x3, table: 1));

        Assert.True(AmbienceClients.TryGetState(ns, out var state));
        Assert.Equal(0x3u, state.Capabilities);
        Assert.Equal(1, state.PresetTable);
        Assert.Equal(
            new byte[] { 0xBF, 0x00, 0x0C, 0x00, 0xE1, 0x01, 0x00, 0x07, 0xFF, 0x00, 0x00, 0x00 },
            Since(ns, mark)
        );
        AssertConnected(ns);
    }

    [Fact]
    public void Hello_OutsideAnyZone_RegistersButSendsNothing()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));
        var mark = Mark(ns);

        Receive(ns, HelloPacket());

        Assert.True(AmbienceClients.TryGetState(ns, out _));
        Assert.Empty(Since(ns, mark));
    }

    [Fact]
    public void Hello_UnknownCapabilityBitsAreMasked_TableMismatchStillRegisters()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));

        Receive(ns, HelloPacket(capabilities: 0xFFFFFFFF, table: 9));

        Assert.True(AmbienceClients.TryGetState(ns, out var state));
        Assert.Equal(AmbienceClients.KnownCapabilities, state.Capabilities);
        Assert.Equal(9, state.PresetTable);
    }

    [Fact]
    public void Hello_OnlyTheFirstAnnouncementCounts()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));

        Receive(ns, HelloPacket(capabilities: 0x1, table: 1));
        Receive(ns, HelloPacket(capabilities: 0x3, table: 2));

        Assert.True(AmbienceClients.TryGetState(ns, out var state));
        Assert.Equal(0x1u, state.Capabilities);
        Assert.Equal(1, state.PresetTable);
    }

    public static TheoryData<string, byte[]> BadHellos() => new()
    {
        { "no payload", new byte[] { 0xBF, 0x00, 0x05, 0x00, 0xE0 } },
        { "one byte short", new byte[] { 0xBF, 0x00, 0x0B, 0x00, 0xE0, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00 } },
        { "one byte long", new byte[] { 0xBF, 0x00, 0x0D, 0x00, 0xE0, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 } },
        { "version 2", HelloPacket(version: 2) },
        { "version 0", HelloPacket(version: 0) },
        { "no capability", HelloPacket(capabilities: 0) },
        { "only unknown capabilities", HelloPacket(capabilities: 0xFFFFFFF8) }
    };

    [Theory]
    [MemberData(nameof(BadHellos))]
    public void BadHello_IsDroppedSilently_WithoutDisconnect(string name, byte[] packet)
    {
        var ns = CreateClient(new Point3D(1050, 1050, 0));
        var mark = Mark(ns);

        Receive(ns, packet);

        Assert.False(AmbienceClients.TryGetState(ns, out _), name);
        Assert.Empty(Since(ns, mark));
        AssertConnected(ns);
    }

    [Fact]
    public void BadHello_DoesNotUseUpTheFirstAnnouncement()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));

        Receive(ns, HelloPacket(version: 2));
        Receive(ns, HelloPacket());

        Assert.True(AmbienceClients.TryGetState(ns, out _));
    }

    [Fact]
    public void Garbage_NeverThrows_NeverDisconnects()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));
        var random = new Random(4711);

        for (var i = 0; i < 2000; i++)
        {
            var length = random.Next(0, 64);
            var payload = new byte[length];
            random.NextBytes(payload);

            // a claimed length that disagrees with the real one must not matter
            var packet = new byte[5 + length];
            packet[0] = 0xBF;
            packet[1] = (byte)random.Next(256);
            packet[2] = (byte)random.Next(256);
            packet[3] = 0x00;
            packet[4] = (byte)(i % 2 == 0 ? 0xE0 : 0xE1);
            payload.CopyTo(packet, 5);

            AmbienceClients.Hello(ns, new SpanReader(payload));
            Receive(ns, packet);
        }

        AssertConnected(ns);
    }

    [Fact]
    public void ClientSendingThePresetSubcommand_IsIgnored()
    {
        var ns = CreateClient(new Point3D(1050, 1050, 0));
        var mark = Mark(ns);

        Receive(ns, [0xBF, 0x00, 0x0C, 0x00, 0xE1, 0x01, 0x00, 0x01, 0xFF, 0x00, 0x00, 0x00]);

        Assert.False(AmbienceClients.TryGetState(ns, out _));
        Assert.Empty(Since(ns, mark));
        AssertConnected(ns);
    }

    [Fact]
    public void Tick_SendsOnlyToAnnouncedClients_AndOnlyOnChange()
    {
        AmbientTime.Set(12, 0);
        var announced = CreateClient(new Point3D(2000, 2000, 0));
        var silent = CreateClient(new Point3D(2000, 2000, 0));

        Receive(announced, HelloPacket());

        announced.Mobile.MoveToWorld(new Point3D(1050, 1050, 0), Map.Trammel);
        silent.Mobile.MoveToWorld(new Point3D(1050, 1050, 0), Map.Trammel);

        var markAnnounced = Mark(announced);
        var markSilent = Mark(silent);

        AmbienceSystem.Tick();
        AmbienceSystem.Tick();

        // a move of more than 18 tiles in one tick counts as a teleport: half a second fade
        Assert.Equal(
            new byte[] { 0xBF, 0x00, 0x0C, 0x00, 0xE1, 0x01, 0x00, 0x07, 0xFF, 0x00, 0x05, 0x00 },
            Since(announced, markAnnounced)
        );
        Assert.Empty(Since(silent, markSilent));
    }

    [Fact]
    public void Force_OverridesTheZones_AutoReturnsToThem()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));
        Receive(ns, HelloPacket());
        var mark = Mark(ns);

        AmbienceSystem.Force(2, 128);
        Assert.Equal(
            new byte[] { 0xBF, 0x00, 0x0C, 0x00, 0xE1, 0x01, 0x00, 0x02, 0x80, 0x00, 0x0A, 0x00 },
            Since(ns, mark)
        );

        mark = Mark(ns);
        AmbienceSystem.ClearForce();
        var back = Since(ns, mark);
        Assert.Equal(12, back.Length);
        Assert.Equal(0, back[6] << 8 | back[7]);
        Assert.Equal(0, back[8]);
    }

    [Fact]
    public void Tick_DropsSessionsThatAreGone()
    {
        var ns = CreateClient(new Point3D(2000, 2000, 0));
        Receive(ns, HelloPacket());
        Assert.True(AmbienceClients.TryGetState(ns, out _));

        NetState.Instances.Remove(ns);

        try
        {
            AmbienceSystem.Tick();
            Assert.False(AmbienceClients.TryGetState(ns, out _));
        }
        finally
        {
            NetState.Instances.Add(ns);
        }
    }
}

[Collection("Sequential UOContent Tests")]
public class AmbienceReloadTests
{
    [Fact]
    public void Reload_SwapsAValidFile_KeepsTheOldSetOnError()
    {
        var previousPath = AmbienceZones.FilePath;
        var previousSet = AmbienceZones.Current;
        var path = Path.Combine(Path.GetTempPath(), $"ambience-zones-{Guid.NewGuid():N}.json");

        try
        {
            AmbienceZones.FilePath = path;

            File.WriteAllText(
                path,
                """
                { "version": 1, "presetTable": 3, "presets": { "p": 1 },
                  "zones": [{ "id": "z", "maps": ["Trammel"], "rects": [{ "x1": 1, "y1": 1, "x2": 9, "y2": 9 }],
                              "rules": [{ "preset": "p" }] }] }
                """
            );

            var errors = new List<string>();
            Assert.True(AmbienceZones.Reload(errors));
            var loaded = AmbienceZones.Current;
            Assert.Equal(3, loaded.PresetTable);
            Assert.Single(loaded.Zones);

            File.WriteAllText(path, """{ "version": 1, "presetTable": 3, "presets": { "p": 1 }, "zones": [{ "id": "" }] }""");

            Assert.False(AmbienceZones.Reload(errors));
            Assert.NotEmpty(errors);
            Assert.Same(loaded, AmbienceZones.Current);

            File.Delete(path);
            errors.Clear();
            Assert.True(AmbienceZones.Reload(errors));
            Assert.Same(AmbienceZoneSet.Empty, AmbienceZones.Current);
        }
        finally
        {
            AmbienceZones.FilePath = previousPath;
            AmbienceZones.SetCurrent(previousSet);
            File.Delete(path);
        }
    }
}

[Collection("Sequential UOContent Tests")]
public class AmbienceWeatherAndCommandTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static Weather CreateWeather(Map map, Rectangle2D area, int temperature, bool active, int stage, bool extreme)
    {
        // Precipitation chance 0 and a day-long interval: the weather's own timer never changes the state under test.
        var weather = new Weather(map, [area], temperature, 0, 0, TimeSpan.FromDays(1));
        typeof(Weather).GetField("m_Active", Private)!.SetValue(weather, active);
        typeof(Weather).GetField("m_Stage", Private)!.SetValue(weather, stage);
        typeof(Weather).GetField("m_ExtremeTemperature", Private)!.SetValue(weather, extreme);
        return weather;
    }

    [Theory]
    [InlineData(false, 5, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 2, true)]
    [InlineData(true, 29, true)]
    public void IsPrecipitating_SkipsTheStageThatSentDensityZero(bool active, int stage, bool expected)
    {
        var weather = CreateWeather(Map.Ilshenar, new Rectangle2D(10, 10, 5, 5), 10, active, stage, false);

        try
        {
            Assert.Equal(expected, weather.IsPrecipitating);
        }
        finally
        {
            Weather.GetWeatherList(Map.Ilshenar).Remove(weather);
        }
    }

    [Theory]
    [InlineData(10, false, false)]
    [InlineData(10, true, true)]
    [InlineData(-10, false, true)]
    [InlineData(-10, true, false)]
    [InlineData(0, false, true)]
    public void IsSnowing_FlipsTheTemperatureWhenExtreme(int temperature, bool extreme, bool expected)
    {
        var weather = CreateWeather(Map.Ilshenar, new Rectangle2D(10, 10, 5, 5), temperature, true, 5, extreme);

        try
        {
            Assert.Equal(expected, weather.IsSnowing);
        }
        finally
        {
            Weather.GetWeatherList(Map.Ilshenar).Remove(weather);
        }
    }

    [Fact]
    public void TryGetPrecipitation_FirstPrecipitatingWeatherCoveringThePointWins()
    {
        var map = Map.Ilshenar;
        var dry = CreateWeather(map, new Rectangle2D(100, 100, 50, 50), 10, false, 5, false);
        var snow = CreateWeather(map, new Rectangle2D(100, 100, 50, 50), -5, true, 5, false);
        var rain = CreateWeather(map, new Rectangle2D(100, 100, 50, 50), 20, true, 5, false);
        var elsewhere = CreateWeather(map, new Rectangle2D(300, 300, 10, 10), 20, true, 5, false);

        try
        {
            Assert.True(Weather.TryGetPrecipitation(map, new Point3D(120, 120, 0), out var isSnow));
            Assert.True(isSnow);
            Assert.Equal(AmbienceWeatherClass.Snow, AmbienceSystem.GetWeather(map, new Point3D(120, 120, 0)));

            Assert.True(Weather.TryGetPrecipitation(map, new Point3D(305, 305, 0), out isSnow));
            Assert.False(isSnow);
            Assert.Equal(AmbienceWeatherClass.Rain, AmbienceSystem.GetWeather(map, new Point3D(305, 305, 0)));

            Assert.False(Weather.TryGetPrecipitation(map, new Point3D(200, 200, 0), out _));
            Assert.Equal(AmbienceWeatherClass.Clear, AmbienceSystem.GetWeather(map, new Point3D(200, 200, 0)));
            Assert.False(Weather.TryGetPrecipitation(Map.Tokuno, new Point3D(120, 120, 0), out _));
            Assert.False(Weather.TryGetPrecipitation(null, new Point3D(120, 120, 0), out _));
        }
        finally
        {
            var list = Weather.GetWeatherList(map);
            list.Remove(dry);
            list.Remove(snow);
            list.Remove(rain);
            list.Remove(elsewhere);
        }
    }

    [Fact]
    public void ForcedWeather_WinsOverTheRegularWeather()
    {
        var map = Map.Ilshenar;
        var rain = CreateWeather(map, new Rectangle2D(100, 100, 50, 50), 20, true, 5, false);

        try
        {
            Assert.Equal(AmbienceWeatherClass.Rain, AmbienceSystem.GetWeather(map, new Point3D(120, 120, 0)));
            AmbientWeather.Force(map, AmbientWeather.StormBrewing, 30);
            Assert.Equal(AmbienceWeatherClass.Clear, AmbienceSystem.GetWeather(map, new Point3D(120, 120, 0)));
            AmbientWeather.Force(map, AmbientWeather.Snow, 30);
            Assert.Equal(AmbienceWeatherClass.Snow, AmbienceSystem.GetWeather(map, new Point3D(120, 120, 0)));
        }
        finally
        {
            AmbientWeather.Release(map);
            Weather.GetWeatherList(map).Remove(rain);
        }
    }

    [Fact]
    public void Band_UsesTheTimeOverride_ElseTheClockAtThatPlace()
    {
        try
        {
            AmbientTime.Set(2, 0);
            Assert.Equal(AmbienceTimeBand.Night, AmbienceSystem.GetBand(Map.Trammel, 1000, 1000));
            AmbientTime.Set(5, 30);
            Assert.Equal(AmbienceTimeBand.Dawn, AmbienceSystem.GetBand(Map.Trammel, 1000, 1000));
            AmbientTime.Set(23, 0);
            Assert.Equal(AmbienceTimeBand.Dusk, AmbienceSystem.GetBand(Map.Trammel, 1000, 1000));

            AmbientTime.Clear();
            Server.Items.Clock.GetTime(Map.Trammel, 1000, 1000, out int hours, out int _);
            Assert.Equal(AmbienceSystem.GetBand(hours), AmbienceSystem.GetBand(Map.Trammel, 1000, 1000));
        }
        finally
        {
            AmbientTime.Clear();
        }
    }

    private static void Run(CommandEventHandler handler, Mobile from, params string[] args) =>
        handler(new CommandEventArgs(from, "test", string.Join(' ', args), args));

    [Fact]
    public void ForceCommand_ParsesPresetAndStrength_RejectsBadInput()
    {
        var previous = AmbienceZones.Current;
        var staff = new Mobile(World.NewMobile);
        staff.DefaultMobileInit();
        staff.AccessLevel = AccessLevel.GameMaster;

        try
        {
            AmbienceZones.SetCurrent(
                AmbienceZoneFileTests.ParseZones(
                    AmbienceZoneFileTests.MakeZone("a", 0, 0, 1, 1, 9, 9, """{ "preset": "night" }""")
                )
            );

            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "night", "0.5");
            Assert.Equal(((ushort)1, (byte)128), AmbienceSystem.Forced);

            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "7");
            Assert.Equal(((ushort)7, (byte)255), AmbienceSystem.Forced);

            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "volcano");
            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "night", "2");
            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "night", "-0.1");
            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "night", "half");
            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "70000");
            Run(AmbienceCommands.AmbienceForce_OnCommand, staff);
            Assert.Equal(((ushort)7, (byte)255), AmbienceSystem.Forced);

            Run(AmbienceCommands.AmbienceForce_OnCommand, staff, "AUTO");
            Assert.Null(AmbienceSystem.Forced);
        }
        finally
        {
            AmbienceSystem.ClearForce();
            AmbienceZones.SetCurrent(previous);
            staff.Delete();
        }
    }

    [Fact]
    public void ReloadCommand_ListsTheErrors_AndKeepsTheZonesInUse()
    {
        var previousPath = AmbienceZones.FilePath;
        var previousSet = AmbienceZones.Current;
        var path = Path.Combine(Path.GetTempPath(), $"ambience-zones-{Guid.NewGuid():N}.json");

        var ns = PacketTestUtilities.CreateTestNetState();
        ns.Account = new MockAccount();
        var m = new PlayerMobile(World.NewMobile);
        m.DefaultMobileInit();
        m.AccessLevel = AccessLevel.GameMaster;
        ns.Mobile = m;
        m.NetState = ns;
        m.MoveToWorld(new Point3D(2000, 2000, 0), Map.Trammel);

        try
        {
            var inUse = AmbienceZoneFileTests.ParseZones(
                AmbienceZoneFileTests.MakeZone("a", 0, 0, 1, 1, 9, 9, """{ "preset": "night" }""")
            );
            AmbienceZones.SetCurrent(inUse);
            AmbienceZones.FilePath = path;

            var zones = new List<string>();

            for (var i = 0; i < 12; i++)
            {
                zones.Add(
                    $$"""
                      { "id": "z{{i}}", "maps": ["Atlantis"], "rects": [{ "x1": 1, "y1": 1, "x2": 9, "y2": 9 }],
                        "rules": [{ "preset": "p" }] }
                      """
                );
            }

            File.WriteAllText(
                path,
                $$"""{ "version": 1, "presetTable": 1, "presets": { "p": 1 }, "zones": [{{string.Join(",", zones)}}] }"""
            );

            var mark = ns.SendBuffer.GetReadSpan().Length;
            Run(AmbienceCommands.AmbienceReload_OnCommand, m);
            var sent = ns.SendBuffer.GetReadSpan()[mark..].ToArray();

            Assert.Same(inUse, AmbienceZones.Current);
            Assert.True(Contains(sent, "rejected with 12 errors"));
            Assert.True(Contains(sent, "zones[9] 'z9': unknown map 'Atlantis'"));
            Assert.False(Contains(sent, "zones[10] 'z10'"));
            Assert.True(Contains(sent, "and 2 more"));
        }
        finally
        {
            AmbienceZones.FilePath = previousPath;
            AmbienceZones.SetCurrent(previousSet);
            File.Delete(path);
            ns.Mobile = null;
            ns.Dispose();
            m.Delete();
        }
    }

    // System messages go out as ASCII or as big-endian UTF-16, depending on the packet the server picks.
    private static bool Contains(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0 ||
        data.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(text)) >= 0;
}
