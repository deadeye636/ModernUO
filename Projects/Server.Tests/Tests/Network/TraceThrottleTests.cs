/*************************************************************************
 * ModernUO                                                              *
 * Copyright 2019-2026 - ModernUO Development Team                       *
 * File: TraceThrottleTests.cs                                           *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 *************************************************************************/

using Server.Network;
using Xunit;

namespace Server.Tests.Network;

[Collection("Sequential Server Tests")]
public class TraceThrottleTests
{
    [Fact]
    public void ASessionIsTracedUpToItsLimitThenSkipped()
    {
        TraceThrottle.Reset();
        var count = 0;

        for (var i = 1; i < TraceThrottle.MaxPerSession; i++)
        {
            Assert.Equal(TraceDecision.Write, TraceThrottle.Next(ref count, i * 1000L));
        }

        Assert.Equal(TraceDecision.WriteAndNoteLimit, TraceThrottle.Next(ref count, 100_000));
        Assert.Equal(TraceDecision.Skip, TraceThrottle.Next(ref count, 200_000));
        Assert.Equal(TraceThrottle.MaxPerSession, count);
    }

    [Fact]
    public void AllSessionsShareThePerSecondLimit()
    {
        TraceThrottle.Reset();
        var written = 0;

        for (var session = 0; session < TraceThrottle.MaxPerSecond * 2; session++)
        {
            var count = 0;

            if (TraceThrottle.Next(ref count, 5_000) != TraceDecision.Skip)
            {
                written++;
            }
        }

        Assert.Equal(TraceThrottle.MaxPerSecond, written);

        var next = 0;
        Assert.Equal(TraceDecision.Write, TraceThrottle.Next(ref next, 6_000));
    }

    [Fact]
    public void ALargeStartingTickCountOpensTheFirstWindow()
    {
        TraceThrottle.Reset();
        var count = 0;

        Assert.Equal(TraceDecision.Write, TraceThrottle.Next(ref count, long.MaxValue - 10));
    }

    [Fact]
    public void ExceptionsHaveTheirOwnPerSecondLimit()
    {
        TraceThrottle.Reset();
        var written = 0;

        for (var i = 0; i < TraceThrottle.MaxPerSecond * 2; i++)
        {
            if (TraceThrottle.NextException(9_000, out _))
            {
                written++;
            }
        }

        Assert.Equal(TraceThrottle.MaxPerSecond, written);

        var count = 0;
        Assert.Equal(TraceDecision.Write, TraceThrottle.Next(ref count, 9_000));
        Assert.True(TraceThrottle.NextException(10_000, out var suppressed));
        Assert.Equal(TraceThrottle.MaxPerSecond, suppressed);
    }
}
