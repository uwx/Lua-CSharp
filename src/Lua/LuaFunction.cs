namespace Lua;

public class LuaFunction(
    string name,
    Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>> func
)
{
    public string Name { get; } = name;
    internal Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>> Func { get; } =
        func;

    /// <summary>
    /// True for the standard library's builtin <c>next</c> iterator. The VM fast-paths
    /// <c>TFORCALL</c> over it, so a <c>for k, v in pairs(t)</c> loop traverses the table
    /// in place instead of paying a Lua-&gt;C# call per key.
    /// </summary>
    internal bool IsBuiltinNext { get; init; }

    public LuaFunction(Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>> func)
        : this("anonymous", func) { }
}
