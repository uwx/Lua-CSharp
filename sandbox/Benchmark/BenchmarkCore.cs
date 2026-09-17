using Lua;
using Lua.Runtime;
using MoonSharp.Interpreter;

public class BenchmarkCore : IDisposable
{
    public NLua.Lua NLuaState => nLuaState;
    public Script MoonSharpState => moonSharpState;
    public LuaState LuaCSharpState => luaCSharpState;
    public string FilePath => filePath;
    public string SourceText => sourceText;

    // Every script is compiled once in Setup, so the benchmarks that run these closures
    // measure execution only, without the load step the DoString/DoFile variants pay.
    public NLua.LuaFunction NLuaCompiledString => nLuaCompiledString;
    public NLua.LuaFunction NLuaCompiledFile => nLuaCompiledFile;
    public DynValue MoonSharpCompiledString => moonSharpCompiledString;
    public DynValue MoonSharpCompiledFile => moonSharpCompiledFile;
    public LuaClosure LuaCSharpCompiledString => luaCSharpCompiledString;
    public LuaClosure LuaCSharpCompiledFile => luaCSharpCompiledFile;

    NLua.Lua nLuaState = default!;
    Script moonSharpState = default!;
    LuaState luaCSharpState = default!;
    string filePath = default!;
    string sourceText = default!;

    NLua.LuaFunction nLuaCompiledString = default!;
    NLua.LuaFunction nLuaCompiledFile = default!;
    DynValue moonSharpCompiledString = default!;
    DynValue moonSharpCompiledFile = default!;
    LuaClosure luaCSharpCompiledString = default!;
    LuaClosure luaCSharpCompiledFile = default!;

    public void Setup(string fileName)
    {
        // moonsharp
        moonSharpState = new();
        Script.WarmUp();

        // NLua
        nLuaState = new();

        // Lua-CSharp
        luaCSharpState = LuaState.Create();

        filePath = FileHelper.GetAbsolutePath(fileName);
        sourceText = File.ReadAllText(filePath);

        nLuaCompiledString = nLuaState.LoadString(sourceText, fileName);
        nLuaCompiledFile = nLuaState.LoadFile(filePath);
        moonSharpCompiledString = moonSharpState.LoadString(sourceText);
        moonSharpCompiledFile = moonSharpState.LoadFile(filePath);
        luaCSharpCompiledString = luaCSharpState.Load(sourceText, sourceText);

        // The built-in file system completes synchronously, so this cannot deadlock.
        luaCSharpCompiledFile = luaCSharpState
            .LoadFileAsync(filePath, "bt", null, default)
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        nLuaState.Dispose();
    }
}
