using BenchmarkDotNet.Attributes;
using Lua;
using Lua.Runtime;
using Lua.Standard;

[Config(typeof(BenchmarkConfig))]
public class GarageWorkload
{
    string sourceText = default!;
    LuaState state = default!;
    LuaClosure closure = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var filePath = FileHelper.GetAbsolutePath("garage-workload.lua");
        sourceText = File.ReadAllText(filePath);
        state = LuaState.Create();
        state.OpenStandardLibraries();
        closure = state.Load(sourceText, sourceText);
    }

    [IterationSetup]
    public void Setup()
    {
        state = default!;
        GC.Collect();

        state = LuaState.Create();
        state.OpenStandardLibraries();
    }

    [Benchmark]
    public async ValueTask RunAsync()
    {
        await state.CallAsync(closure, []);
    }
}
