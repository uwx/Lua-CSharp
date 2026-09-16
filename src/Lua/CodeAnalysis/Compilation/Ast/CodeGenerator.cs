using Lua.Internal;
using Lua.Runtime;

namespace Lua.CodeAnalysis.Compilation.Ast;

using static Constants;
using static Function;
using static Scanner;
using static System.Diagnostics.Debug;

/// <summary>
/// Compiler-rewrite plan Milestone 2: walks the AST <see cref="AstParser"/> builds and drives
/// today's proven codegen machinery in <see cref="Function"/> (register allocation, the
/// constant pool, jump-list patching, table-constructor emission) to produce a
/// <see cref="Prototype"/>. This is, deliberately, a mechanical re-derivation of exactly what
/// <see cref="Parser"/>'s original token-driven walk does -- register/upvalue indices are
/// re-resolved from scratch here (via <see cref="Function.SingleVariableHelper"/>, keyed off
/// each <see cref="NameExpr"/>'s <see cref="NameExpr.Name"/>) rather than trusted from the AST's
/// already-computed <see cref="NameExpr.Kind"/>/<see cref="NameExpr.Index"/> fields, because
/// Function.cs's jump/register/upvalue bookkeeping (<c>ActiveVariableCount</c>,
/// <c>FreeRegisterCount</c>, <c>Block</c> nesting, <c>Proto.UpValuesList</c>) must be replayed
/// in lockstep with emission regardless -- doing so also re-derives the same names naturally,
/// with no extra cost, and keeps this file's structure a close mirror of <c>Parser.cs</c> for
/// reviewability. <see cref="Function.cs"/>'s jump/patch/goto/label/const-folding/table-
/// constructor logic is called verbatim, unchanged -- only its caller moves from live tokens to
/// AST nodes.
///
/// Semantic validation that produces no bytecode for already-valid programs is, however, fully
/// replicated here, because it is observable as a *compile error* rather than as code: the
/// assignment/compound-assignment target checks, the "too many Go levels" limit, and Luau's
/// "local undefined because a `continue` jumped over it in a repeat condition" diagnostic
/// (gated on <c>RepeatConditionFunction</c>/<c>RepeatConditionMinLevel</c>) all had to be
/// restored at the cutover -- they live in <see cref="Parser"/>'s checks and in
/// <see cref="Function.SingleVariableHelper"/>, both of which this file's callers bypass unless
/// it re-issues them. See the compiler-rewrite plan's Milestone 2b for the full list.
///
/// <b>Line numbers are a deliberate exception to "byte-identical."</b> Milestone 2's exit
/// criterion requires structural equivalence (opcodes, operands, instruction counts, constants,
/// locals, upvalues) to be byte-identical to the original compiler -- verified via
/// <c>BytecodeDump.Dump(..., includeLines: false)</c> in <c>CodeGenEquivalenceTests</c> -- but
/// per-instruction *line numbers* are not required to match. The original single-pass compiler
/// stamps a line via whatever its scanner's <c>LastLine</c> happens to be at each
/// <c>Function.Encode</c> call, which is frequently an accident of token-lookahead timing (e.g.
/// a disambiguating <c>LookAhead()</c> call made for an unrelated grammar check can silently
/// advance the "current line" before an instruction is emitted) rather than a deliberate choice.
/// This file instead follows Luau's own model (<c>Compiler.cpp</c>'s
/// <c>setDebugLine</c>/<c>setDebugLineEnd</c>): every statement and expression stamps its own
/// recorded AST position explicitly at entry (<see cref="SetLine"/>, called from
/// <see cref="EmitStatement"/>/<see cref="EmitExpression"/>), and a handful of control-flow-only
/// instructions with no single corresponding source token get their own deliberate rule instead
/// of inheriting scanner-timing artifacts:
/// <list type="bullet">
/// <item>An if-statement's escape jump (skipping past an <c>else</c>/<c>elseif</c> once a
/// non-terminating branch finishes) is given no explicit stamp of its own -- like Luau
/// (<c>Compiler.cpp:3811-3813</c>), it simply inherits whatever line the branch's last statement
/// left, since there is no single token this jump corresponds to.</item>
/// <item>A <c>while</c> loop's back-edge jump is stamped with the line the body's last
/// instruction was emitted at -- matching Lua 5.1 (<c>lparser.c</c>'s <c>whilestat</c> emits the
/// back-jump after <c>block(ls)</c>, when the scanner's lastline is still the body's), and
/// observably so: it makes the back-edge re-report the body's line and thus fire no line-hook
/// event of its own, where stamping the condition's line instead fires one per iteration. See
/// <see cref="EmitWhileStatement"/>.</item>
/// <item>A numeric/generic <c>for</c> loop's back-edge (<c>ForLoop</c>/<c>TForCall</c>) is
/// stamped with the <c>for</c> statement's own starting line (matches Luau,
/// <c>Compiler.cpp:4329</c>).</item>
/// <item>A table constructor's implicit <c>NewTable</c>/final <c>SetList</c>, and a function's
/// (or the main chunk's) implicit trailing <c>Return</c>/<c>Closure</c>, are stamped with that
/// construct's own recorded closing position (<see cref="TableConstructorExpr.EndLine"/>,
/// <see cref="BlockStat.EndLine"/>) -- a real, meaningful AST position, not a scanner artifact.</item>
/// </list>
/// This was a deliberate choice, not an oversight: earlier attempts to instead replicate the
/// original compiler's exact (occasionally scanner-timing-accidental) line numbers for these
/// same instructions required modeling the scanner's token-lookahead-caching side effects
/// precisely, which proved both fragile (small changes elsewhere shifted unrelated lines) and,
/// on reflection, not actually more correct -- Luau's explicit model was adopted instead.
/// </summary>
static class CodeGenerator
{
    public static unsafe Prototype Generate(LuaState l, BlockStat mainBody, string name)
    {
        using var internPool = new StringInternPool(4);
        // A Parser instance is required purely as Function.cs's state container (P.ActiveVariables,
        // P.PendingGotos, P.ActiveLabels, P.Scanner.LastLine for line-info stamping) -- no
        // tokenizing ever happens; Next()/T/TestNext() are never called.
        using var p = Parser.Get(
            new()
            {
                R = new(null, 0),
                Current = InitialState,
                LineNumber = 1,
                LastLine = 1,
                LookAheadToken = new(0, TkEos),
                LookAheadToken2 = new(0, TkEos),
                L = l,
                Source = name,
                Buffer = new(0),
                StringPool = internPool,
            }
        );
        var f = Function.Get(p, PrototypeBuilder.Get(name));
        p.Function = f;
        f.Proto.IsVarArg = true;
        f.Proto.LineDefined = 0;

        // Mirrors Parser.MainFunction / Function.OpenMainFunction+CloseMainFunction exactly
        // (Function.cs:2301-2315).
        f.EnterBlock(false);
        f.MakeUpValue("_ENV", MakeExpression(Kind.Local, 0));
        EmitStatements(p, mainBody.Statements);
        SetLine(p, mainBody.EndLine);
        f.ReturnNone();
        f.LeaveBlock();
        Assert(f.Block == null);
        f.FoldJumps();
        f.FinalizeCaptures();

        return f.Proto.CreatePrototypeAndRelease();
    }

    static void SetLine(Parser p, int line)
    {
        p.Scanner.LastLine = line;
    }

    static void EmitStatements(Parser p, List<Stat> statements)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            EmitStatement(p, statements[i]);
            // Mirrors Statement()'s tail reset (Parser.cs:2004): every temporary register a
            // statement's expressions used is dead once the statement completes.
            p.Function.FreeRegisterCount = p.Function.ActiveVariableCount;
        }
    }

    static void EmitStatement(Parser p, Stat stat)
    {
        // Luau-style: every statement stamps its own recorded AST line at entry (mirrors
        // Compiler.cpp's compileStat calling setDebugLine(node) unconditionally), rather than
        // modeling the original compiler's scanner-lookahead-driven line drift.
        SetLine(p, stat.Line);

        switch (stat)
        {
            case BlockStat block:
                EmitBlockStatement(p, block);
                break;
            case LocalStat local:
                EmitLocalStatement(p, local);
                break;
            case ConstStat constStat:
                EmitConstStatement(p, constStat);
                break;
            case AssignmentStat assignment:
                EmitAssignmentStatement(p, assignment);
                break;
            case CompoundAssignmentStat compound:
                EmitCompoundAssignmentStatement(p, compound);
                break;
            case ExpressionStat exprStat:
                EmitExpressionStatement(p, exprStat);
                break;
            case ReturnStat ret:
                EmitReturnStatement(p, ret);
                break;
            case IfStat ifStat:
                EmitIfStatement(p, ifStat);
                break;
            case WhileStat whileStat:
                EmitWhileStatement(p, whileStat);
                break;
            case RepeatStat repeatStat:
                EmitRepeatStatement(p, repeatStat);
                break;
            case ForNumericStat forNumeric:
                EmitForNumericStatement(p, forNumeric);
                break;
            case ForGenericStat forGeneric:
                EmitForGenericStatement(p, forGeneric);
                break;
            case FunctionStat functionStat:
                EmitFunctionStatement(p, functionStat);
                break;
            case LocalFunctionStat localFunction:
                EmitLocalFunctionStatement(p, localFunction);
                break;
            case BreakStat:
                p.Function.MakeGoto("break", stat.Line, p.Function.Jump());
                break;
            case ContinueStat continueStat:
                EmitContinueStatement(p, continueStat);
                break;
            case GotoStat gotoStat:
                p.Function.MakeGoto(gotoStat.Name, stat.Line, p.Function.Jump());
                break;
            case LabelStat labelStat:
                EmitLabelStatement(p, labelStat);
                break;
            // A bare ';' emits nothing at all -- Statement's `case ';'` (Parser.cs:1900-1902) just
            // consumes it, notably without opening a block (unlike an empty `do end`).
            case EmptyStat:
                break;
            default:
                throw new InvalidOperationException($"unhandled statement kind {stat.GetType()}");
        }
    }

    // A bare `do ... end` block or an empty statement both surface as a BlockStat with no
    // dedicated node of their own (AstParser.cs Statement's ';' and TkDo cases).
    static void EmitBlockStatement(Parser p, BlockStat block)
    {
        if (block.Statements.Count == 0)
        {
            return;
        }

        p.Function.EnterBlock(false);
        EmitStatements(p, block.Statements);
        p.Function.LeaveBlock();
    }

    static void EmitLabelStatement(Parser p, LabelStat stat)
    {
        p.Function.CheckRepeatedLabel(stat.Name);
        var label = p.Function.MakeLabel(stat.Name, stat.Line);

        // Mirrors LabelStatement (Parser.cs:1408-1412): a label that is the last no-op statement
        // in its block assumes its locals are already out of scope, so it is re-levelled to the
        // enclosing block. Without this, a forward goto over a local declaration to a trailing
        // label is rejected as "jumps into the scope of local" (goto.lua:70).
        if (stat.IsLastNoOpStatement)
        {
            p.ActiveLabels[label].ActiveVariableCount = p.Function.Block.ActiveVariableCount;
        }

        p.Function.FindGotos(label);
    }

    static void EmitLocalStatement(Parser p, LocalStat stat)
    {
        var e = EmitAdjustedExpressionList(p, stat.Names.Count, stat.Initializers);
        foreach (var name in stat.Names)
        {
            p.Function.MakeLocalVariable(name);
        }

        p.Function.AdjustLocalVariables(stat.Names.Count);
        _ = e;
    }

    static void EmitConstStatement(Parser p, ConstStat stat)
    {
        var e = EmitAdjustedExpressionList(p, stat.Names.Count, stat.Initializers);
        var firstIndex = p.Function.ActiveVariableCount;
        foreach (var name in stat.Names)
        {
            p.Function.MakeLocalVariable(name);
        }

        p.Function.AdjustLocalVariables(stat.Names.Count);

        // Mirrors ConstStatement's literal-inlining capture (Parser.cs:1663-1673): only a
        // genuine single literal initializer is inlined, checked on the *parsed* (and possibly
        // constant-folded) expression, not the source syntax.
        var literal =
            stat.Names.Count == 1 && stat.Initializers.Count == 1 ? CaptureConstLiteral(e) : default;
        for (var i = 0; i < stat.Names.Count; i++)
        {
            p.Function.MarkConst(firstIndex + i, stat.Names.Count == 1 ? literal : default);
        }
    }

    static ConstBinding CaptureConstLiteral(ExprDesc e)
    {
        var binding = default(ConstBinding);
        switch (e.Kind)
        {
            case Kind.Nil:
                binding.Kind = Kind.Nil;
                break;
            case Kind.True:
            case Kind.False:
                binding.Kind = e.Kind;
                break;
            case Kind.Number:
                binding.Kind = Kind.Number;
                binding.Value = e.Value;
                binding.IntValue = e.IntValue;
                binding.IsInteger = e.IsInteger;
                break;
        }

        return binding;
    }

    /// <summary>
    /// Mirrors LocalStatement/ConstStatement's shared shape: evaluate the initializer list and
    /// call <see cref="Function.AdjustAssignment"/>, matching multi-return/vararg expansion and
    /// nil-padding exactly (<c>Function.cs:2031-2062</c>). Returns the *last* initializer's
    /// ExprDesc (post-adjustment) so <see cref="EmitConstStatement"/> can inspect it for literal
    /// inlining, matching <c>ConstStatement</c>'s use of the parsed expression.
    /// </summary>
    static ExprDesc EmitAdjustedExpressionList(Parser p, int variableCount, List<Expr> initializers)
    {
        if (initializers.Count == 0)
        {
            p.Function.AdjustAssignment(variableCount, 0, default);
            return default;
        }

        ExprDesc last = default;
        for (var i = 0; i < initializers.Count; i++)
        {
            if (i > 0)
            {
                p.Function.ExpressionToNextRegister(last);
            }

            last = EmitExpression(p, initializers[i]);
        }

        p.Function.AdjustAssignment(variableCount, initializers.Count, last);
        return last;
    }

    static void EmitAssignmentStatement(Parser p, AssignmentStat stat)
    {
        // Mirrors Assignment's per-comma CheckLimit (Parser.cs:1057). The original tests the
        // running target count as it recurses; testing the final count once here decides the same
        // way (the count only grows, and CheckLimit's message doesn't name the count), and
        // CheckLimit's `CallCount` term is the parse-recursion depth, which is ~0 during codegen
        // -- so this is exact at statement top level and marginally more permissive for a
        // near-200-target assignment nested several statements deep. Both compilers still reject
        // the case the official suite exercises (errors.lua's 201 targets).
        p.CheckLimit(stat.Targets.Count + p.Scanner.L.CallCount, MaxCallCount, "Go levels");

        var targetDescs = new List<ExprDesc>();
        foreach (var targetNode in stat.Targets)
        {
            var e = EmitAssignmentTarget(p, targetNode);

            // Mirrors Assignment's head check (Parser.cs:1048). This is what rejects
            // `const a = 1; a = 2`: SingleVariableHelper inlines a literal `const` initializer,
            // so the target resolves to a plain Kind.Number rather than a variable, and this
            // check -- not StoreVariable's const test, which can never see Kind.Local here --
            // is what raises the error.
            p.CheckCondition(e.IsVariable(), "syntax error");

            if (e.Kind != Kind.Indexed)
            {
                CheckConflicts(p.Function, targetDescs, e);
            }

            targetDescs.Add(e);
        }

        if (stat.Values.Count != targetDescs.Count)
        {
            var last = EmitAdjustedExpressionListRaw(p, targetDescs.Count, stat.Values, out var n);
            if (n > targetDescs.Count)
            {
                p.Function.FreeRegisterCount -= n - targetDescs.Count;
            }

            _ = last;
        }
        else
        {
            ExprDesc last = default;
            for (var i = 0; i < stat.Values.Count; i++)
            {
                if (i > 0)
                {
                    p.Function.ExpressionToNextRegister(last);
                }

                last = EmitExpression(p, stat.Values[i]);
            }

            p.Function.StoreVariable(targetDescs[^1], p.Function.SetReturn(last));
            for (var i = targetDescs.Count - 2; i >= 0; i--)
            {
                p.Function.StoreVariable(
                    targetDescs[i],
                    MakeExpression(Kind.NonRelocatable, p.Function.FreeRegisterCount - 1)
                );
            }

            return;
        }

        for (var i = targetDescs.Count - 1; i >= 0; i--)
        {
            p.Function.StoreVariable(
                targetDescs[i],
                MakeExpression(Kind.NonRelocatable, p.Function.FreeRegisterCount - 1)
            );
        }
    }

    /// <summary>Like <see cref="EmitAdjustedExpressionList"/> but also reports the raw
    /// expression count parsed, matching <c>Assignment</c>'s use of <c>ExpressionList</c>'s
    /// <c>n</c> (Parser.cs:1062-1070) to trim extra values off the register stack.</summary>
    static ExprDesc EmitAdjustedExpressionListRaw(Parser p, int variableCount, List<Expr> values, out int n)
    {
        n = values.Count;
        ExprDesc last = default;
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                p.Function.ExpressionToNextRegister(last);
            }

            last = EmitExpression(p, values[i]);
        }

        p.Function.AdjustAssignment(variableCount, values.Count, last);
        return last;
    }

    static void CheckConflicts(Function f, List<ExprDesc> priorTargets, ExprDesc e)
    {
        var extra = f.FreeRegisterCount;
        var conflict = false;
        for (var i = 0; i < priorTargets.Count; i++)
        {
            var d = priorTargets[i];
            if (d.Kind != Kind.Indexed)
            {
                continue;
            }

            if (d.TableType == e.Kind && d.Table == e.Info)
            {
                conflict = true;
                d.Table = extra;
                d.TableType = Kind.Local;
            }

            if (e.Kind == Kind.Local && d.Index == e.Info)
            {
                conflict = true;
                d.Index = extra;
            }

            priorTargets[i] = d;
        }

        if (conflict)
        {
            if (e.Kind == Kind.Local)
            {
                f.EncodeABC(OpCode.Move, extra, e.Info, 0);
            }
            else
            {
                f.EncodeABC(OpCode.GetUpVal, extra, e.Info, 0);
            }

            f.ReserveRegisters(1);
        }
    }

    /// <summary>Evaluates an assignment target (a <see cref="NameExpr"/> or
    /// <see cref="IndexExpr"/>) into the raw ExprDesc <see cref="Function.StoreVariable"/> needs,
    /// discharging an IndexExpr's object/key into registers exactly like <c>Function.Indexed</c>
    /// requires -- mirrors <c>FieldSelector</c>/<c>SuffixedExpression</c>'s <c>'['</c> case.</summary>
    static ExprDesc EmitAssignmentTarget(Parser p, Expr target)
    {
        return target switch
        {
            NameExpr name => ResolveNameExpr(p, name),
            IndexExpr index => EmitIndexForAssignment(p, index),
            _ => throw new InvalidOperationException($"invalid assignment target {target.GetType()}"),
        };
    }

    static ExprDesc EmitIndexForAssignment(Parser p, IndexExpr index)
    {
        var objectDesc = p.Function.ExpressionToAnyRegisterOrUpValue(EmitExpression(p, index.Object));
        var keyDesc = EmitExpression(p, index.Key);
        return p.Function.Indexed(objectDesc, keyDesc);
    }

    static void EmitCompoundAssignmentStatement(Parser p, CompoundAssignmentStat stat)
    {
        var target = EmitAssignmentTarget(p, stat.Target);

        // Mirrors CompoundAssignment's head check (Parser.cs:1845), and for the same reason as
        // EmitAssignmentStatement's above: `const a = 1; a += 2` resolves the target to the
        // inlined literal rather than a variable.
        p.CheckCondition(
            target.Kind is Kind.Local or Kind.UpValue or Kind.Indexed,
            "syntax error"
        );

        var current = p.Function.BeginCompoundAssignment(target, stat.Op);
        var value = EmitExpression(p, stat.Value);
        p.Function.EndCompoundAssignment(target, stat.Op, current, value, stat.Line);
    }

    static void EmitExpressionStatement(Parser p, ExpressionStat stat)
    {
        var e = EmitExpression(p, stat.Expression);
        Assert(e.Kind == Kind.Call);
        p.Function.Instruction(e).C = 1; // call statement uses no results
    }

    static void EmitReturnStatement(Parser p, ReturnStat stat)
    {
        if (stat.Values.Count == 0)
        {
            p.Function.ReturnNone();
            return;
        }

        ExprDesc last = default;
        for (var i = 0; i < stat.Values.Count; i++)
        {
            if (i > 0)
            {
                p.Function.ExpressionToNextRegister(last);
            }

            last = EmitExpression(p, stat.Values[i]);
        }

        p.Function.Return(last, stat.Values.Count);
    }

    static void EmitIfStatement(Parser p, IfStat stat)
    {
        var escapes = NoJump;
        for (var i = 0; i < stat.Branches.Count; i++)
        {
            escapes = EmitTestThenBlock(p, stat.Branches[i], escapes, isLast: i == stat.Branches.Count - 1 && stat.Else == null);
        }

        if (stat.Else != null)
        {
            EmitBlockStatement(p, stat.Else);
        }

        p.Function.PatchToHere(escapes);
    }

    /// <summary>Mirrors <c>TestThenBlock</c> (Parser.cs:1207-1243) minus the goto/break jump-
    /// fusion fast path (an optimization over the same observable bytecode shape once
    /// <see cref="EmitBlockStatement"/>'s generic body handles a leading break/goto normally;
    /// see the plan's note that no AST-level change was needed for this).</summary>
    static int EmitTestThenBlock(Parser p, IfBranch branch, int escapes, bool isLast)
    {
        var thenStatements = branch.Then.Statements;

        // Mirrors TestThenBlock's goto/break fast path (Parser.cs:1207-1243): `if cond then
        // goto X/break end` fuses the condition test directly into the goto/break's own jump
        // list (via GoIfFalse, inverted from the normal GoIfTrue) instead of emitting a separate
        // unconditional jump for it. Only a *leading* break/goto qualifies -- matches `T is
        // TkGoto or TkBreak` checked immediately after `then` in the original (this never
        // matches `continue`, a contextual keyword rather than a real token there).
        if (thenStatements.Count > 0 && IsBreakOrGoto(thenStatements[0], out var name, out var gotoLine))
        {
            var fastCondition = EmitExpression(p, branch.Condition);
            SetLine(p, branch.ThenLine);
            var e = p.Function.GoIfFalse(fastCondition);
            p.Function.EnterBlock(false);
            p.Function.MakeGoto(name, gotoLine, e.T);

            // Mirrors TestThenBlock's SkipEmptyStatements()+BlockFollow(false) (Parser.cs:1219-1224):
            // a trailing run of ';'/labels after the goto/break leaves nothing for the escape jump
            // to skip past, so none is emitted. A then-block always ends on 'end'/'else'/'elseif',
            // so unlike StatementList there is no 'until' case to exclude here.
            if (IsTrailingNoOpRun(thenStatements, 1))
            {
                p.Function.LeaveBlock();
                return escapes;
            }

            var jumpFalseFast = p.Function.Jump();
            EmitStatements(p, thenStatements.GetRange(1, thenStatements.Count - 1));
            p.Function.LeaveBlock();
            if (!isLast)
            {
                // Deliberately no explicit SetLine: mirrors Luau's if-escape jump
                // (Compiler.cpp:3811-3813), which inherits whatever line was last stamped by the
                // branch's tail rather than being assigned a line of its own -- there is no
                // single source token this jump corresponds to.
                escapes = p.Function.Concatenate(escapes, p.Function.Jump());
            }

            p.Function.PatchToHere(jumpFalseFast);
            return escapes;
        }

        var conditionExpr = EmitExpression(p, branch.Condition);
        // Stamped *after* the condition (whose own emission stamps its line) so it wins: the
        // branch's test/jump belongs to `then`, the line that opens the branch it selects. See
        // IfBranch.ThenLine.
        SetLine(p, branch.ThenLine);
        var condition = p.Function.GoIfTrue(conditionExpr);
        p.Function.EnterBlock(false);
        var jumpFalse = condition.F;
        EmitStatements(p, thenStatements);
        p.Function.LeaveBlock();
        if (!isLast)
        {
            escapes = p.Function.Concatenate(escapes, p.Function.Jump());
        }

        p.Function.PatchToHere(jumpFalse);
        return escapes;
    }

    /// <summary>True when every statement from <paramref name="start"/> on is a skippable no-op
    /// (<c>;</c> or a label) -- the AST-side equivalent of <c>SkipEmptyStatements()</c> reaching the
    /// end of a block.</summary>
    static bool IsTrailingNoOpRun(List<Stat> statements, int start)
    {
        for (var i = start; i < statements.Count; i++)
        {
            if (statements[i] is not (EmptyStat or LabelStat))
            {
                return false;
            }
        }

        return true;
    }

    static bool IsBreakOrGoto(Stat stat, out string name, out int line)
    {
        switch (stat)
        {
            case BreakStat b:
                name = "break";
                line = b.Line;
                return true;
            case GotoStat g:
                name = g.Name;
                line = g.Line;
                return true;
            default:
                name = "";
                line = 0;
                return false;
        }
    }

    static void EmitWhileStatement(Parser p, WhileStat stat)
    {
        var top = p.Function.Label();
        var conditionExit = p.Function.GoIfTrue(EmitExpression(p, stat.Condition)).F;
        p.Function.EnterBlock(true);
        EmitBlockBody(p, stat.Body);

        // The back-edge is stamped with the line the body's last instruction was emitted at, not
        // with the condition's. That is what Lua 5.1 produces: in lparser.c's `whilestat` the
        // `luaK_patchlist(fs, luaK_jump(fs), whileinit)` runs after `block(ls)`, at which point
        // the scanner's lastline is still the body's (`end` has only been peeked). It also
        // matters observably, not just cosmetically: stamping the condition's line instead makes
        // every iteration's back-edge report a *new* line-hook event, so a multi-line while body
        // traces one extra line per iteration (tests-lua/db.lua's `while a<=3 do` case expects
        // {1,2,3,4,3,4,3,4,3,5}, which only holds if the back-edge re-reports the body's line and
        // so fires no event at all). The numeric/generic `for` back-edge is different -- Lua 5.1
        // stamps that with the `for` statement's own line, and it does fire per iteration; see
        // EmitForNumericStatement.
        var bodyEndLine = p.Scanner.LastLine;
        p.Function.ContinueLabel(p.Function.Block.ActiveVariableCount);
        SetLine(p, bodyEndLine);
        p.Function.JumpTo(top);
        p.Function.LeaveBlock();
        p.Function.PatchToHere(conditionExit);
    }

    /// <summary>Emits a loop/if body's statements directly inside an already-open
    /// <see cref="Function.Block"/>, matching how <c>Block()</c> (Parser.cs:1262-1267) is used
    /// from inside a construct that opened its own outer block first (While/For's loop block).</summary>
    static void EmitBlockBody(Parser p, BlockStat body)
    {
        p.Function.EnterBlock(false);
        EmitStatements(p, body.Statements);
        p.Function.LeaveBlock();
    }

    /// <summary>
    /// Mirrors <c>ContinueStatement</c> (Parser.cs:1362-1382). The <see cref="Parser.Loops"/>
    /// record is not about emission at all: it feeds the "repeat..until condition reads a local
    /// a `continue` jumped over" diagnostic in <see cref="Function.SingleVariableHelper"/>
    /// (Function.cs:2143-2155), which is armed by <see cref="EmitRepeatStatement"/> below. A
    /// `continue` outside any loop is reported by the goto machinery instead
    /// (<see cref="Function.UndefinedGotoError"/>), so the empty-stack case needs no check here.
    /// </summary>
    static void EmitContinueStatement(Parser p, ContinueStat stat)
    {
        var index = p.Loops.Length - 1;
        if (index >= 0)
        {
            var context = p.Loops[index];
            if (
                context.MinContinueLevel < 0
                || p.Function.ActiveVariableCount < context.MinContinueLevel
            )
            {
                context.MinContinueLevel = p.Function.ActiveVariableCount;
                context.MinContinueLine = stat.Line;
                p.Loops[index] = context;
            }
        }

        p.Function.MakeGoto("continue", stat.Line, p.Function.Jump());
    }

    static void EmitRepeatStatement(Parser p, RepeatStat stat)
    {
        var top = p.Function.Label();
        p.Function.EnterBlock(true); // loop block
        p.Function.EnterBlock(false); // scope block
        p.Loops.Add(new() { IsRepeat = true, MinContinueLevel = -1 });
        EmitStatements(p, stat.Body.Statements);

        var loop = p.Loops[p.Loops.Length - 1];
        p.Loops.Shrink(p.Loops.Length - 1);

        var bodyLevel = p.Function.Block.ActiveVariableCount;
        p.Function.ContinueLabel(bodyLevel);

        // The condition is evaluated while the scope block (the body's locals) is still open --
        // see RepeatStat's doc comment. AstParser already built stat.Condition this way. The
        // saved/armed/restored RepeatCondition* window mirrors RepeatStatement
        // (Parser.cs:1305-1318): `continue` targets the condition, so a local the continue
        // jumped over is undefined there, and SingleVariableHelper rejects reading one.
        var savedFunction = p.RepeatConditionFunction;
        var savedLevel = p.RepeatConditionMinLevel;
        var savedLine = p.RepeatConditionLine;
        if (loop.MinContinueLevel >= 0)
        {
            p.RepeatConditionFunction = p.Function;
            p.RepeatConditionMinLevel = loop.MinContinueLevel;
            p.RepeatConditionLine = loop.MinContinueLine;
        }

        var conditionExit = p.Function.GoIfTrue(EmitExpression(p, stat.Condition)).F;

        p.RepeatConditionFunction = savedFunction;
        p.RepeatConditionMinLevel = savedLevel;
        p.RepeatConditionLine = savedLine;

        if (p.Function.Block.HasUpValue)
        {
            p.Function.PatchClose(conditionExit, p.Function.Block.ActiveVariableCount);
        }

        p.Function.LeaveBlock(); // finish scope
        p.Function.PatchList(conditionExit, top); // close loop
        p.Function.LeaveBlock(); // finish loop
    }

    static void EmitForNumericStatement(Parser p, ForNumericStat stat)
    {
        p.Function.EnterBlock(true);
        var @base = p.Function.FreeRegisterCount;
        p.Function.MakeLocalVariable("(for index)");
        p.Function.MakeLocalVariable("(for limit)");
        p.Function.MakeLocalVariable("(for step)");
        p.Function.MakeLocalVariable(stat.Name);

        EmitForControlExpr(p, stat.Start);
        EmitForControlExpr(p, stat.Stop);
        if (stat.Step != null)
        {
            EmitForControlExpr(p, stat.Step);
        }
        else
        {
            p.Function.EncodeConstant(p.Function.FreeRegisterCount, p.Function.LongConstant(1));
            p.Function.ReserveRegisters(1);
        }

        EmitForBody(p, @base, stat.Line, 1, isNumeric: true, stat.Body);
        p.Function.LeaveBlock();
    }

    static void EmitForControlExpr(Parser p, Expr expr)
    {
        var e = p.Function.ExpressionToNextRegister(EmitExpression(p, expr));
        Assert(e.Kind == Kind.NonRelocatable);
    }

    static void EmitForGenericStatement(Parser p, ForGenericStat stat)
    {
        p.Function.EnterBlock(true);
        var n = 4 + (stat.Names.Count - 1);
        var @base = p.Function.FreeRegisterCount;
        p.Function.MakeLocalVariable("(for generator)");
        p.Function.MakeLocalVariable("(for state)");
        p.Function.MakeLocalVariable("(for control)");
        foreach (var name in stat.Names)
        {
            p.Function.MakeLocalVariable(name);
        }

        // The expression list's own line, not the `for` statement's -- see
        // ForGenericStat.ExpressionLine. Used for both the iterator call below and ForBody's
        // loop back-edge, exactly as ForStatement uses its single captured `line`.
        var line = stat.ExpressionLine;
        if (stat.Expressions.Count == 1 && !HasMultipleReturnsShape(stat.Expressions[0]))
        {
            // Luau generalized iteration (ForList, Parser.cs:1149-1166): a single non-call/
            // vararg expression resolves via an internal helper into (iterator, state, control).
            var e = EmitExpression(p, stat.Expressions[0]);
            p.Function.ExpressionToNextRegister(e);
            p.Function.ReserveRegisters(1);
            p.Function.EncodeABC(OpCode.Move, @base + 1, @base, 0);
            p.Function.LoadBuiltin(@base, LuaBuiltin.IterResolver);
            var call = p.Function.EncodeABC(OpCode.Call, @base, 2, 4);
            p.Function.FixLine(line);
            p.Function.FreeRegisterCount = @base + 1;
            p.Function.AdjustAssignment(3, 1, MakeExpression(Kind.Call, call));
        }
        else
        {
            var last = EmitAdjustedExpressionListRaw(p, 3, stat.Expressions, out _);
            _ = last;
        }

        p.Function.CheckStack(3);
        EmitForBody(p, @base, line, n - 3, isNumeric: false, stat.Body);
        p.Function.LeaveBlock();
    }

    /// <summary>An expression AST node "has multiple returns" for ForList's purposes exactly
    /// when it's a call or vararg -- mirrors <see cref="ExprDesc.HasMultipleReturns"/>'s Kind
    /// check (Declarements.cs:160-163), but on the AST shape instead of a discharged ExprDesc.</summary>
    static bool HasMultipleReturnsShape(Expr e) => e is CallExpr or MethodCallExpr or VarArgExpr;

    static void EmitForBody(Parser p, int @base, int line, int n, bool isNumeric, BlockStat body)
    {
        p.Function.AdjustLocalVariables(3);
        var prep = p.Function.OpenForBody(@base, n, isNumeric);
        EmitBlockBody(p, body);
        // Mirrors Luau's numeric/generic for-loop back-edge convention (Compiler.cpp:4329):
        // stamped with the for-statement's own start line.
        SetLine(p, line);
        p.Function.CloseForBody(prep, @base, line, n, isNumeric);
    }

    static void EmitFunctionStatement(Parser p, FunctionStat stat)
    {
        var target = EmitAssignmentTarget(p, stat.Target);
        var functionValue = EmitFunctionExpr(p, stat.Function);
        p.Function.StoreVariable(target, functionValue);
        p.Function.FixLine(stat.Line);
    }

    static void EmitLocalFunctionStatement(Parser p, LocalFunctionStat stat)
    {
        p.Function.MakeLocalVariable(stat.Name);
        p.Function.AdjustLocalVariables(1);
        p.Function.MarkLocalWritten(p.Function.ActiveVariableCount - 1);
        var localIndex = p.Function.ActiveVariableCount - 1;
        var functionValue = EmitFunctionExpr(p, stat.Function);
        p.Function.LocalVariable(functionValue.Info).StartPc = p.Function.Proto.CodeList.Length;
        if (stat.IsConst)
        {
            p.Function.MarkConst(localIndex, default);
        }
    }

    // ---- Expressions ----

    static ExprDesc EmitExpression(Parser p, Expr expr)
    {
        // Luau-style: every expression stamps its own recorded AST line at entry (mirrors
        // Compiler.cpp's setDebugLine(node) convention), rather than relying on whatever the
        // original single-pass compiler's scanner happened to leave ambient at each point.
        SetLine(p, expr.Line);

        return expr switch
        {
            NilExpr => MakeExpression(Kind.Nil, 0),
            TrueExpr => MakeExpression(Kind.True, 0),
            FalseExpr => MakeExpression(Kind.False, 0),
            VarArgExpr => MakeExpression(
                Kind.VarArg,
                p.Function.EncodeABC(OpCode.VarArg, 0, 1, 0)
            ),
            NumberExpr number => EmitNumber(number),
            StringExpr str => p.Function.EncodeString(str.Value),
            NameExpr name => ResolveNameExpr(p, name),
            IndexExpr index => EmitIndex(p, index),
            CallExpr call => EmitCall(p, call),
            MethodCallExpr methodCall => EmitMethodCall(p, methodCall),
            BinaryExpr binary => EmitBinary(p, binary),
            UnaryExpr unary => EmitUnary(p, unary),
            AndOrExpr andOr => EmitAndOr(p, andOr),
            FunctionExpr function => EmitFunctionExpr(p, function),
            TableConstructorExpr table => EmitTableConstructor(p, table),
            ParenExpr paren => EmitParen(p, paren),
            IfElseExpr ifElse => EmitIfElseExpr(p, ifElse),
            InterpolatedStringExpr interp => EmitInterpolatedString(p, interp),
            _ => throw new InvalidOperationException($"unhandled expression kind {expr.GetType()}"),
        };
    }

    static ExprDesc EmitNumber(NumberExpr number)
    {
        var e = MakeExpression(Kind.Number, 0);
        e.Value = number.Value;
        e.IntValue = number.IntValue;
        e.IsInteger = number.IsInteger;
        return e;
    }

    static ExprDesc ResolveNameExpr(Parser p, NameExpr name)
    {
        var (e, found) = SingleVariableHelper(p.Function, name.Name, true);
        if (found)
        {
            return e;
        }

        // Global: not found as local/upvalue -- resolve _ENV and wrap, exactly like
        // Function.SingleVariable (Function.cs:2190-2198). AstParser guarantees every bare name
        // that isn't a local/upvalue was built as an IndexExpr against _ENV, never a bare
        // NameExpr, so this path only exists for completeness/defensiveness.
        var (envExpr, envFound) = SingleVariableHelper(p.Function, "_ENV", true);
        Assert(envFound && (envExpr.Kind == Kind.Local || envExpr.Kind == Kind.UpValue));
        return p.Function.Indexed(envExpr, p.Function.EncodeString(name.Name));
    }

    static ExprDesc EmitIndex(Parser p, IndexExpr index)
    {
        if (TryEmitGetImport(p, index, out var fused))
        {
            return fused;
        }

        var objectDesc = p.Function.ExpressionToAnyRegisterOrUpValue(EmitExpression(p, index.Object));
        var keyDesc = EmitExpression(p, index.Key);
        return p.Function.Indexed(objectDesc, keyDesc);
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 3: fuses the two-level chain <c>up.name1.name2</c> -- the
    /// shape <c>math.floor</c>, <c>string.format</c> and <c>os.time</c> all have, since a global
    /// name is <c>_ENV</c>-rooted and <c>_ENV</c> is this closure's upvalue 0 -- into one
    /// <see cref="OpCode.GetImport"/> dispatch instead of the ordinary
    /// <see cref="OpCode.GetTabUp"/> + <see cref="OpCode.GetTable"/> pair.
    ///
    /// Returns false for anything it cannot fuse *exactly*, in which case the caller emits the
    /// unfused pair and the result is bit-for-bit what it was before this optimization existed:
    /// <list type="bullet">
    /// <item>a non-dotted key anywhere in the chain (<c>math["floor"]</c>) or a non-string key, so
    /// only genuine `name.name` sugar qualifies -- matching Luau, whose GETIMPORT is likewise built
    /// from the field-name form;</item>
    /// <item>a base that isn't an upvalue (<c>local m = math; m.floor</c>, or any local-rooted
    /// chain), because B is GetTabUp's upvalue operand and a local would need a different read
    /// entirely;</item>
    /// <item>a chain longer than two levels (<c>a.b.c.d</c>): this fuses the innermost two and
    /// leaves the rest to the ordinary per-level path, so <c>a.b.c</c> becomes one GetImport plus
    /// one GetTable;</item>
    /// <item>either key's constant landing past <see cref="Instruction.MaxIndexRK"/>, since C
    /// encodes key1 as an RK operand and the VM's deopt rewrite re-encodes key2 as one.</item>
    /// </list>
    ///
    /// <see cref="NameExpr.RefKind.UpValue"/> is only a cheap filter -- the base is re-resolved
    /// here through <see cref="Function.SingleVariableHelper"/> like every other name in this file,
    /// so the upvalue index handed to the VM is the one this pass's bookkeeping believes in.
    /// </summary>
    static bool TryEmitGetImport(Parser p, IndexExpr index, out ExprDesc result)
    {
        result = default;

        if (
            !index.IsDotSugar
            || index.Key is not StringExpr key2
            || index.Object is not IndexExpr inner
            || !inner.IsDotSugar
            || inner.Key is not StringExpr key1
            || inner.Object is not NameExpr baseName
            || baseName.Kind != NameExpr.RefKind.UpValue
        )
        {
            return false;
        }

        var (baseDesc, found) = SingleVariableHelper(p.Function, baseName.Name, true);
        if (!found || baseDesc.Kind != Kind.UpValue)
        {
            return false;
        }

        // Both keys are interned here, before anything is emitted. That is not just convenient:
        // it is what keeps the constant pool in the order the unfused path produces (key1, then
        // key2, with nothing in between -- the unfused pair's own interning points are exactly
        // these two steps), so bailing out below still yields identical bytecode.
        var key1Constant = p.Function.StringConstant(key1.Value);
        var key2Constant = p.Function.StringConstant(key2.Value);
        if (key1Constant > Instruction.MaxIndexRK || key2Constant > Instruction.MaxIndexRK)
        {
            return false;
        }

        var function = p.Function;

        // The fused instruction stands in for both levels, so its line is the line of the
        // expression that produces the value -- index.Line, the same line the unfused path stamps
        // its final GetTable with. (EmitExpression already set it at entry; restating it keeps
        // this independent of what the resolution and constant-pool calls above do to LastLine.)
        // The unfused pair stamps its first level with inner.Line instead, so a chain written
        // across two lines reports the *inner* line for a level-1 failure here -- unavoidable
        // when two instructions become one, and invisible for the single-line chains this targets.
        SetLine(p, index.Line);

        // A = 0, unbound, exactly like DischargeVariables's GetTabUp/GetTable branches: level 1's
        // result, level 2's table and level 2's result are all this one register, and the consumer
        // binds it via DischargeToRegister. FreeRegisterCount is left untouched to match -- the
        // unfused pair frees the level-1 register when it discharges the outer index, so both
        // paths end a chain holding the same number of registers.
        var pc = function.GetImport(baseDesc.Info, key1Constant, key2Constant);
        result = MakeExpression(Kind.Relocatable, pc);
        return true;
    }

    static ExprDesc EmitCall(Parser p, CallExpr call)
    {
        var callee = p.Function.ExpressionToNextRegister(EmitExpression(p, call.Callee));
        return EmitCallArguments(p, callee, call.Arguments, call.Line);
    }

    static ExprDesc EmitMethodCall(Parser p, MethodCallExpr methodCall)
    {
        var receiver = EmitExpression(p, methodCall.Receiver);
        var selfExpr = p.Function.Self(
            receiver,
            new ExprDesc
            {
                Kind = Kind.Constant,
                Info = p.Function.StringConstant(methodCall.MethodName),
                T = NoJump,
                F = NoJump,
            }
        );
        return EmitCallArguments(p, selfExpr, methodCall.Arguments, methodCall.Line);
    }

    /// <summary>Mirrors <c>FunctionArguments</c> (Parser.cs:249-298): <paramref name="callee"/>
    /// must already be in a register (the call base) -- for a method call it's the base
    /// <see cref="Function.Self"/> already reserved (function + self).</summary>
    static ExprDesc EmitCallArguments(Parser p, ExprDesc callee, List<Expr> arguments, int line)
    {
        var @base = callee.Info;
        var parameterCount = MultipleReturns;
        if (arguments.Count > 0)
        {
            ExprDesc last = default;
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                {
                    p.Function.ExpressionToNextRegister(last);
                }

                last = EmitExpression(p, arguments[i]);
            }

            if (HasMultipleReturnsShape2(last))
            {
                p.Function.SetMultipleReturns(last);
            }
            else
            {
                p.Function.ExpressionToNextRegister(last);
                parameterCount = p.Function.FreeRegisterCount - (@base + 1);
            }
        }
        else
        {
            // Mirrors FunctionArguments' Kind.Void case (Parser.cs:280-289) exactly: no register
            // is reserved for the (absent) argument list, so parameterCount is derived from
            // whatever's *already* sitting above the call base -- which is 0 for a plain call,
            // but 1 for a method call, since Self (Function.cs:1499-1510) already reserved a
            // register for `self` before this ever runs. Hardcoding 0 here undercounts self.
            parameterCount = p.Function.FreeRegisterCount - (@base + 1);
        }

        var e = MakeExpression(
            Kind.Call,
            p.Function.EncodeABC(OpCode.Call, @base, parameterCount + 1, 2)
        );
        p.Function.FixLine(line);
        p.Function.FreeRegisterCount = @base + 1;
        return e;
    }

    static bool HasMultipleReturnsShape2(ExprDesc e) => e.Kind is Kind.Call or Kind.VarArg;

    static ExprDesc EmitBinary(Parser p, BinaryExpr binary)
    {
        var leftValue = EmitExpression(p, binary.Left);
        // Infix's discharge of the left operand happens *after* the operator token is consumed
        // in the original (SubExpression's `line = Scanner.LineNumber; Next();` before calling
        // Function.Infix, Parser.cs:657-659) -- binary.Line is the operator's own line, captured
        // the same way. Evaluating the left operand above may have moved the ambient line to
        // its own position, so this must be reset before Infix runs.
        SetLine(p, binary.Line);
        var left = p.Function.Infix(MapInfixOp(binary.Op), leftValue);
        var right = EmitExpression(p, binary.Right);
        return p.Function.Postfix(binary.Op, left, right, binary.Line);
    }

    /// <summary>Mirrors <c>Infix</c>'s dispatch (Function.cs:1843-1875): only <c>and</c>/<c>or</c>
    /// get special pre-right-operand treatment there; every other binary op falls to its
    /// default (discharge-to-value) case. <see cref="AndOrExpr"/> handles and/or itself, so a
    /// plain <see cref="BinaryExpr"/> never carries <c>OprAnd</c>/<c>OprOr</c>.</summary>
    static int MapInfixOp(int op) => op;

    static ExprDesc EmitUnary(Parser p, UnaryExpr unary)
    {
        var operand = EmitExpression(p, unary.Operand);
        return p.Function.Prefix(unary.Op, operand, unary.Line);
    }

    static ExprDesc EmitAndOr(Parser p, AndOrExpr andOr)
    {
        var left = EmitExpression(p, andOr.Left);
        // See EmitBinary's matching comment -- andOr.Line is the and/or operator's own line.
        SetLine(p, andOr.Line);
        left = p.Function.Infix(andOr.IsAnd ? OprAnd : OprOr, left);
        var right = EmitExpression(p, andOr.Right);
        return p.Function.Postfix(andOr.IsAnd ? OprAnd : OprOr, left, right, andOr.Line);
    }

    static ExprDesc EmitParen(Parser p, ParenExpr paren)
    {
        return p.Function.DischargeVariables(EmitExpression(p, paren.Inner));
    }

    static ExprDesc EmitIfElseExpr(Parser p, IfElseExpr ifElse)
    {
        var target = p.Function.FreeRegisterCount;
        p.Function.ReserveRegisters(1);
        var endJumps = NoJump;

        foreach (var branch in ifElse.Branches)
        {
            var condition = p.Function.GoIfTrue(EmitExpression(p, branch.Condition));
            p.Function.ExpressionToRegister(EmitExpression(p, branch.Value), target);
            endJumps = p.Function.Concatenate(endJumps, p.Function.Jump());
            p.Function.PatchToHere(condition.F);
            p.Function.FreeRegisterCount = target + 1;
        }

        p.Function.ExpressionToRegister(EmitExpression(p, ifElse.Else), target);
        p.Function.PatchToHere(endJumps);
        p.Function.FreeRegisterCount = target + 1;
        return MakeExpression(Kind.NonRelocatable, target);
    }

    static ExprDesc EmitInterpolatedString(Parser p, InterpolatedStringExpr interp)
    {
        var @base = p.Function.FreeRegisterCount;
        p.Function.ReserveRegisters(1);
        p.Function.LoadBuiltin(@base, LuaBuiltin.InterpolationBuilder);
        var argumentCount = 0;
        foreach (var part in interp.Parts)
        {
            p.Function.ExpressionToNextRegister(EmitExpression(p, part));
            argumentCount++;
        }

        var call = p.Function.EncodeABC(OpCode.Call, @base, argumentCount + 1, 2);
        p.Function.FixLine(interp.Line);
        p.Function.FreeRegisterCount = @base + 1;
        return MakeExpression(Kind.Call, call);
    }

    static ExprDesc EmitFunctionExpr(Parser p, FunctionExpr function)
    {
        p.Function.OpenFunction(function.LineDefined);
        if (function.Name != null)
        {
            p.Function.Proto.Name = function.Name;
        }

        p.Function.Proto.IsVarArg = function.IsVarArg;
        foreach (var param in function.Parameters)
        {
            p.Function.MakeLocalVariable(param);
        }

        p.Function.AdjustLocalVariables(function.Parameters.Count);
        p.Function.Proto.ParameterCount = p.Function.ActiveVariableCount;
        p.Function.ReserveRegisters(p.Function.ActiveVariableCount);

        // A `continue` in the body may not target a loop of the enclosing function (Body,
        // Parser.cs:1497-1500). Defensive rather than load-bearing: such a `continue` has no
        // matching label in this prototype and is already rejected by
        // Function.UndefinedGotoError, but the loop stack is truncated here all the same.
        var savedLoopCount = p.Loops.Length;
        EmitStatements(p, function.Body.Statements);
        p.Loops.Shrink(savedLoopCount);

        p.Function.Proto.LastLineDefined = function.LastLineDefined;

        // From here on the ambient "current line" is the function's own closing 'end' --
        // matches Body()'s Scanner.CheckMatch(TkEnd, ...) consuming it before CloseFunction()
        // runs, which affects both the CLOSURE instruction emitted into the *parent* function
        // below and this function's own implicit Return.
        SetLine(p, function.Body.EndLine);

        var closureExpr = p.Function.Previous!.ExpressionToNextRegister(
            MakeExpression(
                Kind.Relocatable,
                p.Function.Previous!.EncodeABx(
                    OpCode.Closure,
                    0,
                    p.Function.Previous!.Proto.PrototypeList.Length - 1
                )
            )
        );

        p.Function.ReturnNone();
        p.Function.LeaveBlock();
        Assert(p.Function.Block == null);
        var f = p.Function;
        f.FoldJumps();
        f.FinalizeCaptures();
        p.Function = f.Previous!;
        f.Release();

        return closureExpr;
    }

    /// <summary>
    /// Mirrors <c>Constructor</c>/<c>Field</c> (Parser.cs:192-247) exactly, including the
    /// one-step-delayed array-value flush: a parsed array-position value is *not* discharged to
    /// a register immediately -- it's carried as the pending <c>e</c> and only flushed (via
    /// <see cref="Function.FlushToConstructor"/>, which also periodically batches a
    /// <c>SetList</c> every <c>ListItemsPerFlush</c> items) right before the *next* field is
    /// processed, or at the very end via <see cref="Function.CloseConstructor"/>. Getting this
    /// timing wrong changes SetList batching/positions for literals with more than
    /// ListItemsPerFlush array elements.
    /// </summary>
    static ExprDesc EmitTableConstructor(Parser p, TableConstructorExpr table)
    {
        // Milestone 4: an all-constant constructor becomes one DupTable copy of a template built
        // here, at compile time, instead of a NewTable plus a SetTable/LoadK per field. See
        // TryEmitDupTable -- it either fuses, or returns false having emitted and interned nothing.
        if (TryEmitDupTable(p, table, out var fused))
        {
            return fused;
        }

        var pc = p.Function.EncodeABC(OpCode.NewTable, 0, 0, 0);
        var t = p.Function.ExpressionToNextRegister(MakeExpression(Kind.Relocatable, pc));
        var tableRegister = t.Info;

        var arrayCount = 0;
        var hashCount = 0;
        var pending = 0;
        ExprDesc e = default; // Kind.Void

        for (var i = 0; i < table.Fields.Count; i++)
        {
            if (i > 0 && e.Kind != Kind.Void)
            {
                pending = p.Function.FlushToConstructor(tableRegister, pending, arrayCount, e);
                e.Kind = Kind.Void;
            }

            var field = table.Fields[i];
            var freeRegisterCount = p.Function.FreeRegisterCount;
            if (field.Key != null)
            {
                hashCount++;
                var keyDesc = EmitExpression(p, field.Key);
                p.Function.FlushFieldToConstructor(
                    tableRegister,
                    freeRegisterCount,
                    keyDesc,
                    () => EmitExpression(p, field.Value)
                );
            }
            else
            {
                arrayCount++;
                pending++;
                e = EmitExpression(p, field.Value);
            }
        }

        // CloseConstructor runs after '}' is consumed in the original (Parser.cs:244-245), so
        // both its internal discharge of a still-pending last value and its SetList use the
        // post-'}' line, not the last field's own.
        SetLine(p, table.EndLine);
        p.Function.CloseConstructor(pc, tableRegister, pending, arrayCount, hashCount, e);
        return t;
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: fuses an all-constant table constructor into a single
    /// <see cref="OpCode.DupTable"/> that copies a template built here, at compile time.
    ///
    /// Returns false -- having emitted nothing, reserved no register and interned no constant, so
    /// the caller's per-field path is bit-for-bit what it was before this optimization existed --
    /// unless every field qualifies. A field qualifies when its value folds to a constant
    /// (<see cref="TryFoldToConstant"/>) and, for a keyed field, when its key folds to a *string*.
    /// A table-valued value never qualifies, literal (<c>{ x = {} }</c>) or otherwise: a folded one
    /// would be a single shared table aliased into every clone the instruction makes.
    /// </summary>
    static bool TryEmitDupTable(Parser p, TableConstructorExpr table, out ExprDesc result)
    {
        result = default;

        // One DupTable is two words (the instruction plus the ExtraArg its template index rides in),
        // so a literal whose unfused form is a NewTable plus one SetTable is a wash on instruction
        // count and a loss on work done at runtime: below two fields there is nothing to win.
        if (table.Fields.Count < 2)
        {
            return false;
        }

        var arrayCount = 0;
        var hashCount = 0;
        for (var i = 0; i < table.Fields.Count; i++)
        {
            var field = table.Fields[i];
            if (field.Key is null)
            {
                arrayCount++;
            }
            else
            {
                // The key must fold, and -- deliberately -- to a *string*, not merely to some
                // constant. A foldable non-string key ({ [1] = x }, { [true] = x }) could be fused
                // just as safely as a string one, but an integer key interacts with the array part
                // through GrowArray's migration and no corpus literal has one, so those take the
                // proven path. A computed key ({ [k] = v }) is not foldable at all and lands here
                // too.
                if (!TryFoldToConstant(field.Key, out var key) || key.Type is not LuaValueType.String)
                {
                    return false;
                }

                hashCount++;
            }

            if (!TryFoldToConstant(field.Value, out _))
            {
                return false;
            }
        }

        var template = BuildTemplate(table, arrayCount, hashCount);

        // The fused instruction stands in for the whole constructor, so it is stamped with the
        // constructor's own recorded position -- the opening '{' -- rather than the closing one the
        // unfused path's trailing SetList uses, since there is no trailing instruction any more.
        // (EmitExpression already set that line at entry; restating it keeps this independent of
        // what BuildTemplate does to LastLine.)
        SetLine(p, table.Line);
        var pc = p.Function.DupTable(template);

        // One register, reserved exactly the way NewTable's result reserves it, and
        // FreeRegisterCount left at tableRegister + 1 -- which is where the unfused path ends up
        // too, either through SetList (Function.cs:2046) or, for an all-keyed constructor, by never
        // growing past the table register at all (FlushFieldToConstructor restores it). So whatever
        // consumes the constructor sees identical register state on both paths.
        result = p.Function.ExpressionToNextRegister(MakeExpression(Kind.Relocatable, pc));
        return true;
    }

    /// <summary>
    /// Builds the all-constant template <see cref="TryEmitDupTable"/> hands to
    /// <see cref="Function.DupTable"/>, by replaying the field list with the primitives the runtime
    /// path uses, so that a copy of it is indistinguishable from the table the unfused path would
    /// have built.
    ///
    /// Sizes come from the same array/hash counts NewTable's B/C hints carry -- the counts
    /// CloseConstructor patches those hints to -- so the capacities match; and the
    /// <see cref="LuaTable.EnsureArrayCapacity"/> below mirrors SetList's own call against a
    /// hint-sized array (a no-op past a count of 1, and for 0 or 1 the table constructor leaves the
    /// array empty, so the array part grows to 8 exactly as the first SetList would grow it).
    ///
    /// Array-slot and keyed writes can be laid down in one pass in source order without emulating
    /// SetList's per-field flushes, because TryEmitDupTable guarantees every key is a *string*: a
    /// string key always goes to the string dictionary and never reaches the array part, so the two
    /// kinds of write cannot interact -- which is the property that also rules out an integer key's
    /// GrowArray migration reordering anything.
    /// </summary>
    static LuaTable BuildTemplate(TableConstructorExpr table, int arrayCount, int hashCount)
    {
        var template = new LuaTable(arrayCount, hashCount);
        template.EnsureArrayCapacity(arrayCount);
        var arraySpan = template.GetArraySpan();

        var arrayIndex = 0;
        for (var i = 0; i < table.Fields.Count; i++)
        {
            var field = table.Fields[i];
            // The folds are re-run rather than carried over from the predicate, because they are
            // pure: same node, same value, no compiler state touched. The asserts record that this
            // is a re-run of an already-successful check, not a second chance to fail silently
            // into a default value.
            var foldedValue = TryFoldToConstant(field.Value, out var value);
            Assert(foldedValue);
            if (field.Key is null)
            {
                arraySpan[arrayIndex++] = value;
            }
            else
            {
                // The indexer, not a dictionary insert: it is the same entry point
                // SetTableValueSlowPath uses for a table with no metatable and no live entry
                // (LuaVirtualMachine.cs:3016), so the key lands in whichever structure the runtime
                // would have put it in, at the same capacity policy.
                var foldedKey = TryFoldToConstant(field.Key, out var key);
                Assert(foldedKey && key.Type is LuaValueType.String);
                template[key] = value;
            }
        }

        return template;
    }

    /// <summary>
    /// Milestone 4's fold: the compile-time value of an expression, or false for anything that is
    /// not a literal. No emission, no name resolution and no constant interning -- so calling it
    /// speculatively, as <see cref="TryEmitDupTable"/> does, cannot change any compiler state, which
    /// is what makes falling back to the unfused path free.
    ///
    /// Number literals are read straight off the node's own <c>Value</c>/<c>IntValue</c>/<c>IsInteger</c>
    /// fields, the same three <see cref="EmitNumber"/> copies into an <c>ExprDesc</c>, so a folded
    /// number is exactly the constant the unfused path would intern -- integer-ness included, so
    /// <c>{ gap = 4 }</c> folds to an Integer and not to a Number. Unary minus is folded for the
    /// same reason it is safe to: on an integer operand the VM's own negation is an unchecked one,
    /// which this replicates, and on a double it is exact.
    /// </summary>
    static bool TryFoldToConstant(Expr expr, out LuaValue value)
    {
        switch (expr)
        {
            case NilExpr:
                value = LuaValue.Nil;
                return true;
            case TrueExpr:
                value = new(true);
                return true;
            case FalseExpr:
                value = new(false);
                return true;
            case StringExpr s:
                value = new(s.Value);
                return true;
            case NumberExpr n:
                value = n.IsInteger ? new(n.IntValue) : new(n.Value);
                return true;
            case UnaryExpr { Op: OprMinus, Operand: NumberExpr m }:
                value = m.IsInteger ? new(-m.IntValue) : new(-m.Value);
                return true;
            default:
                value = default;
                return false;
        }
    }
}
