using System;
using ModernUO.Serialization;
using Server.Items;

namespace Server.Mobiles;

// Named undead of the Old Haven ruins (Trammel, from Mondain's Legacy on): a skeletal knight that
// spawns among the zombies, skeletons and spellbinders there.
[SerializationGenerator(0)]
public partial class Drelgor : BaseCreature
{
    // Who dares to defile Haven? I am Drelgor the Impaler! I shall claim your souls as payment for this intrusion!
    public const int ChallengeCliloc = 1077840;
    public const int ChallengeSound = 0x14;

    // Shortest time between two challenges, so a long fight does not repeat the line on every target switch.
    public static readonly TimeSpan ChallengeDelay = TimeSpan.FromMinutes(1.0);

    private long _nextChallenge = Core.TickCount;

    [Constructible]
    public Drelgor() : base(AIType.AI_Melee)
    {
        Body = 147;
        BaseSoundID = 451;

        SetStr(127, 137);
        SetDex(82, 94);
        SetInt(48, 55);

        SetHits(131, 136);

        SetDamage(6, 8);

        SetDamageType(ResistanceType.Physical, 40);
        SetDamageType(ResistanceType.Cold, 60);

        SetResistance(ResistanceType.Physical, 35, 45);
        SetResistance(ResistanceType.Fire, 20, 30);
        SetResistance(ResistanceType.Cold, 50, 60);
        SetResistance(ResistanceType.Poison, 20, 30);
        SetResistance(ResistanceType.Energy, 30, 40);

        SetSkill(SkillName.MagicResist, 60.0);
        SetSkill(SkillName.Tactics, 60.0);
        SetSkill(SkillName.Wrestling, 60.0);

        Fame = 3600;
        Karma = -3600;

        VirtualArmor = 40;

        PackItem(new Scimitar());
        PackItem(new WoodenShield());

        PackItem(
            Utility.Random(5) switch
            {
                0 => new BoneArms(),
                1 => new BoneChest(),
                2 => new BoneGloves(),
                3 => new BoneLegs(),
                _ => new BoneHelm()
            }
        );
    }

    public override string CorpseName => "a Drelgor the Impaler corpse";
    public override string DefaultName => "Drelgor the Impaler";

    public override bool BleedImmune => true;

    public override OppositionGroup OppositionGroup => OppositionGroup.FeyAndUndead;

    public override void GenerateLoot()
    {
        AddLoot(LootPack.Average);
        AddLoot(LootPack.Meager);
    }

    public override void OnCombatantChange()
    {
        base.OnCombatantChange();

        if (Combatant?.Deleted == false && Combatant.Alive)
        {
            TryChallenge();
        }
    }

    // Says the challenge overhead and plays its sound, at most once per ChallengeDelay.
    public bool TryChallenge()
    {
        if (Deleted || Map == null || Map == Map.Internal || Core.TickCount - _nextChallenge < 0)
        {
            return false;
        }

        _nextChallenge = Core.TickCount + (long)ChallengeDelay.TotalMilliseconds;

        Say(ChallengeCliloc);
        PlaySound(ChallengeSound);
        return true;
    }
}
