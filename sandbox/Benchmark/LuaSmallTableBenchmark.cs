using BenchmarkDotNet.Attributes;
using Lua;

/// <summary>
/// Small, record-shaped table lifecycle -- the shape signals.luau's Computation nodes and
/// typical UI prop tables actually create (a handful of string fields), as opposed to
/// <see cref="LuaTableBenchmark"/>'s large N=4096 stress cases. Exists to sanity-check
/// whether in-game Stopwatch-based instrumentation (single-digit-to-double-digit
/// nanoseconds per operation) is trustworthy, or lost below
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>'s resolution --
/// BenchmarkDotNet amortizes over many invocations per measurement, so it isn't subject
/// to that failure mode.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class LuaSmallTableBenchmark
{
    const string K1 = "x", K2 = "y", K3 = "width", K4 = "height", K5 = "visible", K6 = "color";

    [Benchmark(Description = "new LuaTable() default ctor")]
    public LuaTable Construct_Default()
    {
        return new LuaTable();
    }

    [Benchmark(Description = "create + fill 6 string keys (unsized, 0/0)")]
    public LuaTable CreateAndFill_Unsized()
    {
        var t = new LuaTable(0, 0);
        t[K1] = 1;
        t[K2] = 2;
        t[K3] = 3;
        t[K4] = 4;
        t[K5] = 5;
        t[K6] = 6;
        return t;
    }

    [Benchmark(Description = "create + fill 6 string keys (default 8/8 ctor)")]
    public LuaTable CreateAndFill_DefaultSized()
    {
        var t = new LuaTable();
        t[K1] = 1;
        t[K2] = 2;
        t[K3] = 3;
        t[K4] = 4;
        t[K5] = 5;
        t[K6] = 6;
        return t;
    }

    [Benchmark(Description = "create + fill 6 string keys (correctly sized)")]
    public LuaTable CreateAndFill_Sized()
    {
        var t = new LuaTable(0, 6);
        t[K1] = 1;
        t[K2] = 2;
        t[K3] = 3;
        t[K4] = 4;
        t[K5] = 5;
        t[K6] = 6;
        return t;
    }
}
