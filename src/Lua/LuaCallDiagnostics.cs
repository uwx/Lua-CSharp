using System.Diagnostics;
using System.Threading;

namespace Lua;

/// <summary>
/// Cheap counters around <see cref="LuaState.RunSyncCore{T}"/> (the sync boundary every
/// native-to-Lua call crosses) so callers can measure how many top-level Lua invocations
/// happen during some window of interest, and how much wall time they cost in total.
/// Not wired into any tracing system on purpose - read the fields directly (e.g. snapshot
/// before/after a UI event) rather than treating this as a general-purpose profiler.
///
/// A native-to-Lua call can itself call back into a host function that re-enters
/// RunSyncCore before the outer call returns (e.g. an event handler synchronously
/// triggering another flush). <see cref="CallCount"/>/<see cref="ElapsedMilliseconds"/>
/// only count the outermost crossing of each such nest, so their sum reflects real
/// non-overlapping wall-clock time; summing every crossing including nested ones would
/// double-count the overlapping window. <see cref="TotalCrossingCount"/> reports every
/// crossing, nested included, for visibility into how much re-entrancy is happening.
/// </summary>
public static class LuaCallDiagnostics
{
    static long callCount;
    static long elapsedTicks;
    static long totalCrossingCount;

    public static long CallCount => callCount;
    public static double ElapsedMilliseconds => elapsedTicks * 1000.0 / Stopwatch.Frequency;
    public static long TotalCrossingCount => totalCrossingCount;

    public static void Reset()
    {
        Interlocked.Exchange(ref callCount, 0);
        Interlocked.Exchange(ref elapsedTicks, 0);
        Interlocked.Exchange(ref totalCrossingCount, 0);
    }

    internal static void Record(long ticks, bool isOutermost)
    {
        Interlocked.Increment(ref totalCrossingCount);
        if (isOutermost)
        {
            Interlocked.Increment(ref callCount);
            Interlocked.Add(ref elapsedTicks, ticks);
        }
    }
}
