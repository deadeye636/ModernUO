/*************************************************************************
 * ModernUO                                                              *
 * Copyright 2019-2026 - ModernUO Development Team                       *
 * Email: hi@modernuo.com                                                *
 * File: GoalBasedWanderLogic.cs                                         *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program. If not, see <http://www.gnu.org/licenses/>.  *
 ************************************************************************/

using System;
using Server.Engines.Pathing.Cache;
using Server.Engines.Spawners;
using Server.Factions;
using Server.Items;

namespace Server.Mobiles;

public abstract partial class BaseAI
{
    private const int WanderMinDistance = 3;
    private const int WanderMaxDistance = 8;
    private const int WanderTargetAttempts = 16;
    private const int WanderMaxBlockedSteps = 3;
    private const int WanderMaxSteps = 24;
    private const int WanderMinRestMilliseconds = 2_000;
    private const int WanderMaxRestMilliseconds = 6_000;
    private const Direction NoWanderDirection = (Direction)0xFF;

    private enum WanderStepResult
    {
        Blocked,
        Turned,
        Moved
    }

    private Point3D _wanderTarget;
    private Region _wanderRegion;
    private BaseMulti _wanderMulti;
    private long _wanderRestUntil;
    private bool _wanderRequiresRoof;
    private int _wanderBlockedSteps;
    private int _wanderStepsRemaining;
    private Direction _wanderAvoidDirection = NoWanderDirection;
    private bool _hasWanderTarget;

    private bool UsesGoalBasedWandering =>
        NPCSpeeds.GoalBasedWanderingEnabled && !Mobile.Controlled &&
        this is not VendorAI && Mobile is not BaseFactionGuard;

    private bool TryGoalBasedWander()
    {
        if (!UsesGoalBasedWandering)
        {
            if (_hasWanderTarget)
            {
                ClearWanderGoal();
            }

            return false;
        }

        ContinueGoalBasedWander();
        return true;
    }

    private void ContinueGoalBasedWander()
    {
        if (!_hasWanderTarget)
        {
            if (Core.TickCount - _wanderRestUntil < 0 || !TrySelectWanderTarget())
            {
                return;
            }
        }

        if (Mobile.X == _wanderTarget.X && Mobile.Y == _wanderTarget.Y)
        {
            FinishWanderGoal();
            return;
        }

        var result = TryWanderStep();
        if (result == WanderStepResult.Blocked)
        {
            if (++_wanderBlockedSteps >= WanderMaxBlockedSteps)
            {
                FinishWanderGoal();
            }

            return;
        }

        _wanderBlockedSteps = 0;
        if (result == WanderStepResult.Turned)
        {
            return;
        }

        if (--_wanderStepsRemaining <= 0 ||
            Mobile.X == _wanderTarget.X && Mobile.Y == _wanderTarget.Y)
        {
            FinishWanderGoal();
        }
    }

    private bool TrySelectWanderTarget()
    {
        var map = Mobile.Map;
        if (map == null || map == Map.Internal)
        {
            return false;
        }

        var anchor = Mobile.Home == Point3D.Zero ? Mobile.Location : Mobile.Home;
        _wanderRegion = Mobile.Spawner is RegionSpawner rs ? rs.SpawnRegion : Region.Find(anchor, map);
        _wanderMulti = FindWanderMulti(map, anchor);
        _wanderRequiresRoof = _wanderMulti == null && IsUnderStaticRoof(map, anchor);

        for (var i = 0; i < WanderTargetAttempts; i++)
        {
            var dx = Utility.RandomMinMax(-WanderMaxDistance, WanderMaxDistance);
            var dy = Utility.RandomMinMax(-WanderMaxDistance, WanderMaxDistance);
            var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

            if (distance < WanderMinDistance)
            {
                continue;
            }

            var x = Mobile.X + dx;
            var y = Mobile.Y + dy;
            if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
            {
                continue;
            }

            var target = new Point3D(x, y, map.GetAverageZ(x, y));
            if (!IsValidWanderLocation(target, anchor))
            {
                continue;
            }

            _wanderTarget = target;
            _hasWanderTarget = true;
            _wanderBlockedSteps = 0;
            _wanderStepsRemaining = WanderMaxSteps;
            _wanderAvoidDirection = NoWanderDirection;
            return true;
        }

        _wanderRegion = null;
        _wanderMulti = null;
        _wanderRequiresRoof = false;
        return false;
    }

    private WanderStepResult TryWanderStep()
    {
        var current = Mobile.Direction & Direction.Mask;
        var desired = _wanderAvoidDirection == NoWanderDirection
            ? Mobile.GetDirectionTo(_wanderTarget) & Direction.Mask
            : _wanderAvoidDirection;
        var turn = SignedDirectionDelta(current, desired);
        var turnSign = turn == 0 ? (Utility.RandomBool() ? 1 : -1) : Math.Sign(turn);
        var primary = RotateDirection(current, Math.Clamp(turn, -1, 1));

        if (TryDirectWanderDirection(primary) ||
            primary != current && TryDirectWanderDirection(current) ||
            TryDirectWanderDirection(RotateDirection(current, -turnSign)))
        {
            _wanderAvoidDirection = NoWanderDirection;
            return WanderStepResult.Moved;
        }

        if (!TryFindWanderEscape(current, out var escape))
        {
            return WanderStepResult.Blocked;
        }

        _wanderAvoidDirection = escape;
        Mobile.Direction = RotateDirection(current, Math.Sign(SignedDirectionDelta(current, escape)));
        return WanderStepResult.Turned;
    }

    private bool TryDirectWanderDirection(Direction direction)
    {
        if (!CanWanderDirection(direction))
        {
            return false;
        }

        var oldWalkRegion = Mobile.WalkRegion;
        Mobile.WalkRegion = _wanderRegion;

        try
        {
            return DoDirectWanderMove(direction);
        }
        finally
        {
            Mobile.WalkRegion = oldWalkRegion;
        }
    }

    private bool TryFindWanderEscape(Direction current, out Direction escape)
    {
        for (var distance = 2; distance <= 4; distance++)
        {
            var clockwise = RotateDirection(current, distance);
            if (CanWanderDirection(clockwise))
            {
                escape = clockwise;
                return true;
            }

            var counterclockwise = RotateDirection(current, -distance);
            if (counterclockwise != clockwise && CanWanderDirection(counterclockwise))
            {
                escape = counterclockwise;
                return true;
            }
        }

        escape = NoWanderDirection;
        return false;
    }

    private bool CanWanderDirection(Direction direction)
    {
        if (!Mobile.CheckMovement(direction, out var newZ))
        {
            return false;
        }

        var x = Mobile.X;
        var y = Mobile.Y;
        Movement.Movement.Offset(direction, ref x, ref y);
        var destination = new Point3D(x, y, newZ);
        var anchor = Mobile.Home == Point3D.Zero ? Mobile.Location : Mobile.Home;

        return IsValidWanderLocation(destination, anchor) && !IsDoorway(destination);
    }

    private bool IsValidWanderLocation(Point3D location, Point3D anchor)
    {
        if (Mobile.Home != Point3D.Zero &&
            (Math.Abs(location.X - anchor.X) > Mobile.RangeHome || Math.Abs(location.Y - anchor.Y) > Mobile.RangeHome))
        {
            return false;
        }

        var map = Mobile.Map;
        var region = Region.Find(location, map);
        if (_wanderRegion != null && !region.AcceptsSpawnsFrom(_wanderRegion))
        {
            return false;
        }

        return FindWanderMulti(map, location) == _wanderMulti &&
               (!_wanderRequiresRoof || IsUnderStaticRoof(map, location));
    }

    private bool IsDoorway(Point3D destination)
    {
        foreach (var door in Mobile.Map.GetItemsInRange<BaseDoor>(destination, 2))
        {
            var location = door.Location;
            if (door.Open)
            {
                location = new Point3D(
                    location.X - door.Offset.X,
                    location.Y - door.Offset.Y,
                    location.Z - door.Offset.Z
                );
            }

            if (location.X == destination.X && location.Y == destination.Y &&
                location.Z + 20 > Mobile.Z && Mobile.Z + 16 > location.Z)
            {
                return true;
            }
        }

        return false;
    }

    private static BaseMulti FindWanderMulti(Map map, Point3D location) =>
        MultiMaskCache.TryResolveCoveringMulti(map, location.X, location.Y, out var multi, out _, out _) ? multi : null;

    private static bool IsUnderStaticRoof(Map map, Point3D location)
    {
        foreach (var tile in map.Tiles.GetStaticTiles(location.X, location.Y))
        {
            var itemData = TileData.ItemTable[tile.ID & TileData.MaxItemValue];
            if (itemData.Roof && tile.Z > location.Z && tile.Z <= location.Z + 64)
            {
                return true;
            }
        }

        return false;
    }

    private static int SignedDirectionDelta(Direction from, Direction to)
    {
        var delta = (int)to - (int)from;
        return delta > 4 ? delta - 8 : delta < -4 ? delta + 8 : delta;
    }

    private static Direction RotateDirection(Direction direction, int delta) =>
        (Direction)(((int)direction + delta + 8) & 0x7);

    private void FinishWanderGoal()
    {
        _hasWanderTarget = false;
        _wanderBlockedSteps = 0;
        _wanderStepsRemaining = 0;
        _wanderAvoidDirection = NoWanderDirection;
        _wanderRegion = null;
        _wanderMulti = null;
        _wanderRequiresRoof = false;
        _wanderRestUntil = Core.TickCount + Utility.RandomMinMax(
            WanderMinRestMilliseconds,
            WanderMaxRestMilliseconds
        );
    }

    private void ClearWanderGoal()
    {
        _hasWanderTarget = false;
        _wanderBlockedSteps = 0;
        _wanderStepsRemaining = 0;
        _wanderAvoidDirection = NoWanderDirection;
        _wanderRegion = null;
        _wanderMulti = null;
        _wanderRequiresRoof = false;
        _wanderRestUntil = Core.TickCount;
    }

    private bool TryGetWanderWake(out long nextMove)
    {
        nextMove = NextMove;

        if (!UsesGoalBasedWandering)
        {
            if (_hasWanderTarget)
            {
                ClearWanderGoal();
            }

            return false;
        }

        return _hasWanderTarget;
    }

    internal void SetWanderTargetForTesting(Point3D target)
    {
        var anchor = Mobile.Home == Point3D.Zero ? Mobile.Location : Mobile.Home;
        _wanderRegion = Region.Find(anchor, Mobile.Map);
        _wanderMulti = FindWanderMulti(Mobile.Map, anchor);
        _wanderRequiresRoof = _wanderMulti == null && IsUnderStaticRoof(Mobile.Map, anchor);
        _wanderTarget = target;
        _hasWanderTarget = true;
        _wanderBlockedSteps = 0;
        _wanderStepsRemaining = WanderMaxSteps;
        _wanderAvoidDirection = NoWanderDirection;
    }

    internal void ContinueGoalBasedWanderForTesting() => ContinueGoalBasedWander();

    internal bool HasWanderTargetForTesting => _hasWanderTarget;
}
