using System;
using Server.Network;
using Xunit;

namespace Server.Tests.Network;

public class NetStateInactivityTimeoutTests
{
    [Theory]
    [InlineData(30_000, 30_000)]      // upstream default; untouched
    [InlineData(90_000, 90_000)]      // longer keep-alive intervals; untouched
    [InlineData(1_000, 5_000)]        // below one alive-check interval; raised
    [InlineData(0, 5_000)]            // zero; raised
    [InlineData(-10_000, 5_000)]      // negative; raised
    [InlineData(7_200_000, 3_600_000)] // above the ceiling; capped
    public void CoerceInactivityTimeout_ClampsToTheCheckedRange(long configuredMs, long expectedMs) =>
        Assert.Equal(expectedMs, NetState.CoerceInactivityTimeout(TimeSpan.FromMilliseconds(configuredMs)));

    [Fact]
    public void CoerceInactivityTimeout_MaxValueIsCappedWithoutOverflow() =>
        Assert.Equal(3_600_000L, NetState.CoerceInactivityTimeout(TimeSpan.MaxValue));
}
