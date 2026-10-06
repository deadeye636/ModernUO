using System;
using System.Collections.Generic;
using System.IO;
using Server;
using Server.Commands;
using Server.Engines.Spawners;
using Server.Items;
using Xunit;

namespace UOContent.Tests.Commands;

[Collection("Sequential UOContent Tests")]
public class PreMLHavenTests
{
    // Old Haven ("Haven" region in regions.json, MaxExpansion SE).
    private static readonly Rectangle2D[] _oldHaven =
    [
        new(3590, 2460, 118, 225),
        new(3568, 2552, 22, 79),
        new(3708, 2558, 53, 154),
        new(3695, 2685, 13, 27)
    ];

    [Theory]
    [InlineData(Expansion.SE, true, false)]
    [InlineData(Expansion.ML, false, true)]
    [InlineData(Expansion.EJ, false, true)]
    public void DecorationSets_GatePreMLHavenAndNewHaven(Expansion expansion, bool preML, bool custom)
    {
        var previous = Core.Expansion;

        try
        {
            Core.Expansion = expansion;
            var folders = new List<string>();

            foreach (var (folder, _) in Decorate.GetDecorationSets())
            {
                folders.Add(folder);
            }

            Assert.Contains("Data/Decoration/Trammel", folders);
            Assert.Equal(preML, folders.Contains(Decorate.PreMLTrammelFolder));
            Assert.Equal(custom, folders.Contains(Decorate.CustomTrammelFolder));
        }
        finally
        {
            Core.Expansion = previous;
        }
    }

    [SkippableFact]
    public void SharedTrammelDecoration_HasNothingInOldHaven()
    {
        var folder = Path.Combine(Core.BaseDirectory, "Data", "Decoration", "Trammel");
        Skip.IfNot(Directory.Exists(folder), "distribution data not present");

        foreach (var file in Directory.GetFiles(folder, "*.cfg"))
        {
            foreach (var list in DecorationList.ReadAll(file))
            {
                foreach (var entry in list.Entries)
                {
                    var loc = entry.Location;

                    foreach (var area in _oldHaven)
                    {
                        Assert.False(area.Contains(loc), $"{Path.GetFileName(file)} places {loc} in the pre-ML Haven");
                    }
                }
            }
        }
    }

    [SkippableTheory]
    [InlineData(Decorate.PreMLTrammelFolder)]
    [InlineData(Decorate.CustomTrammelFolder)]
    public void GatedDecorationFiles_ParseAndConstruct(string folder)
    {
        var path = Path.Combine(Core.BaseDirectory, folder);
        Skip.IfNot(Directory.Exists(path), "distribution data not present");

        var files = Directory.GetFiles(path, "*.cfg");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var found = new List<Item>();

            // FindGenerated constructs each list's item, so an unknown type or bad parameter throws here.
            foreach (var list in DecorationList.ReadAll(file))
            {
                Assert.NotEmpty(list.Entries);
                list.FindGenerated(Map.Felucca, found);
            }
        }
    }

    [Fact]
    public void IsGenerated_MatchesTypeGraphicAndExactLocation()
    {
        var loc = new Point3D(1200, 1200, 10);
        var template = new Static(0x0B9E);
        var placed = new Static(0x0B9E);
        var otherZ = new Static(0x0B9E);
        var otherGraphic = new Static(0x0B9F);
        var otherType = new LocalizedStatic(0x0B9E, 1020000);

        try
        {
            placed.MoveToWorld(loc, Map.Felucca);
            otherZ.MoveToWorld(new Point3D(loc.X, loc.Y, loc.Z + 1), Map.Felucca);
            otherGraphic.MoveToWorld(loc, Map.Felucca);
            otherType.MoveToWorld(loc, Map.Felucca);

            Assert.True(DecorationList.IsGenerated(template, placed, loc));
            Assert.False(DecorationList.IsGenerated(template, otherZ, loc));
            Assert.False(DecorationList.IsGenerated(template, otherGraphic, loc));
            Assert.False(DecorationList.IsGenerated(template, otherType, loc));
            Assert.False(DecorationList.IsGenerated(template, template, loc));
        }
        finally
        {
            template.Delete();
            placed.Delete();
            otherZ.Delete();
            otherGraphic.Delete();
            otherType.Delete();
        }
    }

    [Fact]
    public void IsGenerated_ComparesDoorsClosed()
    {
        var loc = new Point3D(1210, 1200, 0);
        var template = new MetalDoor(DoorFacing.WestCW);
        var door = new MetalDoor(DoorFacing.WestCW);
        var otherFacing = new MetalDoor(DoorFacing.SouthCW);

        try
        {
            door.MoveToWorld(loc, Map.Felucca);
            otherFacing.MoveToWorld(loc, Map.Felucca);
            door.Open = true;

            Assert.NotEqual(loc, door.Location);
            Assert.True(DecorationList.IsGenerated(template, door, loc));
            Assert.False(DecorationList.IsGenerated(template, otherFacing, loc));
        }
        finally
        {
            template.Delete();
            door.Delete();
            otherFacing.Delete();
        }
    }

    [Fact]
    public void IsGenerated_ComparesSpawnedNames()
    {
        var loc = new Point3D(1220, 1200, 0);
        var template = new Spawner("MansionGuard");
        var same = new Spawner("mansionguard");
        var other = new Spawner("Healer");

        try
        {
            same.MoveToWorld(loc, Map.Felucca);
            other.MoveToWorld(loc, Map.Felucca);

            Assert.True(DecorationList.IsGenerated(template, same, loc));
            Assert.False(DecorationList.IsGenerated(template, other, loc));
        }
        finally
        {
            template.Delete();
            same.Delete();
            other.Delete();
        }
    }

    [Fact]
    public void FindGenerated_FindsWithoutDeleting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muo-preml-haven-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "test.cfg");
        File.WriteAllText(cfg, "# test\nStatic 0x0B9E\n1230 1200 5\n1231 1200 5\n");

        var hit = new Static(0x0B9E);
        var lightMismatch = new Static(0x0B9E) { Light = LightType.Circle300 };
        var miss = new Static(0x0B9E);

        try
        {
            hit.MoveToWorld(new Point3D(1230, 1200, 5), Map.Felucca);
            lightMismatch.MoveToWorld(new Point3D(1231, 1200, 5), Map.Felucca);
            miss.MoveToWorld(new Point3D(1232, 1200, 5), Map.Felucca);

            var found = new List<Item>();
            foreach (var list in DecorationList.ReadAll(cfg))
            {
                list.FindGenerated(Map.Felucca, found);
                list.FindGenerated(Map.Felucca, found);
            }

            Assert.Equal(2, found.Count);
            Assert.Contains(hit, found);
            Assert.Contains(lightMismatch, found);
            Assert.False(hit.Deleted);
            Assert.False(lightMismatch.Deleted);
            Assert.False(miss.Deleted);
        }
        finally
        {
            hit.Delete();
            lightMismatch.Delete();
            miss.Delete();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindPreMLDecoration_KeepsMovableItems()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muo-preml-player-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "test.cfg"), "# test\nStatic 0x0B9E\n1250 1200 5\n1251 1200 5\n");

        var generated = new Static(0x0B9E);
        var player = new Static(0x0B9E) { Movable = true };

        try
        {
            generated.MoveToWorld(new Point3D(1250, 1200, 5), Map.Felucca);
            player.MoveToWorld(new Point3D(1251, 1200, 5), Map.Felucca);

            var report = new CleanPreMLHaven.Report();
            CleanPreMLHaven.FindPreMLDecoration(Map.Felucca, dir, report);

            Assert.Same(generated, Assert.Single(report.Decoration));
            Assert.Same(player, Assert.Single(report.KeptPlayer));
        }
        finally
        {
            generated.Delete();
            player.Delete();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindPreMLSigns_MatchesSignsOnlyOnTheirFacet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muo-preml-signs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "signs-preml.cfg");
        File.WriteAllText(file, "2 2979 1240 1200 0 The Test Bakery\n");

        var sign = new Sign(2979) { Name = "The Test Bakery" };
        var staticSign = new Static(2979);
        var feluccaSign = new Sign(2979);

        try
        {
            sign.MoveToWorld(new Point3D(1240, 1200, 0), Map.Trammel);
            staticSign.MoveToWorld(new Point3D(1240, 1200, 0), Map.Trammel);
            feluccaSign.MoveToWorld(new Point3D(1240, 1200, 0), Map.Felucca);

            var trammel = new CleanPreMLHaven.Report();
            CleanPreMLHaven.FindPreMLSigns(Map.Trammel, file, trammel);
            var felucca = new CleanPreMLHaven.Report();
            CleanPreMLHaven.FindPreMLSigns(Map.Felucca, file, felucca);

            Assert.Same(sign, Assert.Single(trammel.Signs));
            Assert.Empty(felucca.Signs);
        }
        finally
        {
            sign.Delete();
            staticSign.Delete();
            feluccaSign.Delete();
            Directory.Delete(dir, true);
        }
    }

    [SkippableFact]
    public void Run_Apply_IsIdempotent()
    {
        Skip.IfNot(
            File.Exists(Path.Combine(Core.BaseDirectory, "Data", "Spawns", "post-uoml", "trammel", "TownsPeople.json")),
            "distribution data not present"
        );

        var placed = new List<BaseSpawner>();

        try
        {
            var first = CleanPreMLHaven.Run(Map.Trammel, true);
            Assert.Empty(first.Errors);
            Assert.Equal(14, first.PlacedSpawners.Count);

            var second = CleanPreMLHaven.Run(Map.Trammel, true);
            Assert.Empty(second.Errors);
            Assert.Empty(second.PlacedSpawners);
            Assert.Empty(second.Decoration);
            Assert.Empty(second.Signs);
            Assert.Equal(14, second.SpawnersInPlace);

            foreach (var (dto, _) in first.PlacedSpawners)
            {
                foreach (var spawner in Map.Trammel.GetItemsAt<BaseSpawner>(dto.Location))
                {
                    if (spawner.Guid == dto.Guid)
                    {
                        placed.Add(spawner);
                    }
                }
            }

            Assert.Equal(14, placed.Count);
        }
        finally
        {
            foreach (var spawner in placed)
            {
                spawner.Delete();
            }
        }
    }
}
