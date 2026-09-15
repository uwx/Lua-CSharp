using System.Diagnostics;
using System.Threading;

namespace Lua;

public static class LuaTableDiagnostics
{
    static long tableCount;
    static long stringInsertCount;
    static long stringInsertTicks;
    static long stringResizeCount;
    static long stringResizeTicks;
    static long setTableFastCount;
    static long setTableSlowCount;
    static long setTableSlowTicks;

    public static long TableCount => tableCount;
    public static long StringInsertCount => stringInsertCount;
    public static double StringInsertMilliseconds => stringInsertTicks * 1000.0 / Stopwatch.Frequency;
    public static long StringResizeCount => stringResizeCount;
    public static double StringResizeMilliseconds => stringResizeTicks * 1000.0 / Stopwatch.Frequency;
    public static long SetTableFastCount => setTableFastCount;
    public static long SetTableSlowCount => setTableSlowCount;
    public static double SetTableSlowMilliseconds => setTableSlowTicks * 1000.0 / Stopwatch.Frequency;

    public static void Reset()
    {
        Interlocked.Exchange(ref tableCount, 0);
        Interlocked.Exchange(ref stringInsertCount, 0);
        Interlocked.Exchange(ref stringInsertTicks, 0);
        Interlocked.Exchange(ref stringResizeCount, 0);
        Interlocked.Exchange(ref stringResizeTicks, 0);
        Interlocked.Exchange(ref setTableFastCount, 0);
        Interlocked.Exchange(ref setTableSlowCount, 0);
        Interlocked.Exchange(ref setTableSlowTicks, 0);
    }

    [Conditional("LUA_VM_DIAGNOSTICS")]
    internal static void RecordSetTableFast()
    {
        Interlocked.Increment(ref setTableFastCount);
    }

    [Conditional("LUA_VM_DIAGNOSTICS")]
    internal static void RecordSetTableSlow(long ticks)
    {
        Interlocked.Increment(ref setTableSlowCount);
        Interlocked.Add(ref setTableSlowTicks, ticks);
    }

    [Conditional("LUA_VM_DIAGNOSTICS")]
    internal static void RecordTableCreated()
    {
        Interlocked.Increment(ref tableCount);
    }

    [Conditional("LUA_VM_DIAGNOSTICS")]
    internal static void RecordStringInsert(long ticks)
    {
        Interlocked.Increment(ref stringInsertCount);
        Interlocked.Add(ref stringInsertTicks, ticks);
    }

    [Conditional("LUA_VM_DIAGNOSTICS")]
    internal static void RecordStringResize(long ticks)
    {
        Interlocked.Increment(ref stringResizeCount);
        Interlocked.Add(ref stringResizeTicks, ticks);
    }
}
