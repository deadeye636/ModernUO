namespace Server.Mobiles;

// A shy animal that keeps its distance. While wild it edges away from any approaching player
// and from any creature its OppositionGroup marks as an enemy (e.g. a bird fleeing a hunting
// cat). It still fights back and flees on low health like an ordinary animal if actually
// attacked; it simply will not let a threat walk up to it unprovoked.
//
// Only wild animals are skittish. A tamed one is routed to Obey() by the AITimer and never
// reaches Think()/DoActionWander, so it behaves exactly like an ordinary pet; the Controlled
// guard below makes that explicit.
public class SkittishAI : AnimalAI
{
    // A player is spotted a comfortable distance off, but a stalking predator is only noticed
    // up close so an ambush hunter (see StalkAI) still gets its chance. In-game tuning values.
    private const int PlayerAlarmRange = 9;
    private const int PredatorAlarmRange = 4;

    public SkittishAI(BaseCreature m) : base(m)
    {
    }

    public override bool DoActionWander()
    {
        if (!Mobile.Controlled && Mobile.Combatant == null && AcquireThreat())
        {
            this.DebugSayFormatted($"{Mobile.FocusMob.Name} is too close; keeping my distance.");

            Action = ActionType.Backoff;
            return true;
        }

        return base.DoActionWander();
    }

    public override bool DoActionBackoff()
    {
        if (Mobile.Combatant != null)
        {
            Action = ActionType.Combat;
            return true;
        }

        if (Mobile.Controlled || !AcquireThreat())
        {
            DebugSay("Safe distance; back to foraging.");
            Action = ActionType.Wander;
            return true;
        }

        // One step straight away from the nearest threat; the backoff ends on its own once the
        // threat drifts back out of alarm range.
        DoMove(Mobile.FocusMob.GetDirectionTo(Mobile));
        return true;
    }

    // Nearest threat in line of sight: a player within the player-alarm range, or an opposition
    // enemy within the shorter predator-alarm range. Sets FocusMob.
    private bool AcquireThreat()
    {
        var map = Mobile.Map;

        if (map == null)
        {
            Mobile.FocusMob = null;
            return false;
        }

        var opposition = Mobile.OppositionGroup;
        Mobile nearest = null;
        var nearestDist = int.MaxValue;

        foreach (var m in map.GetMobilesInRange(Mobile.Location, PlayerAlarmRange))
        {
            if (m == Mobile || m.Deleted || !m.Alive || m.AccessLevel > AccessLevel.Player || !Mobile.CanSee(m))
            {
                continue;
            }

            int effRange;

            if (m.Player)
            {
                effRange = PlayerAlarmRange;
            }
            else if (opposition?.IsEnemy(Mobile, m) == true)
            {
                effRange = PredatorAlarmRange;
            }
            else
            {
                continue;
            }

            var dist = (int)Mobile.GetDistanceToSqrt(m);

            if (dist > effRange || dist >= nearestDist || !Mobile.InLOS(m))
            {
                continue;
            }

            nearest = m;
            nearestDist = dist;
        }

        Mobile.FocusMob = nearest;
        return nearest != null;
    }
}
