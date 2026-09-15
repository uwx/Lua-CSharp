using System.Diagnostics;
using System.Threading;

namespace Lua;

/// <summary>
/// Cheap counters around <see cref="LuaState.RunSyncCore{T}"/> (the sync boundary every
/// native-to-Lua call crosses) so callers can measure how many top-level Lua invocations
/// happen during some window of interest, and how much wall time they cost in total.
/// Not wired into any tracing system on purpose - read the fields directly (e.g. snapshot
/// before/after a UI event) rather than treating this as a general-purpose profiler.
/// </summary>
public static class LuaCallDiagnostics
{
    static long callCount;
    static long elapsedTicks;

    public static long CallCount => callCount;
    public static double ElapsedMilliseconds => elapsedTicks * 1000.0 / Stopwatch.Frequency;

    public static void Reset()
    {
        Interlocked.Exchange(ref callCount, 0);
        Interlocked.Exchange(ref elapsedTicks, 0);
    }

    internal static void Record(long ticks)
    {
        Interlocked.Increment(ref callCount);
        Interlocked.Add(ref elapsedTicks, ticks);
    }
}
