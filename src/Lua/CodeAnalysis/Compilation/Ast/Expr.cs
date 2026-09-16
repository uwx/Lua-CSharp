namespace Lua.CodeAnalysis.Compilation.Ast;

/// <summary>
/// Milestone 1 of the compiler-rewrite plan (single-pass streaming -> two-pass AST + codegen):
/// a retained expression tree, built by <see cref="AstParser"/> during a resolution-only pass
/// that performs no register allocation or instruction emission. The node inventory here is
/// complete -- the grammar is covered by Milestone 1's Increment A plus Increment B, and the
/// whole real corpus builds. The one construct with no node is `type`/`export type`, which is
/// erased at parse time exactly as today's compiler erases it (<c>SkipTypeAnnotation</c>) and has
/// no runtime effect to represent; see <c>Stat.cs</c>.
///
/// Every node carries <see cref="Line"/>, captured at the same point today's single-pass
/// compiler would stamp it into <c>Proto.LineInfoList</c>, so a later codegen pass can
/// reproduce byte-identical line info.
///
/// See <c>docs/compiler-ast.md</c> for how a pass over this tree is allowed to treat it, and in
/// particular for why a transform must not disturb which declarations are active.
/// </summary>
abstract class Expr
{
    public int Line;
}

sealed class NilExpr : Expr;

sealed class TrueExpr : Expr;

sealed class FalseExpr : Expr;

sealed class VarArgExpr : Expr;

sealed class NumberExpr : Expr
{
    public double Value;
    public long IntValue;
    public bool IsInteger;
}

sealed class StringExpr : Expr
{
    public string Value = "";
}

/// <summary>
/// A resolved local or upvalue reference. A bare name that resolves to neither (a global) is
/// never represented as a <see cref="NameExpr"/> -- <see cref="AstParser"/> builds it as an
/// <see cref="IndexExpr"/> against the resolved <c>_ENV</c> reference instead, exactly
/// mirroring <c>Function.SingleVariable</c> (<c>Function.cs:2190-2198</c>) today.
/// </summary>
sealed class NameExpr : Expr
{
    public enum RefKind
    {
        Local,
        UpValue,
    }

    /// <summary>
    /// The name as written. This is the **load-bearing** field: codegen re-resolves every name
    /// from scratch through <c>Function.SingleVariableHelper</c> keyed off this string
    /// (<c>CodeGenerator.ResolveNameExpr</c>), and never reads <see cref="Kind"/>/<see cref="Index"/>.
    /// See <c>docs/compiler-ast.md</c>'s "one contract that matters most" for why, and for what
    /// that means for a pass that transforms this tree.
    /// </summary>
    public required string Name;

    /// <summary>
    /// The resolution recorded during parsing. Diagnostics/debugging only -- codegen re-derives
    /// both. Do not build anything on these without reading the note above first.
    /// </summary>
    public required RefKind Kind;

    /// <summary>Local-variable index (== register level) or upvalue index, per <see cref="Kind"/>.
    /// Diagnostics/debugging only, as above.</summary>
    public required int Index;
}

/// <summary>
/// <c>object.key</c> (<see cref="IsDotSugar"/> true, <see cref="Key"/> always a
/// <see cref="StringExpr"/>) or <c>object[key]</c> (<see cref="IsDotSugar"/> false, any key
/// expression). This distinction is preserved deliberately even though today's codegen doesn't
/// need it -- it's what Milestone 3's GetImport optimization inspects to recognize a fusable
/// dotted chain.
/// </summary>
sealed class IndexExpr : Expr
{
    public required Expr Object;
    public required Expr Key;
    public required bool IsDotSugar;
}

sealed class CallExpr : Expr
{
    public required Expr Callee;
    public required List<Expr> Arguments;
}

/// <summary>
/// <c>receiver:name(args)</c>. Kept distinct from <see cref="CallExpr"/> rather than desugared
/// to <c>CallExpr(IndexExpr(receiver, name), [receiver, ...args])</c>, to avoid double-
/// evaluating <c>receiver</c> and to keep the <c>Self</c> fused-instruction opcode available to
/// codegen as a single-node target (matches <c>Function.Self</c>, used from
/// <c>SuffixedExpression</c>, <c>Parser.cs:333-336</c>).
/// </summary>
sealed class MethodCallExpr : Expr
{
    public required Expr Receiver;
    public required string MethodName;
    public required List<Expr> Arguments;
}

sealed class BinaryExpr : Expr
{
    /// <summary>One of <c>Function.OprAdd</c>..<c>Function.OprGE</c> or <c>OprIDiv</c> (never
    /// <c>OprAnd</c>/<c>OprOr</c> -- those are <see cref="AndOrExpr"/>).</summary>
    public required int Op;
    public required Expr Left;
    public required Expr Right;
}

sealed class UnaryExpr : Expr
{
    /// <summary>One of <c>Function.OprMinus</c>/<c>OprNot</c>/<c>OprLength</c>.</summary>
    public required int Op;
    public required Expr Operand;
}

/// <summary>
/// Kept distinct from <see cref="BinaryExpr"/> because <c>and</c>/<c>or</c> need short-circuit
/// jump codegen (<c>Function.GoIfTrue</c>/<c>GoIfFalse</c>-style), not arithmetic/comparison
/// codegen -- conflating them risks a codegen switch that silently mishandles jump lists.
/// </summary>
sealed class AndOrExpr : Expr
{
    public required bool IsAnd;
    public required Expr Left;
    public required Expr Right;
}

sealed class FunctionExpr : Expr
{
    public required List<string> Parameters;
    public required bool IsVarArg;

    /// <summary>True for a method body (<c>function t:m() end</c>) -- an implicit leading
    /// <c>self</c> parameter has already been added to <see cref="Parameters"/>, matching
    /// <c>Body</c>'s <c>isMethod</c> handling (<c>Parser.cs:1480-1484</c>).</summary>
    public required bool IsMethod;

    public required BlockStat Body;
    public required int LineDefined;
    public int LastLineDefined;

    /// <summary>Optional name for the compiled prototype's debug info (<c>local X = function()
    /// end</c> / <c>function a.b.c() end</c> naming, <c>Parser.cs:1578-1583,1463-1471</c>).</summary>
    public string? Name;
}

sealed class TableField
{
    /// <summary>Null for an array-position field (<c>{value}</c>); otherwise the hash key
    /// (<c>{[k] = v}</c> or <c>{name = v}</c>, the latter represented as a <see cref="StringExpr"/> key).</summary>
    public Expr? Key;
    public required Expr Value;
}

sealed class TableConstructorExpr : Expr
{
    public required List<TableField> Fields;

    /// <summary>The line of the token consumed right after the closing <c>}</c> (i.e.
    /// <c>Scanner.LastLine</c> once <c>CheckMatch('}', ...)</c> has run) -- what
    /// <c>Function.CloseConstructor</c>'s final <c>SetList</c> stamp actually uses today, since
    /// that call happens after <c>}</c> is consumed (<c>Constructor</c>, Parser.cs:244-245).</summary>
    public int EndLine;
}

/// <summary>
/// <c>(expr)</c> -- preserves Lua's truncate-to-one-value semantics for a parenthesized
/// multi-return call, distinct from a bare <c>f()</c> used directly (matches
/// <c>PrimaryExpression</c>'s <c>'('</c> case, <c>Parser.cs:304-310</c>, which discharges
/// variables but keeps the call's result count unadjusted until context demands one).
/// </summary>
sealed class ParenExpr : Expr
{
    public required Expr Inner;
}

sealed class IfElseBranch
{
    public required Expr Condition;
    public required Expr Value;
}

/// <summary>
/// Luau's <c>if cond then value {elseif cond then value} else value</c> *expression* form
/// (<c>IfElseExpression</c>, <c>Parser.cs:557-587</c>) -- distinct from <see cref="Stat.IfStat"/>.
/// A statement-position <c>if</c> is never parsed as this: <c>Statement</c> handles <c>TkIf</c>
/// before any expression parsing begins, exactly mirroring today's parser.
/// </summary>
sealed class IfElseExpr : Expr
{
    public required List<IfElseBranch> Branches;
    public required Expr Else;
}

/// <summary>
/// A Luau interpolated (backtick) string, <c>`...{expr}...`</c>. <see cref="Parts"/> alternates
/// literal text segments (always <see cref="StringExpr"/>) and hole expressions in source order,
/// skipping zero-length literal segments exactly as <c>InterpolatedString</c>
/// (<c>Parser.cs:504-550</c>) does. Codegen concatenates them the same way that method does: one
/// call to an internal helper (<c>LuaBuiltin.InterpolationBuilder</c>) with every part as an
/// argument, so <c>__tostring</c>/number formatting matches <c>tostring</c> exactly.
/// </summary>
sealed class InterpolatedStringExpr : Expr
{
    public required List<Expr> Parts;
}
