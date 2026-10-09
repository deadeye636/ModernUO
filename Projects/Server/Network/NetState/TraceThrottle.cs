/*************************************************************************
 * ModernUO                                                              *
 * Copyright 2019-2026 - ModernUO Development Team                       *
 * File: TraceThrottle.cs                                                *
 *                                                                       *
 * This program is free software: you can redistribute it and/or modify  *
 * it under the terms of the GNU General Public License as published by  *
 * the Free Software Foundation, either version 3 of the License, or     *
 * (at your option) any later version.                                   *
 *                                                                       *
 * You should have received a copy of the GNU General Public License     *
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 *************************************************************************/

namespace Server.Network;

public enum TraceDecision
{
    Skip,
    Write,
    WriteAndNoteLimit
}

/// <summary>
/// Decides which client-triggered diagnostics are written to their log files: unhandled packets
/// (<see cref="NetState.Trace"/>), invalid house designer items and network exceptions
/// (<see cref="NetState.TraceException"/>). Without a limit, a client that keeps sending bad packets makes the game
/// thread open a file and append to it for every one of them. Called on the game thread only.
/// </summary>
public static class TraceThrottle
{
    public const int MaxPerSession = 10;
    public const int MaxPerSecond = 20;
    private const long WindowMilliseconds = 1000;

    // Not readonly: TryTake must change the fields themselves, a readonly field would hand it a copy.
    private static RateWindow _sessionTraces;
    private static RateWindow _exceptions;
    private static int _suppressedExceptions;

    /// <summary>
    /// Counts one traced event of a session and says whether it is written.
    /// </summary>
    /// <param name="sessionCount">The session's counter of traced events, advanced by one when written.</param>
    /// <param name="now">The current tick count in milliseconds.</param>
    /// <returns>
    /// <see cref="TraceDecision.WriteAndNoteLimit"/> for the session's last written event, <see cref="TraceDecision.Skip"/>
    /// once the session or the shared per-second limit is used up.
    /// </returns>
    public static TraceDecision Next(ref int sessionCount, long now)
    {
        if (sessionCount >= MaxPerSession || !_sessionTraces.TryTake(now))
        {
            return TraceDecision.Skip;
        }

        sessionCount++;

        return sessionCount == MaxPerSession ? TraceDecision.WriteAndNoteLimit : TraceDecision.Write;
    }

    /// <summary>
    /// Says whether one more network exception is written; exceptions have no session, so only the shared
    /// per-second limit applies. Server errors share that limit with client-triggered ones, so the count of the
    /// exceptions skipped since the last written one is handed out with the next written one.
    /// </summary>
    /// <param name="now">The current tick count in milliseconds.</param>
    /// <param name="suppressed">The exceptions skipped since the last written one, when this one is written.</param>
    /// <returns>True while the limit of the current second is not used up.</returns>
    public static bool NextException(long now, out int suppressed)
    {
        if (!_exceptions.TryTake(now))
        {
            _suppressedExceptions++;
            suppressed = 0;
            return false;
        }

        suppressed = _suppressedExceptions;
        _suppressedExceptions = 0;
        return true;
    }

    internal static void Reset()
    {
        _sessionTraces = default;
        _exceptions = default;
        _suppressedExceptions = 0;
    }

    private struct RateWindow
    {
        private bool _started;
        private long _start;
        private int _count;

        public bool TryTake(long now)
        {
            // Compared by subtraction only: tick counts may start large or wrap.
            if (!_started || now - _start >= WindowMilliseconds)
            {
                _started = true;
                _start = now;
                _count = 0;
            }

            if (_count >= MaxPerSecond)
            {
                return false;
            }

            _count++;
            return true;
        }
    }
}
