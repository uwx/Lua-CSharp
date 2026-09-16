namespace Lua.CodeAnalysis.Compilation.Ast;

/// <summary>
/// Statement nodes for the compiler-rewrite plan's AST. "Increment B" (repeat/until, compound
/// assignment, goto/label/break/continue, const declarations) is implemented; <c>type</c>
/// statements remain out of scope permanently (they're erased at parse time today too, via
/// <c>TypeStatement</c>/<c>SkipTypeAnnotation</c>, and have no runtime effect to preserve).
///
/// <see cref="AstParser"/> does not perform goto/label semantic validation (duplicate-label,
/// undefined-goto, "jumps into scope of local" checks) during AST building -- today's
/// implementation of that (<c>Function.cs:394-502</c>, <c>MakeGoto</c>/<c>FindLabel</c>/
/// <c>CloseGoto</c>/<c>MoveGotosOut</c>) is inherently coupled to codegen: it patches real
/// bytecode PCs (<c>PatchList</c>/<c>PatchClose</c>) as part of resolving a goto to its label.
/// This is deliberately deferred to Milestone 2's codegen pass, which has real PCs to patch
/// against -- see the compiler-rewrite plan's "goto/label PCs are a genuine seam" note.
/// </summary>
abstract class Stat
{
    public int Line;
}

sealed class BlockStat : Stat
{
    public required List<Stat> Statements;

    /// <summary>
    /// The line of the token immediately following this block's last statement (its closing
    /// keyword -- <c>end</c>/<c>until</c> -- or, for the main chunk, EOF). Matches what
    /// <c>Scanner.LastLine</c>/<c>LineNumber</c> holds at that point in today's compiler.
    /// Consumed by: the main chunk's implicit final <c>Return</c>
    /// (<see cref="Compilation.Function.CloseMainFunction"/>); and a trailing
    /// <see cref="ContinueStat"/>'s jump, whose line-stamp today is *always* the line of
    /// whatever follows <c>continue</c> (a side effect of the lookahead
    /// <c>IsContinueStatement</c> needs to disambiguate `continue` from `continue()`/
    /// `continue.x`, which advances the scanner's line-tracking before <c>Function.Jump()</c>
    /// runs) -- when <c>continue</c> is a block's last statement, that following line is this
    /// block's own <c>EndLine</c>. A function body's equivalent value is
    /// <see cref="FunctionExpr.LastLineDefined"/> instead.
    /// </summary>
    public int EndLine;
}

sealed class LocalStat : Stat
{
    public required List<string> Names;

    /// <summary>May be shorter than <see cref="Names"/> (extra names default to nil) or, if the
    /// last initializer has multiple returns, conceptually longer -- matches
    /// <c>Function.AdjustAssignment</c> (<c>Function.cs:2031-2062</c>).</summary>
    public required List<Expr> Initializers;
}

/// <summary>
/// <c>target {, target} = value {, value}</c>. Each target must resolve to a <see cref="NameExpr"/>
/// (local/upvalue write) or <see cref="IndexExpr"/> (table write) at codegen time --
/// <see cref="AstParser"/> does not itself validate assignability beyond what parsing already
/// guarantees (only those two expression shapes are ever routed here).
/// </summary>
sealed class AssignmentStat : Stat
{
    public required List<Expr> Targets;
    public required List<Expr> Values;
}

/// <summary>A call expression used as a statement (its results are discarded).</summary>
sealed class ExpressionStat : Stat
{
    public required Expr Expression;
}

sealed class ReturnStat : Stat
{
    public required List<Expr> Values;
}

sealed class IfBranch
{
    public required Expr Condition;
    public required BlockStat Then;

    /// <summary>
    /// Line of the <c>then</c> keyword. The conditional test/jump for this branch is stamped with
    /// this rather than with <see cref="Condition"/>'s own line, matching what Lua 5.1 produces:
    /// <c>TestThenBlock</c> consumes <c>then</c> (<c>CheckNext</c>, Parser.cs:1225) and only *then*
    /// calls <c>GoIfTrue</c>/<c>GoIfFalse</c> (Parser.cs:1228/1242), so the jump is stamped with
    /// whatever line the scanner has reached -- the <c>then</c>'s. This is a real source position
    /// rather than a scanner artifact: the jump is what *chooses the branch*, and the branch opens
    /// at <c>then</c>. Invisible for the overwhelmingly common single-line
    /// <c>if cond then</c>, where the two lines coincide.
    /// </summary>
    public required int ThenLine;
}

/// <summary>
/// The full <c>if/elseif*/else?</c> chain flattened into one node (rather than nested
/// <c>IfStat</c>s inside an else-block), so codegen's jump-chaining loop
/// (mirroring <c>IfStatement</c>, <c>Parser.cs:1245-1260</c>) is a simple iteration.
/// </summary>
sealed class IfStat : Stat
{
    public required List<IfBranch> Branches;
    public BlockStat? Else;
}

sealed class WhileStat : Stat
{
    public required Expr Condition;
    public required BlockStat Body;
}

sealed class ForNumericStat : Stat
{
    public required string Name;
    public required Expr Start;
    public required Expr Stop;

    /// <summary>Null means the default step of integer <c>1</c> (<c>Parser.cs:1108-1113</c>).</summary>
    public Expr? Step;

    public required BlockStat Body;
}

sealed class ForGenericStat : Stat
{
    public required List<string> Names;
    public required List<Expr> Expressions;
    public required BlockStat Body;

    /// <summary>
    /// Line of the first token of <see cref="Expressions"/> -- what <c>ForStatement</c> captures
    /// as its own <c>line</c> immediately after consuming <c>in</c> (Parser.cs:1148-1149) and then
    /// uses for both the generalized-iteration iterator call's <c>FixLine</c> and
    /// <c>ForBody</c>'s back-edge. Deliberately not <see cref="Stat.Line"/>: for a loop written
    /// across lines (<c>for k,v in \n 3 \n do</c>) the two differ, and the original stamps the
    /// expression's line, so a runtime error inside the iterator call reports that line.
    /// </summary>
    public required int ExpressionLine;
}

/// <summary><c>function a.b.c() end</c> / <c>function a.b:c() end</c> -- <see cref="Target"/> is
/// the (possibly chained) assignment target built the same way <c>FunctionName</c>
/// (<c>Parser.cs:1511-1534</c>) does, as a plain <see cref="NameExpr"/> or <see cref="IndexExpr"/> chain.</summary>
sealed class FunctionStat : Stat
{
    public required Expr Target;
    public required FunctionExpr Function;
}

/// <summary><c>local function f() end</c> -- distinct from <see cref="FunctionStat"/> because the
/// name must be in scope *inside* the body for recursion (<c>Parser.cs:1544-1556</c>).
/// <see cref="IsConst"/> is set for Luau's <c>const function f() end</c> form
/// (<c>ConstStatement</c>, <c>Parser.cs:1626-1631</c>) -- the binding itself is immutable, same
/// as any other <c>const</c> declaration.</summary>
sealed class LocalFunctionStat : Stat
{
    public required string Name;
    public required FunctionExpr Function;
    public bool IsConst;
}

/// <summary>
/// Luau <c>repeat ... until condition</c>. <see cref="Condition"/> is evaluated with
/// <see cref="Body"/>'s locals still in scope (<c>RepeatStatement</c>, <c>Parser.cs:1287-1327</c>)
/// -- codegen must visit them in that order, not as two independent children of a generic
/// traversal. <see cref="AstParser"/> already builds them in this order (parses the condition
/// before leaving the body's scope block), so this invariant only needs to be preserved by
/// whatever visits this node next, not re-derived.
/// </summary>
sealed class RepeatStat : Stat
{
    public required BlockStat Body;
    public required Expr Condition;
}

/// <summary>
/// <c>target op= value</c> (Luau): <see cref="Target"/> is evaluated once by codegen for both
/// the read and the write side (<c>t[f()] += 1</c> must not call <c>f()</c> twice) -- see
/// <c>Function.BeginCompoundAssignment</c>/<c>EndCompoundAssignment</c>. Storing one shared
/// <see cref="Target"/> node here (rather than re-parsing it) is what lets codegen honor that.
/// </summary>
sealed class CompoundAssignmentStat : Stat
{
    public required Expr Target;

    /// <summary>One of <c>Function.OprAdd</c>/<c>OprSub</c>/<c>OprMul</c>/<c>OprDiv</c>/<c>OprIDiv</c>/<c>OprMod</c>/<c>OprPow</c>/<c>OprConcat</c>.</summary>
    public required int Op;
    public required Expr Value;
}

sealed class BreakStat : Stat;

/// <summary>Luau <c>continue</c>: jumps to the end of the innermost loop.</summary>
sealed class ContinueStat : Stat;

sealed class GotoStat : Stat
{
    public required string Name;
}

/// <summary>
/// A bare <c>;</c>. Distinguished from an empty <see cref="BlockStat"/> (which a <c>do end</c>
/// also produces) because the two differ in two places: <c>;</c> emits nothing at all
/// (<c>Statement</c>'s <c>case ';'</c>, Parser.cs:1900-1902, never enters a block), whereas an
/// empty <c>do end</c> still opens and closes one; and only <c>;</c> counts as a skippable no-op
/// for <see cref="LabelStat.IsLastNoOpStatement"/> (<c>SkipEmptyStatements</c>, Parser.cs:1395).
/// </summary>
sealed class EmptyStat : Stat;

sealed class LabelStat : Stat
{
    public required string Name;

    /// <summary>
    /// True when this label is the last no-op statement in its block -- nothing but further
    /// <c>;</c>/labels follows it, and the block does not end in <c>until</c>. Mirrors
    /// <c>LabelStatement</c>'s <c>SkipEmptyStatements()</c>+<c>BlockFollow(false)</c> test
    /// (Parser.cs:1408-1412), which then re-levels the label to its enclosing block's
    /// <c>ActiveVariableCount</c>: its own block's locals are already out of scope, which is what
    /// makes a forward <c>goto</c> over a local declaration to a trailing label legal.
    /// </summary>
    public bool IsLastNoOpStatement;
}

/// <summary>Luau <c>const name {, name} = explist</c> (non-function form; see
/// <see cref="LocalFunctionStat.IsConst"/> for <c>const function</c>).</summary>
sealed class ConstStat : Stat
{
    public required List<string> Names;
    public required List<Expr> Initializers;
}
