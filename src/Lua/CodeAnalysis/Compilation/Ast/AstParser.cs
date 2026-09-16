using Lua.Internal;
using Lua.Runtime;

namespace Lua.CodeAnalysis.Compilation.Ast;

using static Constants;
using static Function;
using static Scanner;
using static System.Diagnostics.Debug;

/// <summary>
/// Compiler-rewrite plan Milestone 1: builds a retained <see cref="Expr"/>/<see cref="Stat"/>
/// tree, driving the exact same scope/name resolution machinery <see cref="Parser"/> uses today
/// (<see cref="Function.MakeLocalVariable"/>, <see cref="Function.EnterBlock"/>/
/// <see cref="Function.LeaveBlock"/>, <see cref="Function.SingleVariableHelper"/>, upvalue
/// capture via <see cref="Function.FinalizeCaptures"/>) but calling none of the codegen-half
/// methods (<c>Encode*</c>, <c>Discharge*</c>, <c>ExpressionTo*Register*</c>,
/// <c>StoreVariable</c>, ...). This is a parallel, test-only path -- it does not replace
/// <see cref="Parser.Parse"/>.
///
/// Increment A and Increment B (repeat/until, compound assignment, goto/label/break/continue,
/// const declarations, the Luau if-expression, interpolated strings) are both implemented.
/// <c>type</c> statements remain permanently out of scope (see <see cref="Stat"/>'s doc comment).
/// Goto/label semantic validation is deliberately deferred to Milestone 2 (see <see cref="Stat"/>).
/// </summary>
static class AstParser
{
    /// <summary>Convenience overload for callers outside this assembly (e.g. tests) that don't
    /// have access to the unsafe <see cref="TextReader"/> constructor, mirroring how
    /// <see cref="LuaState.Load(ReadOnlySpan{char}, string, LuaTable?)"/> builds one
    /// (<c>LuaState.cs:568-581</c>).</summary>
    public static unsafe BlockStat ParseToAst(LuaState l, string source, string name)
    {
        fixed (char* ptr = source)
        {
            return ParseToAst(l, new TextReader(ptr, source.Length), name);
        }
    }

    public static BlockStat ParseToAst(LuaState l, TextReader r, string name)
    {
        using var internPool = new StringInternPool(4);
        using var p = Parser.Get(
            new()
            {
                R = r,
                Current = InitialState,
                LineNumber = 1,
                LastLine = 1,
                LookAheadToken = new(0, TkEos),
                LookAheadToken2 = new(0, TkEos),
                L = l,
                Source = name,
                Buffer = new(r.Length),
                StringPool = internPool,
            }
        );
        var f = Function.Get(p, PrototypeBuilder.Get(name));
        p.Function = f;
        f.Proto.IsVarArg = true;
        f.Proto.LineDefined = 0;

        // This pass resolves only: it builds an AST and never emits bytecode. Everything that
        // would normally emit or patch instruction PCs must stand down, or it would corrupt the
        // discarded PrototypeBuilder this pass leaves behind (see Parser.ResolutionOnly).
        p.ResolutionOnly = true;

        // Mirrors Parser.MainFunction / Function.OpenMainFunction+CloseMainFunction
        // (Function.cs:2301-2315), minus the codegen calls (ReturnNone, FoldJumps).
        f.EnterBlock(false);
        f.MakeUpValue("_ENV", MakeExpression(Kind.Local, 0));
        p.Next();
        var line = p.Scanner.LineNumber;
        var statements = StatementList(p);
        var endLine = p.Scanner.LastLine;
        p.Scanner.Check(TkEos);
        f.LeaveBlock();
        Assert(f.Block == null);
        f.FinalizeCaptures();

        // The resolution-only contract: no instruction was emitted anywhere in this pass. This
        // prototype is discarded by the caller (CodeGenerator.Generate builds its own), so any
        // instruction here would be silently-lost work at best and, because jump lists are
        // patched as linked lists in emission order, corrupted bookkeeping at worst.
        Assert(f.Proto.CodeList.Length == 0);

        // The resolution output this pass produces is the AST; the PrototypeBuilder tree only
        // exists because Function's scope bookkeeping hangs off it. Nothing consumes it, so
        // return it to the pool rather than leaking one builder per nested function per compile
        // (PrototypeBuilder.Get pops from a pool -- skipping this makes every compile allocate).
        ReleaseProtoTree(f.Proto);

        return new BlockStat { Statements = statements, Line = line, EndLine = endLine };
    }

    /// <summary>
    /// Returns a whole <see cref="PrototypeBuilder"/> subtree to its pool. The children are
    /// released first because <c>PrototypeBuilder.Release</c> only clears its own lists --
    /// clearing the parent's <c>PrototypeList</c> would otherwise orphan the children
    /// un-released (the pool has no other way back to them).
    /// </summary>
    static void ReleaseProtoTree(PrototypeBuilder proto)
    {
        for (var i = 0; i < proto.PrototypeList.Length; i++)
        {
            ReleaseProtoTree(proto.PrototypeList[i]);
        }

        proto.Release();
    }

    static List<Stat> StatementList(Parser p)
    {
        var stats = new List<Stat>();
        while (!p.BlockFollow(true))
        {
            if (p.T == TkReturn)
            {
                stats.Add(Statement(p));
                break;
            }

            stats.Add(Statement(p));
        }

        // Mirrors LabelStatement's SkipEmptyStatements()+BlockFollow(false) test
        // (Parser.cs:1408-1412), which runs once the label has consumed its own `::` and is
        // decided by what follows it. Reconstructing it here instead of at label-parse time is
        // exact because the run it skips is textually the rest of this statement list: the loop
        // above has already consumed it, and p.T is now the token that ended it. BlockFollow(false)
        // counts only '.'end'/'else'/'elseif'/EOF as a block end -- notably not 'until', so a
        // repeat body's trailing label does not qualify.
        if (p.T != TkUntil)
        {
            MarkTrailingLabels(stats);
        }

        return stats;
    }

    /// <summary>
    /// Marks the trailing run of `;`/labels at the end of <paramref name="stats"/> -- the labels
    /// SkipEmptyStatements would have walked through on its way to the block's terminator. See
    /// <see cref="LabelStat.IsLastNoOpStatement"/>.
    /// </summary>
    static void MarkTrailingLabels(List<Stat> stats)
    {
        for (var i = stats.Count - 1; i >= 0; i--)
        {
            switch (stats[i])
            {
                case EmptyStat:
                    continue;
                case LabelStat label:
                    label.IsLastNoOpStatement = true;
                    continue;
                default:
                    return;
            }
        }
    }

    static BlockStat Block(Parser p)
    {
        var line = p.Scanner.LineNumber;
        p.Function.EnterBlock(false);
        var stats = StatementList(p);
        var endLine = p.Scanner.LastLine;
        p.Function.LeaveBlock();
        return new BlockStat { Statements = stats, Line = line, EndLine = endLine };
    }

    static Stat Statement(Parser p)
    {
        var result = StatementCore(p);

        // Mirrors Parser.Statement's tail reset (Parser.cs:2004): since AstParser never
        // reserves registers for temporaries (no codegen happens here), FreeRegisterCount
        // would otherwise never track ActiveVariableCount, tripping the
        // `FreeRegisterCount == ActiveVariableCount` assert the next time a nested
        // Function.EnterBlock runs.
        p.Function.FreeRegisterCount = p.Function.ActiveVariableCount;
        return result;
    }

    static Stat StatementCore(Parser p)
    {
        var line = p.Scanner.LineNumber;
        using var enterLevel = p.EnterLevel();
        switch (p.T)
        {
            case ';':
                p.Next();
                return new EmptyStat { Line = line };
            case TkIf:
                return IfStatement(p, line);
            case TkWhile:
                return WhileStatement(p, line);
            case TkDo:
                p.Next();
                var block = Block(p);
                p.Scanner.CheckMatch(TkEnd, TkDo, line);
                return block;
            case TkFor:
                return ForStatement(p, line);
            case TkRepeat:
                return RepeatStatement(p, line);
            case TkFunction:
                return FunctionStatement(p, line);
            case TkLocal:
                p.Next();
                if (p.TestNext(TkFunction))
                {
                    return LocalFunction(p, line, isConst: false);
                }

                return LocalStatement(p, line);
            case TkDoubleColon:
                p.Next();
                return LabelStatement(p, p.CheckName(), line);
            case '@':
                return AttributeStatement(p, line);
            case TkReturn:
                p.Next();
                return ReturnStatement(p, line);
            case TkBreak:
                p.Next();
                return new BreakStat { Line = line };
            case TkGoto:
                p.Next();
                return new GotoStat { Name = p.CheckName(), Line = line };
            default:
                if (p.T == TkName)
                {
                    var name = p.Scanner.Token.S;
                    if (name == "continue" && IsContinueStatement(p))
                    {
                        p.Next(); // consume 'continue'
                        return new ContinueStat { Line = line };
                    }

                    if (name == "const" && p.Scanner.LookAhead() is TkName or TkFunction)
                    {
                        return ConstStatement(p, line);
                    }

                    if (name == "type" && p.Scanner.LookAhead() is TkName or TkFunction)
                    {
                        return TypeStatement(p, line);
                    }

                    if (
                        name == "export"
                        && p.Scanner.LookAhead() == TkName
                        && p.Scanner.LookAheadToken.S == "type"
                    )
                    {
                        p.Next(); // consume 'export'
                        if (p.Scanner.LookAhead() is not (TkName or TkFunction))
                        {
                            p.Scanner.SyntaxError("type name expected");
                        }

                        return TypeStatement(p, line);
                    }
                }

                return ExpressionStatement(p, line);
        }
    }

    static bool IsContinueStatement(Parser p)
    {
        return p.Scanner.LookAhead() is not ('=' or ',' or '.' or '[' or ':' or '(' or '{' or TkString);
    }

    static Stat ExpressionStatement(Parser p, int line)
    {
        var e = SuffixedExpression(p);
        var compoundOp = CompoundAssignmentOperator(p.T);
        if (compoundOp != OprNoBinary)
        {
            if (e is not (NameExpr or IndexExpr))
            {
                p.Scanner.SyntaxError("syntax error");
            }

            p.Next(); // consume the compound operator
            var value = Expression(p);
            return new CompoundAssignmentStat { Target = e, Op = compoundOp, Value = value, Line = line };
        }

        if (p.T == '=' || p.T == ',')
        {
            var targets = new List<Expr> { e };
            while (p.TestNext(','))
            {
                targets.Add(SuffixedExpression(p));
            }

            p.CheckNext('=');
            var values = ExpressionList(p);
            return new AssignmentStat { Targets = targets, Values = values, Line = line };
        }

        if (e is not (CallExpr or MethodCallExpr))
        {
            p.Scanner.SyntaxError("syntax error");
        }

        return new ExpressionStat { Expression = e, Line = line };
    }

    static int CompoundAssignmentOperator(int token)
    {
        return token switch
        {
            TkAddAssign => OprAdd,
            TkSubAssign => OprSub,
            TkMulAssign => OprMul,
            TkDivAssign => OprDiv,
            TkIDivAssign => OprIDiv,
            TkModAssign => OprMod,
            TkPowAssign => OprPow,
            TkConcatAssign => OprConcat,
            _ => OprNoBinary,
        };
    }

    static Stat ReturnStatement(Parser p, int line)
    {
        List<Expr> values;
        if (p.BlockFollow(true) || p.T == ';')
        {
            values = [];
        }
        else
        {
            values = ExpressionList(p);
        }

        p.TestNext(';');
        return new ReturnStat { Values = values, Line = line };
    }

    static Stat IfStatement(Parser p, int line)
    {
        var branches = new List<IfBranch>();
        p.Next(); // consume 'if'
        branches.Add(TestThenBlock(p));
        while (p.T == TkElseif)
        {
            p.Next();
            branches.Add(TestThenBlock(p));
        }

        BlockStat? elseBlock = null;
        if (p.TestNext(TkElse))
        {
            elseBlock = Block(p);
        }

        p.Scanner.CheckMatch(TkEnd, TkIf, line);
        return new IfStat { Branches = branches, Else = elseBlock, Line = line };
    }

    static IfBranch TestThenBlock(Parser p)
    {
        var condition = Expression(p);
        p.CheckNext(TkThen);
        // Read immediately after consuming `then`, which is exactly where the original compiler's
        // GoIfTrue/GoIfFalse sits (Parser.cs:1225-1242). See IfBranch.ThenLine.
        var thenLine = p.Scanner.LastLine;
        p.Function.EnterBlock(false);
        var stats = StatementList(p);
        var endLine = p.Scanner.LastLine;
        p.Function.LeaveBlock();
        return new IfBranch
        {
            Condition = condition,
            ThenLine = thenLine,
            Then = new() { Statements = stats, Line = condition.Line, EndLine = endLine },
        };
    }

    static Stat WhileStatement(Parser p, int line)
    {
        p.Next();
        var condition = Expression(p);
        p.Function.EnterBlock(true);
        p.CheckNext(TkDo);
        var body = Block(p);
        p.Scanner.CheckMatch(TkEnd, TkWhile, line);
        p.Function.LeaveBlock();
        return new WhileStat { Condition = condition, Body = body, Line = line };
    }

    static Stat ForStatement(Parser p, int line)
    {
        p.Function.EnterBlock(true);
        p.Next();
        var name = p.CheckName();
        if (p.T == ':')
        {
            p.Next();
            p.SkipTypeAnnotation();
        }

        Stat result;
        switch (p.T)
        {
            case '=':
                result = ForNumeric(p, name, line);
                break;
            case ',':
            case TkIn:
                result = ForGeneric(p, name, line);
                break;
            default:
                p.Scanner.SyntaxError("'=' or 'in' expected");
                result = default!;
                break;
        }

        p.Scanner.CheckMatch(TkEnd, TkFor, line);
        p.Function.LeaveBlock();
        return result;
    }

    // Both for-loop forms declare their implicit control locals (and, for ForGeneric, every
    // named loop variable) BEFORE the body is parsed, exactly mirroring
    // ForNumeric/ForList/ForBody/OpenForBody (Parser.cs:1084-1174, Function.cs:2267-2274) --
    // otherwise a reference to the loop variable inside the body resolves as a global instead
    // of the loop-local, since AstParser bakes that resolution into the AST's node *shape*
    // (NameExpr vs. IndexExpr-against-_ENV), not just an annotation a later pass could correct.

    static Stat ForNumeric(Parser p, string name, int line)
    {
        p.Next(); // consume '='
        p.Function.MakeLocalVariable("(for index)");
        p.Function.MakeLocalVariable("(for limit)");
        p.Function.MakeLocalVariable("(for step)");
        p.Function.MakeLocalVariable(name);

        var start = Expression(p);
        p.CheckNext(',');
        var stop = Expression(p);
        Expr? step = null;
        if (p.TestNext(','))
        {
            step = Expression(p);
        }

        p.CheckNext(TkDo);
        p.Function.AdjustLocalVariables(4);
        p.Function.FreeRegisterCount = p.Function.ActiveVariableCount;
        p.Function.EnterBlock(false);
        var body = Block(p);
        p.Function.LeaveBlock();

        return new ForNumericStat
        {
            Name = name,
            Start = start,
            Stop = stop,
            Step = step,
            Body = body,
            Line = line,
        };
    }

    static Stat ForGeneric(Parser p, string firstName, int line)
    {
        var names = new List<string> { firstName };
        p.Function.MakeLocalVariable("(for generator)");
        p.Function.MakeLocalVariable("(for state)");
        p.Function.MakeLocalVariable("(for control)");
        p.Function.MakeLocalVariable(firstName);
        while (p.TestNext(','))
        {
            var name = p.CheckName();
            names.Add(name);
            p.Function.MakeLocalVariable(name);
            if (p.T == ':')
            {
                p.Next();
                p.SkipTypeAnnotation();
            }
        }

        p.CheckNext(TkIn);
        // Captured at the same point ForStatement does (Parser.cs:1148-1149): the line of the
        // first token of the expression list, not of the `for` keyword.
        var expressionLine = p.Scanner.LineNumber;
        var expressions = ExpressionList(p);
        p.CheckNext(TkDo);
        p.Function.AdjustLocalVariables(3 + names.Count);
        p.Function.FreeRegisterCount = p.Function.ActiveVariableCount;
        p.Function.EnterBlock(false);
        var body = Block(p);
        p.Function.LeaveBlock();

        return new ForGenericStat
        {
            Names = names,
            Expressions = expressions,
            Body = body,
            Line = line,
            ExpressionLine = expressionLine,
        };
    }

    static Stat FunctionStatement(Parser p, int line)
    {
        p.Next();
        var (target, isMethod, funcName) = FunctionName(p);
        var function = FunctionBody(p, isMethod, line, funcName);
        return new FunctionStat { Target = target, Function = function, Line = line };
    }

    static (Expr Target, bool IsMethod, string Name) FunctionName(Parser p)
    {
        var firstName = p.CheckName();
        Expr e = ResolveName(p, firstName, p.Scanner.LineNumber);
        var name = firstName;
        while (p.T == '.')
        {
            p.Next();
            name = p.CheckName();
            e = new IndexExpr
            {
                Object = e,
                Key = new StringExpr { Value = name, Line = p.Scanner.LineNumber },
                IsDotSugar = true,
                Line = p.Scanner.LineNumber,
            };
        }

        if (p.T == ':')
        {
            p.Next();
            name = p.CheckName();
            e = new IndexExpr
            {
                Object = e,
                Key = new StringExpr { Value = name, Line = p.Scanner.LineNumber },
                IsDotSugar = true,
                Line = p.Scanner.LineNumber,
            };
            return (e, true, name);
        }

        return (e, false, name);
    }

    static Stat LocalFunction(Parser p, int line, bool isConst)
    {
        var name = p.CheckName();
        p.Function.MakeLocalVariable(name);
        p.Function.AdjustLocalVariables(1);
        p.Function.MarkLocalWritten(p.Function.ActiveVariableCount - 1);
        var function = FunctionBody(p, false, p.Scanner.LineNumber, name);
        if (isConst)
        {
            p.Function.MarkConst(p.Function.ActiveVariableCount - 1, default);
        }

        return new LocalFunctionStat { Name = name, Function = function, IsConst = isConst, Line = line };
    }

    static Stat RepeatStatement(Parser p, int line)
    {
        p.Function.EnterBlock(true); // loop block
        p.Function.EnterBlock(false); // scope block
        p.Next(); // consume 'repeat'
        var statements = StatementList(p);
        var endLine = p.Scanner.LastLine;
        p.Scanner.CheckMatch(TkUntil, TkRepeat, line);

        // The condition is parsed while the body's scope block is still open (see RepeatStat's
        // doc comment) so a local declared in the body resolves correctly here.
        var condition = Expression(p);

        p.Function.LeaveBlock(); // finish scope
        p.Function.LeaveBlock(); // finish loop
        return new RepeatStat
        {
            Body = new BlockStat { Statements = statements, Line = line, EndLine = endLine },
            Condition = condition,
            Line = line,
        };
    }

    static Stat LabelStatement(Parser p, string name, int line)
    {
        p.CheckNext(TkDoubleColon);
        // SkipEmptyStatements' consumption of a run of ';'/'::label::' after this one falls out
        // naturally: StatementList's loop calls Statement again for each, which handles ';' and
        // further labels itself. Its *consequence* -- re-levelling a label that is last in its
        // block -- does not, and is applied by MarkTrailingLabels once the list is complete.
        return new LabelStat { Name = name, Line = line };
    }

    static Stat AttributeStatement(Parser p, int line)
    {
        p.SkipAttributes();

        switch (p.T)
        {
            case TkFunction:
                return FunctionStatement(p, line);
            case TkLocal:
                p.Next();
                p.CheckNext(TkFunction);
                return LocalFunction(p, line, isConst: false);
        }

        if (p.T == TkName && p.Scanner.Token.S == "const" && p.Scanner.LookAhead() == TkFunction)
        {
            return ConstStatement(p, line);
        }

        p.Scanner.SyntaxError("Expected 'function', 'local function' or 'const function' after attribute");
        return default!;
    }

    // Deliberately never passes a real ConstBinding to Function.MarkConst here (always `default`,
    // whose Kind is Void): today's literal-const inlining (Function.cs:354-379) operates on
    // constant-folded ExprDescs, since parsing there always folds arithmetic eagerly. AstParser
    // doesn't fold at all -- that's a codegen-time concern for Milestone 2, which will need its
    // own equivalent of ConstBindings/TryGetConstLiteral working from ConstStat/IsConst plus its
    // own folding of the AST initializer. Marking-as-const here only affects IsConstLocal (not
    // used by AstParser itself); every use of a const local still resolves as an ordinary
    // NameExpr{Local/UpValue}, never partially inlined.
    static Stat ConstStatement(Parser p, int line)
    {
        p.Next(); // consume 'const'

        if (p.TestNext(TkFunction))
        {
            return LocalFunction(p, line, isConst: true);
        }

        var names = new List<string>();
        for (var first = true; first || p.TestNext(','); first = false)
        {
            var name = p.CheckName();
            names.Add(name);
            if (p.T == ':')
            {
                p.Next();
                p.SkipTypeAnnotation();
            }
        }

        if (!p.TestNext('='))
        {
            p.Scanner.SyntaxError("Missing initializer in const declaration");
        }

        if (names.Count == 1)
        {
            p.PendingFunctionName = names[0];
        }

        var initializers = ExpressionList(p);

        foreach (var name in names)
        {
            p.Function.MakeLocalVariable(name);
        }

        p.Function.AdjustLocalVariables(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            p.Function.MarkConst(p.Function.ActiveVariableCount - names.Count + i, default);
        }

        p.PendingFunctionName = null;
        return new ConstStat { Names = names, Initializers = initializers, Line = line };
    }

    /// <summary>
    /// Luau <c>type NAME = Type</c> / <c>export type NAME = Type</c> / <c>type function NAME
    /// funcbody</c> (<c>TypeStatement</c>, <c>Parser.cs:1870-1891</c>). Type annotations are
    /// erased with no runtime effect in today's compiler too (<c>SkipTypeAnnotation</c>), so
    /// this produces an empty no-op statement rather than a dedicated AST node -- there is
    /// nothing for a later codegen pass to act on.
    /// </summary>
    static Stat TypeStatement(Parser p, int line)
    {
        p.Next(); // consume 'type'
        if (p.T == TkFunction)
        {
            p.Next();
            p.CheckName();
            // Parsed for its side effects on the token stream only; the resulting FunctionExpr
            // is discarded, matching Body(..., discard: true)'s CloseFunctionDiscard behavior.
            FunctionBody(p, false, line, null);
            return new BlockStat { Statements = [], Line = line };
        }

        p.CheckName();
        if (p.T == '<')
        {
            p.SkipGenericTypeParameters();
        }

        p.CheckNext('=');
        p.SkipTypeAnnotation();
        return new BlockStat { Statements = [], Line = line };
    }

    static Stat LocalStatement(Parser p, int line)
    {
        var names = new List<string>();
        for (var first = true; first || p.TestNext(','); first = false)
        {
            var name = p.CheckName();
            names.Add(name);
            if (p.T == ':')
            {
                p.Next();
                p.SkipTypeAnnotation();
            }
        }

        List<Expr> initializers;
        if (p.TestNext('='))
        {
            if (names.Count == 1)
            {
                p.PendingFunctionName = names[0];
            }

            initializers = ExpressionList(p);
        }
        else
        {
            initializers = [];
        }

        foreach (var name in names)
        {
            p.Function.MakeLocalVariable(name);
        }

        p.Function.AdjustLocalVariables(names.Count);
        p.PendingFunctionName = null;
        return new LocalStat { Names = names, Initializers = initializers, Line = line };
    }

    static FunctionExpr FunctionBody(Parser p, bool isMethod, int line, string? name)
    {
        p.Function.OpenFunction(line);
        if (name != null)
        {
            p.Function.Proto.Name = name;
        }
        else if (p.PendingFunctionName != null)
        {
            // Mirrors Body()'s PendingFunctionName consumption (Parser.cs:1467-1471): names the
            // *first* function literal encountered while parsing a single-name local/const's
            // initializer, however deeply nested (e.g. `local x = call(function() end)` names
            // the callback "x", not just a bare `local x = function() end`).
            name = p.PendingFunctionName;
            p.Function.Proto.Name = name;
            p.PendingFunctionName = null;
        }

        if (p.T == '<')
        {
            p.SkipGenericTypeParameters();
        }

        p.CheckNext('(');
        var parameters = new List<string>();
        if (isMethod)
        {
            p.Function.MakeLocalVariable("self");
            p.Function.AdjustLocalVariables(1);
            parameters.Add("self");
        }

        var isVarArg = false;
        if (p.T != ')')
        {
            for (var first = true; first || (!isVarArg && p.TestNext(',')); first = false)
            {
                if (p.T == TkName)
                {
                    var paramName = p.CheckName();
                    p.Function.MakeLocalVariable(paramName);
                    parameters.Add(paramName);
                    if (p.T == ':')
                    {
                        p.Next();
                        p.SkipTypeAnnotation();
                    }
                }
                else if (p.T == TkDots)
                {
                    p.Next();
                    isVarArg = true;
                    if (p.T == ':')
                    {
                        p.Next();
                        p.SkipTypeAnnotation();
                    }
                }
                else
                {
                    p.Scanner.SyntaxError("<name> or '...' expected");
                }
            }
        }

        p.Function.Proto.IsVarArg = isVarArg;
        p.Function.AdjustLocalVariables(parameters.Count - (isMethod ? 1 : 0));
        p.Function.Proto.ParameterCount = p.Function.ActiveVariableCount;

        // See Statement()'s matching reset: parameters just bumped ActiveVariableCount without
        // any register reservation (no codegen happens here), so this must be resynced before
        // the body's first statement can safely call Function.EnterBlock.
        p.Function.FreeRegisterCount = p.Function.ActiveVariableCount;

        p.CheckNext(')');
        if (p.T == ':')
        {
            p.Next();
            p.SkipTypeAnnotation();
        }

        var statements = StatementList(p);
        p.Function.Proto.LastLineDefined = p.Scanner.LineNumber;
        p.Scanner.CheckMatch(TkEnd, TkFunction, line);
        // Captured *after* CheckMatch consumes 'end' (unlike LastLineDefined above, a distinct
        // metadata field matching Body()'s own capture point exactly, Parser.cs:1499) -- this is
        // the ambient line CloseFunction's ReturnNone/Closure emission actually sees today, since
        // Scanner.LastLine only updates when a token is truly consumed via Next().
        var endLine = p.Scanner.LastLine;

        var childProto = p.Function.Proto;
        p.Function.LeaveBlock();
        Assert(p.Function.Block == null);
        p.Function.FinalizeCaptures();
        p.Function = p.Function.Previous!;

        return new FunctionExpr
        {
            Parameters = parameters,
            IsVarArg = isVarArg,
            IsMethod = isMethod,
            Body = new BlockStat { Statements = statements, Line = line, EndLine = endLine },
            LineDefined = line,
            LastLineDefined = childProto.LastLineDefined,
            Name = name,
            Line = line,
        };
    }

    static List<Expr> ExpressionList(Parser p)
    {
        var list = new List<Expr> { Expression(p) };
        while (p.TestNext(','))
        {
            list.Add(Expression(p));
        }

        return list;
    }

    static Expr Expression(Parser p)
    {
        return SubExpression(p, 0).Item1;
    }

    static (Expr, int) SubExpression(Parser p, int limit)
    {
        using var enterLevel = p.EnterLevel();
        Expr e;
        var u = Parser.UnaryOp(p.T);
        if (u != OprNoUnary)
        {
            var line = p.Scanner.LineNumber;
            p.Next();
            (e, _) = SubExpression(p, Parser.UnaryPriority);
            e = new UnaryExpr { Op = u, Operand = e, Line = line };
        }
        else
        {
            e = SimpleExpression(p);
        }

        if (
            p.T == TkDoubleColon
            && !(p.Scanner.LookAhead() == TkName && p.Scanner.LookAhead2() == TkDoubleColon)
        )
        {
            p.Next();
            p.SkipTypeAnnotation();
        }

        var op = Parser.BinaryOp(p.T);
        while (op != OprNoBinary && Parser.priority[op].Left > limit)
        {
            var line = p.Scanner.LineNumber;
            p.Next();
            var (e2, next) = SubExpression(p, Parser.priority[op].Right);
            e = op is OprAnd or OprOr
                ? new AndOrExpr { IsAnd = op == OprAnd, Left = e, Right = e2, Line = line }
                : new BinaryExpr { Op = op, Left = e, Right = e2, Line = line };
            op = next;
        }

        return (e, op);
    }

    static Expr SimpleExpression(Parser p)
    {
        var line = p.Scanner.LineNumber;
        Expr e;
        switch (p.T)
        {
            case TkNumber:
                e = NumberLiteral(p, line);
                break;
            case TkString:
            case TkInterpString:
                e = new StringExpr { Value = p.Scanner.Token.S, Line = line };
                break;
            case TkNil:
                e = new NilExpr { Line = line };
                break;
            case TkTrue:
                e = new TrueExpr { Line = line };
                break;
            case TkFalse:
                e = new FalseExpr { Line = line };
                break;
            case TkDots:
                if (!p.Function.Proto.IsVarArg)
                {
                    p.Scanner.SyntaxError("cannot use '...' outside a vararg function");
                }

                e = new VarArgExpr { Line = line };
                break;
            case '{':
                return Constructor(p);
            case TkFunction:
                p.Next();
                return FunctionBody(p, false, line, null);
            case TkIf:
                return IfElseExpression(p);
            case TkInterpBegin:
                return InterpolatedString(p);
            case '@':
                p.SkipAttributes();
                if (p.T != TkFunction)
                {
                    p.Scanner.SyntaxError("Expected 'function' after attribute");
                }

                p.Next();
                return FunctionBody(p, false, line, null);
            default:
                return SuffixedExpression(p);
        }

        p.Next();
        return e;
    }

    /// <summary>Luau <c>if cond then value {elseif cond then value} else value</c> expression
    /// form. A statement-position <c>if</c> never reaches here -- <see cref="StatementCore"/>
    /// handles <c>TkIf</c> before any expression is parsed, mirroring <c>Parser.cs:470-473</c>.</summary>
    static Expr IfElseExpression(Parser p)
    {
        using var enterLevel = p.EnterLevel();
        var line = p.Scanner.LineNumber;
        var branches = new List<IfElseBranch>();
        p.Next(); // consume 'if'
        while (true)
        {
            var condition = Expression(p);
            p.CheckNext(TkThen);
            var value = Expression(p);
            branches.Add(new IfElseBranch { Condition = condition, Value = value });
            if (!p.TestNext(TkElseif))
            {
                break;
            }
        }

        p.CheckNext(TkElse); // mandatory, unlike the if *statement*
        var elseValue = Expression(p);
        return new IfElseExpr { Branches = branches, Else = elseValue, Line = line };
    }

    /// <summary>Mirrors <c>InterpolatedString</c> (<c>Parser.cs:504-550</c>) exactly, minus the
    /// codegen (the call-to-builtin sequence becomes a Milestone 2 concern).</summary>
    static Expr InterpolatedString(Parser p)
    {
        var line = p.Scanner.LineNumber;
        var parts = new List<Expr>();
        while (true)
        {
            if (p.T is not (TkInterpString or TkInterpBegin or TkInterpMid or TkInterpEnd))
            {
                p.Scanner.SyntaxError("malformed interpolated string");
            }

            var text = p.Scanner.Token.S;
            if (text.Length > 0)
            {
                parts.Add(new StringExpr { Value = text, Line = p.Scanner.LineNumber });
            }

            if (p.T is TkInterpString or TkInterpEnd)
            {
                p.Next();
                break;
            }

            p.Next();
            if (p.T is TkInterpMid or TkInterpEnd)
            {
                p.Scanner.SyntaxError("malformed interpolated string, expected expression inside '{}'");
            }

            parts.Add(Expression(p));
        }

        return new InterpolatedStringExpr { Parts = parts, Line = line };
    }

    static Expr NumberLiteral(Parser p, int line)
    {
        var e = new NumberExpr { Value = p.Scanner.Token.N, Line = line };
        e.IsInteger = TryParseIntegerLiteral(p.Scanner.GetTokenRawText(), out e.IntValue);
        return e;
    }

    /// <summary>Mirrors Parser.TryParseIntegerLiteral (Parser.cs:364-431) exactly.</summary>
    static bool TryParseIntegerLiteral(string? rawText, out long value)
    {
        value = 0;
        if (rawText == null)
        {
            return false;
        }

        var text = rawText.Contains('_') ? rawText.Replace("_", "") : rawText;

        if (text.Length > 1 && text[0] == '0' && text[1] is 'b' or 'B')
        {
            var digits = text.AsSpan(2);
            if (digits.Length is 0 or > 63)
            {
                return false;
            }

            var result = 0L;
            foreach (var c in digits)
            {
                if (c is not ('0' or '1'))
                {
                    return false;
                }

                result = (result << 1) | (uint)(c - '0');
            }

            value = result;
            return true;
        }

        if (text.Length > 1 && text[0] == '0' && text[1] is 'x' or 'X')
        {
            for (var i = 2; i < text.Length; i++)
            {
                if (text[i] is '.' or 'p' or 'P')
                {
                    return false;
                }
            }

            return long.TryParse(
                    text.AsSpan(2),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value
                ) && value >= 0;
        }

        foreach (var c in text)
        {
            if (c is '.' or 'e' or 'E')
            {
                return false;
            }
        }

        return long.TryParse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out value
        );
    }

    static Expr Constructor(Parser p)
    {
        var line = p.Scanner.LineNumber;
        p.CheckNext('{');
        var fields = new List<TableField>();
        while (p.T != '}')
        {
            if (p.T == TkName && p.Scanner.LookAhead() == '=')
            {
                var key = new StringExpr { Value = p.CheckName(), Line = p.Scanner.LineNumber };
                p.CheckNext('=');
                fields.Add(new TableField { Key = key, Value = Expression(p) });
            }
            else if (p.T == '[')
            {
                p.Next();
                var key = Expression(p);
                p.CheckNext(']');
                p.CheckNext('=');
                fields.Add(new TableField { Key = key, Value = Expression(p) });
            }
            else
            {
                fields.Add(new TableField { Key = null, Value = Expression(p) });
            }

            if (!p.TestNext(',') && !p.TestNext(';'))
            {
                break;
            }
        }

        p.Scanner.CheckMatch('}', '{', line);
        return new TableConstructorExpr { Fields = fields, Line = line, EndLine = p.Scanner.LastLine };
    }

    static Expr PrimaryExpression(Parser p)
    {
        switch (p.T)
        {
            case '(':
                var line = p.Scanner.LineNumber;
                p.Next();
                var inner = Expression(p);
                p.Scanner.CheckMatch(')', '(', line);
                return new ParenExpr { Inner = inner, Line = line };
            case TkName:
                var nameLine = p.Scanner.LineNumber;
                return ResolveName(p, p.CheckName(), nameLine);
            default:
                p.Scanner.SyntaxError("unexpected symbol");
                return null!;
        }
    }

    static Expr SuffixedExpression(Parser p)
    {
        var line = p.Scanner.LineNumber;
        var e = PrimaryExpression(p);
        while (true)
        {
            switch (p.T)
            {
                case '.':
                    p.Next();
                    var fieldLine = p.Scanner.LineNumber;
                    e = new IndexExpr
                    {
                        Object = e,
                        Key = new StringExpr { Value = p.CheckName(), Line = fieldLine },
                        IsDotSugar = true,
                        Line = fieldLine,
                    };
                    break;
                case '[':
                    p.Next();
                    var key = Expression(p);
                    p.CheckNext(']');
                    e = new IndexExpr { Object = e, Key = key, IsDotSugar = false, Line = key.Line };
                    break;
                case ':':
                    p.Next();
                    var methodLine = p.Scanner.LineNumber;
                    var methodName = p.CheckName();
                    e = new MethodCallExpr
                    {
                        Receiver = e,
                        MethodName = methodName,
                        Arguments = FunctionArguments(p),
                        Line = methodLine,
                    };
                    break;
                case '(':
                case TkString:
                case '{':
                    e = new CallExpr { Callee = e, Arguments = FunctionArguments(p), Line = line };
                    break;
                case '<':
                    if (p.Scanner.LookAhead() == '<')
                    {
                        p.SkipTypeInstantiation();
                        break;
                    }

                    return e;
                default:
                    return e;
            }
        }
    }

    /// <summary>Mirrors <c>FunctionArguments</c> (Parser.cs:249-298).</summary>
    static List<Expr> FunctionArguments(Parser p)
    {
        switch (p.T)
        {
            case '(':
                var line = p.Scanner.LineNumber;
                p.Next();
                List<Expr> args;
                if (p.T == ')')
                {
                    args = [];
                }
                else
                {
                    args = ExpressionList(p);
                }

                p.Scanner.CheckMatch(')', '(', line);
                return args;
            case '{':
                return [Constructor(p)];
            case TkString:
                var s = new StringExpr { Value = p.Scanner.Token.S, Line = p.Scanner.LineNumber };
                p.Next();
                return [s];
            default:
                p.Scanner.SyntaxError("function arguments expected");
                return [];
        }
    }

    static Expr ResolveName(Parser p, string name, int line)
    {
        var (e, found) = SingleVariableHelper(p.Function, name, true);
        if (found)
        {
            return e.Kind switch
            {
                Kind.Local => new NameExpr { Kind = NameExpr.RefKind.Local, Index = e.Info, Name = name, Line = line },
                Kind.UpValue => new NameExpr { Kind = NameExpr.RefKind.UpValue, Index = e.Info, Name = name, Line = line },
                _ => throw new InvalidOperationException($"unexpected resolved kind {e.Kind} for '{name}'"),
            };
        }

        var (envExpr, envFound) = SingleVariableHelper(p.Function, "_ENV", true);
        if (!envFound || (envExpr.Kind != Kind.Local && envExpr.Kind != Kind.UpValue))
        {
            throw new InvalidOperationException("_ENV did not resolve to a local or upvalue");
        }

        Expr env = envExpr.Kind == Kind.Local
            ? new NameExpr { Kind = NameExpr.RefKind.Local, Index = envExpr.Info, Name = "_ENV", Line = line }
            : new NameExpr { Kind = NameExpr.RefKind.UpValue, Index = envExpr.Info, Name = "_ENV", Line = line };

        return new IndexExpr
        {
            Object = env,
            Key = new StringExpr { Value = name, Line = line },
            IsDotSugar = true,
            Line = line,
        };
    }

    static NotSupportedException NotSupported(string construct)
    {
        return new($"'{construct}' is not yet supported by AstParser (deferred to compiler-rewrite Increment B)");
    }
}
