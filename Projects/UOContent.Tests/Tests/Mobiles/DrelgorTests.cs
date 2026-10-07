using System;
using System.Collections.Generic;
using Server;
using Server.Items;
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
}
