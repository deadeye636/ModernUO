using ModernUO.Serialization;

namespace Server.Items;

// Piece of the unofficial "Drelgor the Impaler's Suit": plain bone armor by name and look, with the
// resists and the one extra property UOGuide lists for it. Dropped by Drelgor (Drelgor.SuitDropChance).
[SerializationGenerator(0)]
public partial class DrelgorBoneTunic : BoneChest
{
    [Constructible]
    public DrelgorBoneTunic()
    {
        Attributes.RegenHits = 1;
        ArmorAttributes.LowerStatReq = 70;
    }

    public override int BasePhysicalResistance => 8;
    public override int BaseFireResistance => 8;
    public override int BaseColdResistance => 9;
    public override int BasePoisonResistance => 7;
    public override int BaseEnergyResistance => 9;
}
