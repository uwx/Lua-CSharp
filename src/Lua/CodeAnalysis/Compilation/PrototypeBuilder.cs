using Lua.Internal;
using Lua.Runtime;

namespace Lua.CodeAnalysis.Compilation;

class PrototypeBuilder : IPoolNode<PrototypeBuilder>
{
    internal FastListCore<LuaValue> ConstantsList;

    public ReadOnlySpan<LuaValue> Constants => ConstantsList.AsSpan();

    internal FastListCore<Instruction> CodeList;

    public ReadOnlySpan<Instruction> Code => CodeList.AsSpan();

    internal FastListCore<PrototypeBuilder> PrototypeList;

    public ReadOnlySpan<PrototypeBuilder> Prototypes => PrototypeList.AsSpan();

    internal FastListCore<int> LineInfoList;

    public ReadOnlySpan<int> LineInfo => LineInfoList.AsSpan();

    internal FastListCore<LocalVariable> LocalVariablesList;

    public ReadOnlySpan<LocalVariable> LocalVariables => LocalVariablesList.AsSpan();

    /// <summary>
    /// Parallel to <see cref="LocalVariablesList"/>: true once the local is assigned anywhere
    /// after its declaration (including from nested functions). Drives by-value capture, see
    /// <see cref="UpValueDesc.ByValue"/>.
    /// </summary>
    internal FastListCore<bool> LocalWrittenList;

    internal FastListCore<UpValueDesc> UpValuesList;

    public ReadOnlySpan<UpValueDesc> UpValues => UpValuesList.AsSpan();

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: one prebuilt all-constant <see cref="LuaTable"/> per
    /// fused table literal, indexed by <see cref="OpCode.DupTable"/>'s trailing ExtraArg.
    /// </summary>
    internal FastListCore<LuaTable> TemplatesList;

    public ReadOnlySpan<LuaTable> Templates => TemplatesList.AsSpan();

    public string Source;
    public string? Name;
    public int LineDefined,
        LastLineDefined;
    public int ParameterCount,
        MaxStackSize;
    public bool IsVarArg;

    internal PrototypeBuilder(string source)
    {
        Source = source;
    }

    static LinkedPool<PrototypeBuilder> pool;

    PrototypeBuilder? nextNode;

    ref PrototypeBuilder? IPoolNode<PrototypeBuilder>.NextNode => ref nextNode;

    internal static PrototypeBuilder Get(string source)
    {
        if (!pool.TryPop(out var f))
        {
            f = new(source);
        }

        f.Source = source;
        f.Name = null;
        f.LineDefined = 0;
        f.LastLineDefined = 0;
        f.ParameterCount = 0;
        f.MaxStackSize = 0;
        f.IsVarArg = false;
        return f;
    }

    internal void Release()
    {
        ConstantsList.Clear();
        CodeList.Clear();
        PrototypeList.Clear();
        LineInfoList.Clear();
        LocalVariablesList.Clear();
        LocalWrittenList.Clear();
        UpValuesList.Clear();
        TemplatesList.Clear();
        LineDefined = 0;
        LastLineDefined = 0;
        ParameterCount = 0;
        MaxStackSize = 0;
        IsVarArg = false;
        Name = null;
        pool.TryPush(this);
    }

    public Prototype CreatePrototypeAndRelease()
    {
        var protoTypes =
            Prototypes.Length == 0 ? Array.Empty<Prototype>() : new Prototype[Prototypes.Length];
        for (var i = 0; i < Prototypes.Length; i++)
        {
            protoTypes[i] = Prototypes[i].CreatePrototypeAndRelease(); //ref
        }

        Prototype p = new(
            Name,
            Source,
            LineDefined,
            LastLineDefined,
            ParameterCount,
            MaxStackSize,
            IsVarArg,
            Constants.ToArray(),
            Code.ToArray(),
            protoTypes,
            LineInfo.ToArray(),
            LocalVariables.ToArray(),
            UpValues.ToArray(),
            Templates.ToArray()
        );
        Release();
        return p;
    }
}
