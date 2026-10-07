using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Misc;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests.Mobiles;

[Collection("Sequential UOContent Tests")]
public class DrelgorTests : IDisposable
{
    private readonly List<Mobile> _created = [];

    public void Dispose()
    {
        for (var i = 0; i < _created.Count; i++)
        {
            _created[i].Delete();
        }
    }

    private Drelgor NewDrelgor()
    {
        var d = new Drelgor();
        _created.Add(d);
        return d;
    }

    [Fact]
    public void Construct_IsTheNamedSkeletalUndead()
    {
        var d = NewDrelgor();

        Assert.Equal("Drelgor the Impaler", d.Name);
        Assert.Equal(147, d.Body.BodyID);
        Assert.True(d.BleedImmune);
        Assert.Same(OppositionGroup.FeyAndUndead, d.OppositionGroup);
        Assert.InRange(d.HitsMax, 131, 136);
        Assert.Equal(40, d.PhysicalDamage);
        Assert.Equal(60, d.ColdDamage);
        Assert.Equal(-3600, d.Karma);
    }

    [Fact]
    public void Construct_PacksSwordShieldAndOneBonePiece()
    {
        var d = NewDrelgor();
        var pack = d.Backpack;

        Assert.NotNull(pack);
        Assert.NotNull(pack.FindItemByType<Scimitar>());
        Assert.NotNull(pack.FindItemByType<WoodenShield>());

        var bone = 0;
        foreach (var item in pack.Items)
        {
            if (item is BoneArms or BoneChest or BoneGloves or BoneLegs or BoneHelm)
            {
                bone++;
            }
        }

        Assert.Equal(1, bone);
    }

    [Fact]
    public void TryChallenge_SpeaksOnceThenWaitsForTheDelay()
    {
        var d = NewDrelgor();
        d.MoveToWorld(new Point3D(3651, 2509, 0), Map.Trammel);
        d.AIObject?.AITimer.Stop();

        Assert.True(d.TryChallenge());
        Assert.False(d.TryChallenge());
    }

    [Fact]
    public void TryChallenge_StaysSilentOffTheMap()
    {
        var d = NewDrelgor();

        Assert.False(d.TryChallenge());
    }

    [Fact]
    public void SuitPieces_CarryTheUOGuideValues()
    {
        BaseArmor[] pieces = [new DrelgorBoneArms(), new DrelgorBoneTunic(), new DrelgorBoneGloves(), new DrelgorBoneLegs(), new DrelgorBoneHelm()];
        try
        {
            int phys = 0, fire = 0, cold = 0, poison = 0, energy = 0;
            foreach (var piece in pieces)
            {
                Assert.Equal(0, piece.Hue);
                Assert.Null(piece.Name);
                Assert.InRange(piece.MaxHitPoints, 25, 30);
                phys += piece.PhysicalResistance;
                fire += piece.FireResistance;
                cold += piece.ColdResistance;
                poison += piece.PoisonResistance;
                energy += piece.EnergyResistance;
            }

            Assert.Equal((40, 40, 45, 35, 45), (phys, fire, cold, poison, energy));

            Assert.Equal(3, pieces[0].Attributes.BonusDex);
            Assert.Equal(1, pieces[1].Attributes.RegenHits);
            Assert.Equal(3, pieces[2].Attributes.BonusStr);
            Assert.Equal(1, pieces[3].Attributes.RegenMana);
            Assert.Equal(3, pieces[4].Attributes.BonusInt);
            Assert.Equal(1, pieces[4].Attributes.NightSight);

            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(70, pieces[i].ArmorAttributes.LowerStatReq);
            }

            Assert.Equal(0, pieces[4].ArmorAttributes.LowerStatReq);
            Assert.IsAssignableFrom<BoneArms>(pieces[0]);
            Assert.IsAssignableFrom<BoneChest>(pieces[1]);
            Assert.IsAssignableFrom<BoneGloves>(pieces[2]);
            Assert.IsAssignableFrom<BoneLegs>(pieces[3]);
            Assert.IsAssignableFrom<BoneHelm>(pieces[4]);
        }
        finally
        {
            foreach (var piece in pieces)
            {
                piece.Delete();
            }
        }
    }

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(0.0, 0)]
    public void Death_DropsASuitPieceByChance(double chance, int expected)
    {
        var old = Drelgor.SuitDropChance;
        var oldCorpseHandler = Mobile.CreateCorpseHandler;
        Drelgor.SuitDropChance = chance;
        Mobile.CreateCorpseHandler = Corpse.Mobile_CreateCorpseHandler;
        try
        {
            var d = NewDrelgor();
            d.MoveToWorld(new Point3D(3651, 2509, 0), Map.Trammel);
            d.AIObject?.AITimer.Stop();

            d.Kill();

            var corpse = d.Corpse;
            Assert.NotNull(corpse);
            var pieces = 0;
            foreach (var item in corpse.Items)
            {
                if (item is DrelgorBoneArms or DrelgorBoneTunic or DrelgorBoneGloves or DrelgorBoneLegs or DrelgorBoneHelm)
                {
                    pieces++;
                }
            }

            corpse.Delete();
            Assert.Equal(expected, pieces);
        }
        finally
        {
            Drelgor.SuitDropChance = old;
            Mobile.CreateCorpseHandler = oldCorpseHandler;
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    // The test map has no regions, so this pins the general young rule; Haven Island is a NoHousingRegion,
    // whose YoungProtected stays true, so the same rule holds there.
    public void Harm_SparesYoungPlayersOnly(bool young, bool harmful)
    {
        var d = NewDrelgor();
        d.MoveToWorld(new Point3D(3651, 2509, 0), Map.Trammel);
        d.AIObject?.AITimer.Stop();

        var player = new PlayerMobile { Young = young };
        _created.Add(player);
        player.MoveToWorld(new Point3D(3652, 2509, 0), Map.Trammel);

        Assert.Equal(young, player.Young);
        Assert.Equal(harmful, NotorietyHandlers.Mobile_AllowHarmful(d, player));
    }
}
