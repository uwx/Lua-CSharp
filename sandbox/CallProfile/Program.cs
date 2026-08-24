using Lua;
using Lua.Standard;

// Profiling harness: runs the id_call Lua workload in a tight loop so a
// CPU profile can be captured (dotnet-trace) for the function-call path.
var state = LuaState.Create();
state.OpenStandardLibraries();
state.OpenStringLibrary();
state.OpenTableLibrary();

const string source = @"
    local id = function(x) return x end
    local s = 0
    for i = 1, 1000000 do s = s + id(i) end
    return s
";

var closure = state.Load(source, "@id_call");

// Warmup + JIT
for (var w = 0; w < 5; w++)
{
    state.Execute(closure);
}

var sw = new System.Diagnostics.Stopwatch();
var best = double.MaxValue;
var bestAlloc = 0L;
for (var r = 0; r < 40; r++)
{
    sw.Restart();
    var allocBefore = GC.GetTotalAllocatedBytes();
    state.Execute(closure);
    sw.Stop();
    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
    bestAlloc = Math.Max(bestAlloc, GC.GetTotalAllocatedBytes() - allocBefore);
}

Console.WriteLine($"BEST: {best:F2} ms/run (1M calls), max alloc: {bestAlloc / 1024.0:F1} KiB/run");
Console.WriteLine($"per-call: {best * 1000:F1} ns");
