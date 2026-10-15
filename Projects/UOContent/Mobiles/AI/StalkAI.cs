namespace Server.Mobiles;

// A wild animal that hunts by stealth: it creeps up on nearby prey one slow, deliberate step
// at a time, then springs into an ordinary melee pounce once it is close enough. Prey is any
// creature the hunter's OppositionGroup marks as an enemy (e.g. a cat against birds).
//
// Only wild hunters stalk. A tamed one is routed to Obey() by the AITimer and never reaches
// Think()/DoActionWander, so it behaves exactly like an ordinary pet; the Controlled guard
// below makes that explicit.
public class StalkAI : AnimalAI
{
    // The creep is paced slower than the normal passive wander step so the approach reads as a
    // deliberate stalk rather than a walk. Between steps the hunter holds still and watches.
    private const int MinStalkStepDelay = 700;
    private const int MaxStalkStepDelay = 1100;

    // How close the hunter creeps before it springs. Chosen a little wider than the prey's own
    // predator-alarm range (see SkittishAI) so the pounce usually begins before the prey bolts;
    // the chase itself runs at the faster combat speed. In-game tuning value.
    private const int PounceRange = 5;

    private long _nextStalkStep;

    public StalkAI(BaseCreature m) : base(m)
    {
    }

    public override bool DoActionWander()
    {
        if (Mobile.Controlled || Mobile.Combatant != null)
        {
            return base.DoActionWander();
        }

        var prey = FindPrey();

        if (prey == null)
        {
            return base.DoActionWander();
        }

        if (Mobile.InRange(prey, PounceRange))
        {
            this.DebugSayFormatted($"Pouncing on {prey.Name}!");

            Mobile.Combatant = prey;
            Action = ActionType.Combat;
            return true;
        }

        StalkStep(prey);
        return true;
    }

    // One deliberate step toward the prey, self-throttled below the wander pace. On the ticks in
    // between it only turns to face the prey, which keeps the creep visibly slow regardless of
    // how often the AI thinks.
    private void StalkStep(Mobile prey)
    {
        Mobile.Direction = Mobile.GetDirectionTo(prey);

        if (Core.TickCount - _nextStalkStep < 0)
        {
            return;
        }

        DoMove(Mobile.Direction);
        _nextStalkStep = Core.TickCount + Utility.RandomMinMax(MinStalkStepDelay, MaxStalkStepDelay);
    }

    // Nearest wild, living opposition enemy in range and line of sight. Tamed and summoned
    // creatures are left alone so a hunter never preys on someone's pet.
    private Mobile FindPrey()
    {
        var opposition = Mobile.OppositionGroup;
        var map = Mobile.Map;

        if (opposition == null || map == null)
        {
            return null;
        }

        Mobile nearest = null;
        var nearestDist = int.MaxValue;

        foreach (var m in map.GetMobilesInRange(Mobile.Location, Mobile.RangePerception))
        {
            if (m == Mobile || m.Deleted || !m.Alive || m is not BaseCreature prey)
            {
                continue;
            }

            if (prey.Controlled || prey.Summoned || !opposition.IsEnemy(Mobile, prey) ||
                !Mobile.CanSee(prey) || !Mobile.InLOS(prey))
            {
                continue;
            }

            var dist = (int)Mobile.GetDistanceToSqrt(prey);

            if (dist < nearestDist)
            {
                nearest = prey;
                nearestDist = dist;
            }
        }

        return nearest;
    }
}
