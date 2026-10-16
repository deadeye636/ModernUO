namespace Server.Mobiles;

// A wild animal that hunts by stealth: it creeps up on nearby prey one slow, deliberate step at
// a time, breaks into a sprint for the last few tiles, and kills its prey with a single pounce
// the moment it reaches it. Prey is any creature the hunter's OppositionGroup marks as an enemy
// (e.g. a cat against birds).
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

    // How close the hunter creeps before it breaks into a sprint. Chosen a little wider than the
    // prey's own predator-alarm range (see SkittishAI) so the sprint usually begins before the
    // prey bolts. The sprint itself runs at the faster active pace. In-game tuning value.
    private const int PounceRange = 5;

    private long _nextStalkStep;

    public StalkAI(BaseCreature m) : base(m)
    {
    }

    public override bool DoActionWander()
    {
        // A real combatant (e.g. a player who attacked) owns War mode and the pace; leave it be.
        if (Mobile.Controlled || Mobile.Combatant != null)
        {
            return base.DoActionWander();
        }

        var prey = FindPrey();

        if (prey == null)
        {
            EndSprint();
            return base.DoActionWander();
        }

        // Reached the prey: one lethal pounce and it is over. Killing outright keeps a stalk that
        // connects decisive - the prey's regen can never outlast a slow melee - and one blow
        // instead of many leaves no pool of blood (#12).
        if (Mobile.InRange(prey, 1))
        {
            Pounce(prey);
            return true;
        }

        // Within springing distance but not yet adjacent: drop the slow creep and sprint.
        if (Mobile.InRange(prey, PounceRange))
        {
            Sprint(prey);
            return true;
        }

        // Still far: creep up one deliberate, slow step at a time.
        EndSprint();
        StalkStep(prey);
        return true;
    }

    // The killing pounce: a single blow that drops the prey at once. Kill() takes it straight to
    // death without the damage path's blood splatter, so a connecting stalk is "one pounce, not a
    // brawl" and the prey's regen is irrelevant (#12).
    private void Pounce(Mobile prey)
    {
        this.DebugSayFormatted($"Pouncing on {prey.Name}!");

        EndSprint();
        Mobile.Direction = Mobile.GetDirectionTo(prey);
        prey.Kill();
    }

    // The sprint across the last few tiles. War mode is the engine's switch for the engaged
    // (active) pace: it holds CurrentSpeed active across steps - DoMove would otherwise drop it
    // back to passive - so both the think cadence (AITimer) and the move budget run on the active
    // clock, faster than the prey's passive flee. No combatant is set, so the hunter closes in
    // without trading blows; the kill is the single Pounce once it reaches the prey.
    private void Sprint(Mobile prey)
    {
        if (!Mobile.Warmode)
        {
            Mobile.Warmode = true;
        }

        Mobile.Direction = Mobile.GetDirectionTo(prey);
        DoMove(Mobile.Direction);
    }

    // Leaves the sprint and returns to the calm passive pace, so the hunter never wanders the world
    // in a permanent war stance. War mode set by real combat is left to the combatant branch.
    private void EndSprint()
    {
        if (Mobile.Warmode)
        {
            Mobile.Warmode = false;
        }
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
