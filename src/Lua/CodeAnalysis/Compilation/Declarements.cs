using System.Runtime.CompilerServices;
using Lua.Internal;

namespace Lua.CodeAnalysis.Compilation;

unsafe struct TextReader(char* ptr, int length)
{
    public int Position;

    public (char, bool) Read()
    {
        return Position >= length ? ('\0', false) : (ptr[Position++], true);
    }

    public bool TryRead(out char c)
    {
        if (Position >= length)
        {
            c = '\0';
            return false;
        }

        c = ptr[Position++];
        return true;
    }

    public char Current => ptr[Position];

    public ReadOnlySpan<char> Span => new(ptr, length);

    public int Length => length;
}

unsafe struct AssignmentTarget(ref AssignmentTarget previous, ExprDesc exprDesc)
{
    public readonly AssignmentTarget* Previous = (AssignmentTarget*)Unsafe.AsPointer(ref previous);
    public ExprDesc Description = exprDesc;
}

struct Label
{
    public string Name;
    public int Pc,
        Line;
    public int ActiveVariableCount;
}

/// <summary>
/// A `const` local binding (Luau). Recorded on the <see cref="Function"/> rather than in
/// <see cref="LocalVariable"/> so the public, dumped local-variable record is untouched.
/// <see cref="Kind"/> is <see cref="Kind.Void"/> when the initializer was not a literal and
/// therefore cannot be inlined at the use sites.
/// </summary>
struct ConstBinding
{
    /// <summary>Local-variable index (== register level) inside the declaring function.</summary>
    public int Index;

    public Kind Kind;
    public double Value;
    public long IntValue;
    public bool IsInteger;
    public string? StringValue;
}

/// <summary>
/// Bookkeeping for a lexically enclosing loop while parsing Luau `continue` statements.
/// The stack lives on <see cref="Parser"/> and is truncated when a nested function body
/// starts, so `continue` can never target a loop in an enclosing function.
/// </summary>
struct LoopContext
{
    public bool IsRepeat;

    /// <summary>
    /// Lowest active-variable level a `continue` in this loop jumped over, or -1 when the
    /// loop contains no `continue`. A repeat loop's `until` condition may not read a local
    /// declared at or above this level (the `continue` would skip its declaration).
    /// </summary>
    public int MinContinueLevel;

    /// <summary>Line of the `continue` that produced <see cref="MinContinueLevel"/>.</summary>
    public int MinContinueLine;
}

class Block : IPoolNode<Block>
{
    public Block? Previous;
    public int FirstLabel,
        FirstGoto;
    public int ActiveVariableCount;
    public bool HasUpValue,
        IsLoop;

    Block() { }

    ref Block? IPoolNode<Block>.NextNode => ref Previous;

    static LinkedPool<Block> Pool;

    public static Block Get(
        Block? previous,
        int firstLabel,
        int firstGoto,
        int activeVariableCount,
        bool hasUpValue,
        bool isLoop
    )
    {
        if (!Pool.TryPop(out var block))
        {
            block = new();
        }

        block.Previous = previous;
        block.FirstLabel = firstLabel;
        block.FirstGoto = firstGoto;
        block.ActiveVariableCount = activeVariableCount;
        block.HasUpValue = hasUpValue;
        block.IsLoop = isLoop;

        return block;
    }

    public void Release()
    {
        Previous = null;
        Pool.TryPush(this);
    }
}

struct ExprDesc
{
    public Kind Kind;
    public int Index;
    public int Table;
    public Kind TableType;
    public int Info;
    public int T,
        F;
    public double Value;
    public long IntValue;
    public bool IsInteger;

    public readonly bool HasJumps()
    {
        return T != F;
    }

    public readonly bool IsNumeral()
    {
        return Kind == Kind.Number && T == Function.NoJump && F == Function.NoJump;
    }

    public readonly bool IsVariable()
    {
        return Kind is >= Kind.Local and <= Kind.Indexed;
    }

    public readonly bool HasMultipleReturns()
    {
        return Kind == Kind.Call || Kind == Kind.VarArg;
    }
}

enum Kind
{
    Void = 0,
    Nil = 1,
    True = 2,
    False = 3,
    Constant = 4,
    Number = 5,
    NonRelocatable = 6,
    Local = 7,
    UpValue = 8,
    Indexed = 9,
    Jump = 10,
    Relocatable = 11,
    Call = 12,
    VarArg = 13,
}
