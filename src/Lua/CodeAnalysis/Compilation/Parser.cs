using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lua.Internal;
using Lua.Runtime;
using static System.Diagnostics.Debug;

namespace Lua.CodeAnalysis.Compilation;

using static Constants;
using static Function;
using static Scanner;

class Parser : IPoolNode<Parser>, IDisposable
{
    /// inline
    internal Scanner Scanner;

    internal Function Function = null!;
    internal FastListCore<int> ActiveVariables;
    internal FastListCore<Label> PendingGotos;
    internal FastListCore<Label> ActiveLabels;

    /// <summary>
    /// Luau `continue` bookkeeping: one entry per lexically enclosing loop of the function
    /// being parsed. Truncated when a nested function body starts, so a `continue` inside a
    /// nested function cannot target a loop of an enclosing one.
    /// </summary>
    internal FastListCore<LoopContext> Loops;

    /// <summary>
    /// Function whose repeat-loop `until` condition is currently being parsed, with the
    /// lowest level a `continue` jumped over (see <see cref="LoopContext.MinContinueLevel"/>).
    /// Locals at or above that level are undefined in the condition.
    /// </summary>
    internal Function? RepeatConditionFunction;

    internal int RepeatConditionMinLevel = -1;
    internal int RepeatConditionLine;

    // Set by `local X = function() ... end` (single-local direct assignment) so the
    // anonymous function's prototype can be named after its variable.
    internal string? PendingFunctionName;

    Parser? nextNode;

    static LinkedPool<Parser> pool;

    static readonly (int Left, int Right)[] priority =
    [
        (6, 6),
        (6, 6),
        (7, 7),
        (7, 7),
        (7, 7),
        (10, 9),
        (5, 4),
        (3, 3),
        (3, 3),
        (3, 3),
        (3, 3),
        (3, 3),
        (3, 3),
        (2, 2),
        (1, 1),
        (7, 7), // '//' (Luau), same precedence as '*' '/' '%'
    ];

    internal int T => Scanner.Token.T;

    internal bool TestNext(int token)
    {
        return Scanner.TestNext(token);
    }

    internal void Next()
    {
        Scanner.Next();
    }

    Parser() { }

    ref Parser? IPoolNode<Parser>.NextNode => ref nextNode;

    static Parser Get(Scanner scanner)
    {
        if (!pool.TryPop(out var parser))
        {
            parser = new();
        }

        parser.Scanner = scanner;
        return parser;
    }

    void IDisposable.Dispose()
    {
        Release();
    }

    public void Release()
    {
        Scanner.Buffer.Dispose();
        Scanner = default;
        ActiveVariables.Clear();
        PendingGotos.Clear();
        ActiveLabels.Clear();
        Loops.Clear();
        RepeatConditionFunction = null;
        RepeatConditionMinLevel = -1;
        RepeatConditionLine = 0;
        PendingFunctionName = null;
        pool.TryPush(this);
    }

    public void CheckCondition(bool c, string message)
    {
        if (!c)
        {
            Scanner.SyntaxError(message);
        }
    }

    public string CheckName()
    {
        Scanner.Check(TkName);
        var s = Scanner.Token.S;
        Next();
        return s;
    }

    public void CheckLimit(int val, int limit, string what)
    {
        if (val > limit)
        {
            var where = "main function";
            var line = Function.Proto.LineDefined;
            if (line != 0)
            {
                where = $"function at line {line}";
            }

            Scanner.SyntaxError($"too many {what} (limit is {limit}) in {where}");
        }
    }

    public void CheckNext(int t)
    {
        Scanner.Check(t);
        Next();
    }

    public ExprDesc CheckNameAsExpression()
    {
        return Function.EncodeString(CheckName());
    }

    public ExprDesc SingleVariable()
    {
        return Function.SingleVariable(CheckName());
    }

    public void LeaveLevel()
    {
        Scanner.L.CallCount--;
    }

    public TempBlock EnterLevel()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Scanner.SyntaxError("too many syntax levels");
        }

        Scanner.L.CallCount++;
        CheckLimit(Scanner.L.CallCount, MaxCallCount, "Go levels");
        return new(Scanner.L);
    }

    public (ExprDesc e, int n) ExpressionList()
    {
        var n = 1;
        var e = Expression();
        for (; TestNext(','); n++, e = Expression())
        {
            Function.ExpressionToNextRegister(e);
        }

        return (e, n);
    }

    public (int, int, int, ExprDesc) Field(int tableRegister, int a, int h, int pending, ExprDesc e)
    {
        var freeRegisterCount = Function.FreeRegisterCount;

        if (T == TkName && Scanner.LookAhead() == '=')
        {
            CheckLimit(h, MaxInt, "items in a constructor");
            HashField(CheckNameAsExpression());
        }
        else if (T == '[')
        {
            HashField(Index());
        }
        else
        {
            e = Expression();
            CheckLimit(a, MaxInt, "items in a constructor");
            a++;
            pending++;
        }

        return (a, h, pending, e);

        void HashField(ExprDesc k)
        {
            h++;
            CheckNext('=');
            Function.FlushFieldToConstructor(tableRegister, freeRegisterCount, k, Expression);
        }
    }

    public ExprDesc Constructor()
    {
        var (pc, t) = Function.OpenConstructor();
        var (line, a, h, pending) = (Scanner.LineNumber, 0, 0, 0);
        ExprDesc e = default;
        CheckNext('{');
        if (T != '}')
        {
            (a, h, pending, e) = Field(t.Info, a, h, pending, e);
            while ((TestNext(',') || TestNext(';')) && T != '}')
            {
                if (e.Kind != Kind.Void)
                {
                    pending = Function.FlushToConstructor(t.Info, pending, a, e);
                    e.Kind = Kind.Void;
                }

                (a, h, pending, e) = Field(t.Info, a, h, pending, e);
            }
        }

        Scanner.CheckMatch('}', '{', line);
        Function.CloseConstructor(pc, t.Info, pending, a, h, e);
        return t;
    }

    public ExprDesc FunctionArguments(ExprDesc f, int line)
    {
        ExprDesc args = default;
        switch (T)
        {
            case '(':
                Next();
                if (T == ')')
                {
                    args.Kind = Kind.Void;
                }
                else
                {
                    (args, _) = ExpressionList();
                    Function.SetMultipleReturns(args);
                }

                Scanner.CheckMatch(')', '(', line);
                break;
            case '{':
                args = Constructor();
                break;
            case TkString:
                args = Function.EncodeString(Scanner.Token.S);
                Next();
                break;
            default:
                Scanner.SyntaxError("function arguments expected");
                break;
        }

        var (@base, parameterCount) = (f.Info, MultipleReturns);
        if (!args.HasMultipleReturns())
        {
            if (args.Kind != Kind.Void)
            {
                Function.ExpressionToNextRegister(args);
            }

            parameterCount = Function.FreeRegisterCount - (@base + 1);
        }

        var e = MakeExpression(
            Kind.Call,
            Function.EncodeABC(OpCode.Call, @base, parameterCount + 1, 2)
        );
        Function.FixLine(line);
        Function.FreeRegisterCount = @base + 1; // call removed function and args & leaves (unless changed) one result
        return e;
    }

    public ExprDesc PrimaryExpression()
    {
        switch (T)
        {
            case '(':
                var line = Scanner.LineNumber;
                Next();
                var e = Expression();
                Scanner.CheckMatch(')', '(', line);
                e = Function.DischargeVariables(e);
                return e;
            case TkName:
                return SingleVariable();
            default:
                Scanner.SyntaxError("unexpected symbol");
                return default;
        }
    }

    public ExprDesc SuffixedExpression()
    {
        var line = Scanner.LineNumber;
        var e = PrimaryExpression();
        while (true)
        {
            switch (T)
            {
                case '.':
                    e = FieldSelector(e);
                    break;
                case '[':
                    e = Function.Indexed(Function.ExpressionToAnyRegisterOrUpValue(e), Index());
                    break;
                case ':':
                    Next();
                    e = FunctionArguments(Function.Self(e, CheckNameAsExpression()), line);
                    break;
                case '(':
                case TkString:
                case '{':
                    e = FunctionArguments(Function.ExpressionToNextRegister(e), line);
                    break;
                case '<':
                    // explicit type instantiation: f<<T, U>>(args)
                    if (Scanner.LookAhead() == '<')
                    {
                        SkipTypeInstantiation();
                        break;
                    }

                    return e;
                default:
                    return e;
            }
        }
    }

    /// <summary>
    /// Parses an integer literal directly from its raw text as a 64-bit signed
    /// integer, avoiding the lossy double round-trip (the scanner parses every
    /// number as a double). Returns false when the literal is a float form (has
    /// '.', a decimal exponent, or a hex 'p' exponent) or the value overflows a
    /// signed 64-bit integer (Lua 5.3: such literals denote floats).
    /// </summary>
    static bool TryParseIntegerLiteral(string? rawText, out long value)
    {
        value = 0;
        if (rawText == null)
        {
            return false;
        }

        // Luau digit separators carry no value ('1_000' == 1000, '1_' == 1).
        var text = rawText.Contains('_') ? rawText.Replace("_", "") : rawText;

        if (text.Length > 1 && text[0] == '0' && text[1] is 'b' or 'B')
        {
            // Binary integer literal (Luau). 63 bits is the most that stays a
            // non-negative signed 64-bit value; wider literals denote floats.
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
            // Hex integer: 'e'/'E' are hex digits; only '.', 'p'/'P' make it a float.
            for (var i = 2; i < text.Length; i++)
            {
                if (text[i] is '.' or 'p' or 'P')
                {
                    return false;
                }
            }

            // Fits a signed 64-bit integer only when the parsed value is non-negative
            // (16-hex-digit values >= 0x8000000000000000 parse as negative longs).
            return long.TryParse(
                    text.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out value
                ) && value >= 0;
        }

        // Decimal integer: '.', 'e'/'E' exponent make it a float.
        foreach (var c in text)
        {
            if (c is '.' or 'e' or 'E')
            {
                return false;
            }
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public ExprDesc SimpleExpression()
    {
        ExprDesc e;
        switch (T)
        {
            case TkNumber:
                e = MakeExpression(Kind.Number, 0);
                e.Value = Scanner.Token.N;
                e.IsInteger = TryParseIntegerLiteral(Scanner.GetTokenRawText(), out e.IntValue);
                break;
            case TkString:
            case TkInterpString:
                e = Function.EncodeString(Scanner.Token.S);
                break;
            case TkNil:
                e = MakeExpression(Kind.Nil, 0);
                break;
            case TkTrue:
                e = MakeExpression(Kind.True, 0);
                break;
            case TkFalse:
                e = MakeExpression(Kind.False, 0);
                break;
            case TkDots:
                CheckCondition(
                    Function.Proto.IsVarArg,
                    "cannot use '...' outside a vararg function"
                );
                e = MakeExpression(Kind.VarArg, Function.EncodeABC(OpCode.VarArg, 0, 1, 0));
                break;
            case '{':
                e = Constructor();
                return e;
            case TkFunction:
                Next();
                e = Body(false, Scanner.LineNumber);
                return e;
            case TkIf:
                // Luau if-then-else expression. A statement `if` never reaches here:
                // Statement() handles TkIf before any expression is parsed.
                return IfElseExpression();
            case TkInterpBegin:
                // Luau interpolated string (backtick literal with `{expr}` holes).
                return InterpolatedString();
            case '@':
                // Luau attributes on an anonymous function: `@native function() end`.
                // They are parsed and ignored.
                SkipAttributes();
                if (T != TkFunction)
                {
                    Scanner.SyntaxError("Expected 'function' after attribute");
                }

                Next();
                return Body(false, Scanner.LineNumber);
            default:
                e = SuffixedExpression();
                return e;
        }

        Next();
        return e;
    }

    /// <summary>
    /// Luau interpolated string: a backtick literal with `{expr}` holes. The literal parts and
    /// the hole expressions are pushed as the arguments of one call to an internal helper that
    /// concatenates them with the same conversion `tostring` uses, so `__tostring` and number
    /// formatting match. Like Luau, the result cannot be suffixed directly (it is not a
    /// prefixexp), so `` `{x}`:upper() `` needs parentheses.
    /// </summary>
    public ExprDesc InterpolatedString()
    {
        var line = Scanner.LineNumber;
        var @base = Function.FreeRegisterCount;
        Function.ReserveRegisters(1);
        Function.LoadBuiltin(@base, LuaBuiltin.InterpolationBuilder);
        var argumentCount = 0;

        while (true)
        {
            if (T is not (TkInterpString or TkInterpBegin or TkInterpMid or TkInterpEnd))
            {
                Scanner.SyntaxError("malformed interpolated string");
            }

            var text = Scanner.Token.S;
            if (text.Length > 0)
            {
                Function.ExpressionToNextRegister(Function.EncodeString(text));
                argumentCount++;
            }

            if (T is TkInterpString or TkInterpEnd)
            {
                Next();
                break;
            }

            // INTERP_BEGIN / INTERP_MID: the hole's expression follows. An empty hole means the
            // scanner already produced the section after it.
            Next();
            if (T is TkInterpMid or TkInterpEnd)
            {
                Scanner.SyntaxError(
                    "malformed interpolated string, expected expression inside '{}'"
                );
            }

            Function.ExpressionToNextRegister(Expression());
            argumentCount++;
        }

        var call = Function.EncodeABC(OpCode.Call, @base, argumentCount + 1, 2);
        Function.FixLine(line);
        Function.FreeRegisterCount = @base + 1;
        return Function.MakeExpression(Kind.Call, call);
    }

    /// <summary>
    /// Luau if-then-else expression:
    /// `if cond then value {elseif cond then value} else value`. The result is a single
    /// value, so a branch that produces multiple results keeps only its first.
    /// </summary>
    public ExprDesc IfElseExpression()
    {
        using var b = EnterLevel();
        var target = Function.FreeRegisterCount;
        Function.ReserveRegisters(1);
        var endJumps = NoJump;

        Next(); // consume 'if'
        while (true)
        {
            var condition = Function.GoIfTrue(Expression());
            CheckNext(TkThen);
            Function.ExpressionToRegister(Expression(), target);
            endJumps = Function.Concatenate(endJumps, Function.Jump());

            // Condition was false: continue with the next elseif / the else branch.
            Function.PatchToHere(condition.F);
            Function.FreeRegisterCount = target + 1;

            if (!TestNext(TkElseif)) // also consumes 'elseif'
            {
                break;
            }
        }

        CheckNext(TkElse); // mandatory, unlike the if *statement*
        Function.ExpressionToRegister(Expression(), target);
        Function.PatchToHere(endJumps);
        Function.FreeRegisterCount = target + 1;
        return Function.MakeExpression(Kind.NonRelocatable, target);
    }

    public static int UnaryOp(int op)
    {
        return op switch
        {
            TkNot => OprNot,
            '-' => OprMinus,
            '#' => OprLength,
            _ => OprNoUnary,
        };
    }

    public static int BinaryOp(int op)
    {
        return op switch
        {
            '+' => OprAdd,
            '-' => OprSub,
            '*' => OprMul,
            '/' => OprDiv,
            TkIDiv => OprIDiv,
            '%' => OprMod,
            '^' => OprPow,
            TkConcat => OprConcat,
            TkNe => OprNE,
            TkEq => OprEq,
            '<' => OprLT,
            TkLe => OprLE,
            '>' => OprGT,
            TkGe => OprGE,
            TkAnd => OprAnd,
            TkOr => OprOr,
            _ => OprNoBinary,
        };
    }

    public static int UnaryPriority => 8;

    public (ExprDesc, int) SubExpression(int limit)
    {
        using var b = EnterLevel();
        ExprDesc e;
        var u = UnaryOp(T);
        if (u != OprNoUnary)
        {
            var line = Scanner.LineNumber;
            Next();
            (e, _) = SubExpression(UnaryPriority);
            e = Function.Prefix(u, e, line);
        }
        else
        {
            e = SimpleExpression();
        }

        if (
            T == TkDoubleColon
            && !(Scanner.LookAhead() == TkName && Scanner.LookAhead2() == TkDoubleColon)
        )
        {
            // Luau type cast: 'exp :: Type'. A ':: name ::' sequence is a Lua 5.2
            // label and is left for the statement parser.
            Next();
            SkipTypeAnnotation();
        }

        var op = BinaryOp(T);
        while (op != OprNoBinary && priority[op].Left > limit)
        {
            var line = Scanner.LineNumber;
            Next();
            e = Function.Infix(op, e);
            var (e2, next) = SubExpression(priority[op].Right);
            e = Function.Postfix(op, e, e2, line);
            op = next;
        }

        return (e, op);
    }

    public ExprDesc Expression()
    {
        var (e, _) = SubExpression(0);
        return e;
    }

    // Skips a Luau type annotation. The type content is never validated: any token
    // stream that lexes cleanly is consumed until a structural terminator is reached, so
    // type-level errors never interrupt parsing. Only an unterminated delimiter (EOF while
    // still inside brackets) is reported, as the parser cannot recover from that.
    public void SkipTypeAnnotation()
    {
        var paren = 0;
        var curly = 0;
        var square = 0;
        var angle = 0;
        var expectAtom = true;
        var expectParen = false; // a following '(' continues the type (typeof / <T>)
        while (true)
        {
            var t = T;

            // '->' is always consumed as a unit so its '>' is never mistaken for a
            // generic-argument close.
            if (t == '-' && Scanner.LookAhead() == '>')
            {
                Next();
                Next();
                if (paren + curly + square + angle == 0)
                {
                    expectAtom = true;
                }

                continue;
            }

            if (paren + curly + square + angle > 0)
            {
                switch (t)
                {
                    case TkEos:
                        Scanner.SyntaxError("unfinished type annotation");
                        return;
                    case '(':
                        paren++;
                        break;
                    case ')':
                        if (paren > 0)
                        {
                            paren--;
                        }

                        break;
                    case '{':
                        curly++;
                        break;
                    case '}':
                        if (curly > 0)
                        {
                            curly--;
                        }

                        break;
                    case '[':
                        square++;
                        break;
                    case ']':
                        if (square > 0)
                        {
                            square--;
                        }

                        break;
                    case '<':
                        angle++;
                        break;
                    case '>':
                        if (angle > 0)
                        {
                            angle--;
                        }

                        break;
                }

                Next();
                if (paren + curly + square + angle == 0)
                {
                    expectAtom = false;
                }

                continue;
            }

            if (expectAtom)
            {
                switch (t)
                {
                    case TkName:
                    {
                        var name = Scanner.Token.S;
                        Next();
                        expectAtom = false;
                        expectParen = name == "typeof";
                        continue;
                    }
                    case TkString:
                    case TkNumber:
                    case TkNil:
                    case TkTrue:
                    case TkFalse:
                        Next();
                        expectAtom = false;
                        expectParen = false;
                        continue;
                    case '(':
                        paren = 1;
                        Next();
                        continue;
                    case '{':
                        curly = 1;
                        Next();
                        continue;
                    case '<':
                        // generic type parameters of a function type: '<T>(...) -> ...'
                        angle = 1;
                        expectParen = true;
                        Next();
                        continue;
                    case TkDots:
                        // '...' Type (variadic type pack)
                        Next();
                        continue;
                    default:
                        return;
                }
            }

            // After a type atom only continuations are consumed; anything else (including
            // a bare NAME that starts the next statement) ends the type.
            switch (t)
            {
                case '|':
                case '&':
                    Next();
                    expectAtom = true;
                    expectParen = false;
                    continue;
                case '?':
                case TkDots:
                    Next();
                    expectParen = false;
                    continue;
                case '.':
                    Next();
                    expectAtom = true;
                    expectParen = false;
                    continue;
                case '(' when expectParen:
                    // 'typeof' '(' exp ')' or '<T>' '(' params ')' '->' ...
                    paren = 1;
                    expectParen = false;
                    Next();
                    continue;
                case '<':
                    // In type position '<' after a name always introduces generic
                    // type arguments (matching Luau's parseSimpleType); the first
                    // argument may be any type, e.g. a table type 'Array<{...}>'.
                    Next();
                    angle = 1;
                    expectParen = false;
                    continue;
                default:
                    return;
            }
        }
    }

    // Skips a generic type parameter list (current token must be '<'), used both for
    // function declarations and type aliases. Tracks ()/[]/{} nesting so a '>' belonging
    // to a nested function type does not close the list early.
    public void SkipGenericTypeParameters()
    {
        var paren = 0;
        var curly = 0;
        var square = 0;
        var angle = 1;
        Next();
        while (angle > 0)
        {
            var t = T;

            // consume '->' as a unit so its '>' is not treated as a closing '>'
            if (t == '-' && Scanner.LookAhead() == '>')
            {
                Next();
                Next();
                continue;
            }

            switch (t)
            {
                case TkEos:
                    Scanner.SyntaxError("unfinished generic type parameter list");
                    return;
                case '(':
                    paren++;
                    break;
                case ')':
                    if (paren > 0)
                    {
                        paren--;
                    }

                    break;
                case '{':
                    curly++;
                    break;
                case '}':
                    if (curly > 0)
                    {
                        curly--;
                    }

                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    if (square > 0)
                    {
                        square--;
                    }

                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    if (paren == 0 && curly == 0 && square == 0)
                    {
                        angle--;
                    }

                    break;
            }

            Next();
        }
    }

    public void SkipTypeInstantiation()
    {
        // Current token is '<' and the next is '<' (verified by the caller):
        // 'f<<T, U>>' — explicit type arguments. Skipped and ignored.
        Next();
        Next();
        var paren = 0;
        var curly = 0;
        var square = 0;
        var angle = 0;
        while (true)
        {
            var t = T;

            // consume '->' as a unit so its '>' is not mistaken for a closing '>'
            if (t == '-' && Scanner.LookAhead() == '>')
            {
                Next();
                Next();
                continue;
            }

            // '>>' at the outermost level closes the instantiation
            if (t == '>' && paren + curly + square + angle == 0 && Scanner.LookAhead() == '>')
            {
                Next();
                Next();
                return;
            }

            switch (t)
            {
                case TkEos:
                    Scanner.SyntaxError("unfinished type instantiation");
                    return;
                case '(':
                    paren++;
                    break;
                case ')':
                    if (paren > 0)
                    {
                        paren--;
                    }

                    break;
                case '{':
                    curly++;
                    break;
                case '}':
                    if (curly > 0)
                    {
                        curly--;
                    }

                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    if (square > 0)
                    {
                        square--;
                    }

                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    if (angle > 0)
                    {
                        angle--;
                    }

                    break;
            }

            Next();
        }
    }

    public bool BlockFollow(bool withUntil)
    {
        switch (T)
        {
            case TkElse:
            case TkElseif:
            case TkEnd:
            case TkEos:
                return true;
            case TkUntil:
                return withUntil;
        }

        return false;
    }

    public void StatementList()
    {
        while (!BlockFollow(true))
        {
            if (T == TkReturn)
            {
                Statement();
                return;
            }

            Statement();
        }
    }

    public ExprDesc FieldSelector(ExprDesc e)
    {
        e = Function.ExpressionToAnyRegisterOrUpValue(e);
        Next(); // skip dot or colon
        return Function.Indexed(e, CheckNameAsExpression());
    }

    public ExprDesc Index()
    {
        Next(); // skip '['
        var e = Function.ExpressionToValue(Expression());
        CheckNext(']');
        return e;
    }

    public void Assignment(AssignmentTarget t, int variableCount)
    {
        CheckCondition(t.Description.IsVariable(), "syntax error");
        if (TestNext(','))
        {
            var e = SuffixedExpression();
            if (e.Kind != Kind.Indexed)
            {
                Function.CheckConflict(t, e);
            }

            CheckLimit(variableCount + Scanner.L.CallCount, MaxCallCount, "Go levels");
            Assignment(new(ref t, e), variableCount + 1);
        }
        else
        {
            CheckNext('=');
            var (e, n) = ExpressionList();
            if (n != variableCount)
            {
                Function.AdjustAssignment(variableCount, n, e);
                if (n > variableCount)
                {
                    Function.FreeRegisterCount -= n - variableCount; // remove extra values
                }
            }
            else
            {
                Function.StoreVariable(t.Description, Function.SetReturn(e));
                return; // avoid default
            }
        }

        Function.StoreVariable(
            t.Description,
            MakeExpression(Kind.NonRelocatable, Function.FreeRegisterCount - 1)
        );
    }

    public void ForBody(int @base, int line, int n, bool isNumeric)
    {
        Function.AdjustLocalVariables(3);
        CheckNext(TkDo);
        var prep = Function.OpenForBody(@base, n, isNumeric);
        Block();
        Function.CloseForBody(prep, @base, line, n, isNumeric);
    }

    public void ForNumeric(string name, int line)
    {
        var @base = Function.FreeRegisterCount;
        Function.MakeLocalVariable("(for index)");
        Function.MakeLocalVariable("(for limit)");
        Function.MakeLocalVariable("(for step)");
        Function.MakeLocalVariable(name);
        CheckNext('=');
        Expr();
        CheckNext(',');
        Expr();
        if (TestNext(','))
        {
            Expr();
        }
        else
        {
            // Default step is the integer 1 so integer loops stay on the integer fast path.
            Function.EncodeConstant(Function.FreeRegisterCount, Function.LongConstant(1));
            Function.ReserveRegisters(1);
        }

        ForBody(@base, line, 1, true);
        return;

        void Expr()
        {
            var e = Function.ExpressionToNextRegister(Expression());
            Assert(e.Kind == Kind.NonRelocatable);
        }
    }

    public void ForList(string name)
    {
        var n = 4;
        var @base = Function.FreeRegisterCount;
        Function.MakeLocalVariable("(for generator)");
        Function.MakeLocalVariable("(for state)");
        Function.MakeLocalVariable("(for control)");
        Function.MakeLocalVariable(name);
        while (TestNext(','))
        {
            Function.MakeLocalVariable(CheckName());
            if (T == ':')
            {
                Next();
                SkipTypeAnnotation();
            }

            n++;
        }

        CheckNext(TkIn);
        var line = Scanner.LineNumber;
        var (e, c) = ExpressionList();

        if (c == 1 && !e.HasMultipleReturns())
        {
            // Luau generalized iteration: a single expression that is not a call/vararg is
            // resolved by an internal helper, which produces the usual (iterator, state,
            // control) triple. A table iterates with `next`, an `__iter` metamethod is
            // honored, and a function stays its own iterator, so `pairs`/`ipairs`/multi-value
            // forms (handled below) keep behaving exactly as before. The value is evaluated
            // into the base register first and moved up so the helper can occupy the base
            // register the call result starts at.
            Function.ExpressionToNextRegister(e);
            Function.ReserveRegisters(1);
            Function.EncodeABC(OpCode.Move, @base + 1, @base, 0);
            Function.LoadBuiltin(@base, LuaBuiltin.IterResolver);
            var call = Function.EncodeABC(OpCode.Call, @base, 2, 4);
            Function.FixLine(line);
            Function.FreeRegisterCount = @base + 1;
            Function.AdjustAssignment(3, 1, Function.MakeExpression(Kind.Call, call));
        }
        else
        {
            Function.AdjustAssignment(3, c, e);
        }

        Function.CheckStack(3);
        ForBody(@base, line, n - 3, false);
    }

    public void ForStatement(int line)
    {
        Function.EnterBlock(true);
        Loops.Add(default);
        Next();
        var name = CheckName();
        if (T == ':')
        {
            Next();
            SkipTypeAnnotation();
        }

        switch (T)
        {
            case '=':
                ForNumeric(name, line);
                break;
            case ',':
            case TkIn:
                ForList(name);
                break;
            default:
                Scanner.SyntaxError("'=' or 'in' expected");
                break;
        }

        Scanner.CheckMatch(TkEnd, TkFor, line);
        Function.LeaveBlock();
        Loops.Shrink(Loops.Length - 1);
    }

    public int TestThenBlock(int escapes)
    {
        int jumpFalse;
        Next();
        var e = Expression();
        CheckNext(TkThen);
        if (T is TkGoto or TkBreak)
        {
            e = Function.GoIfFalse(e);
            Function.EnterBlock(false);
            GotoStatement(e.T);
            SkipEmptyStatements();
            if (BlockFollow(false))
            {
                Function.LeaveBlock();
                return escapes;
            }

            jumpFalse = Function.Jump();
        }
        else
        {
            e = Function.GoIfTrue(e);
            Function.EnterBlock(false);
            jumpFalse = e.F;
        }

        StatementList();
        Function.LeaveBlock();
        if (T is TkElse or TkElseif)
        {
            escapes = Function.Concatenate(escapes, Function.Jump());
        }

        Function.PatchToHere(jumpFalse);
        return escapes;
    }

    public void IfStatement(int line)
    {
        var escapes = TestThenBlock(NoJump);
        while (T == TkElseif)
        {
            escapes = TestThenBlock(escapes);
        }

        if (TestNext(TkElse))
        {
            Block();
        }

        Scanner.CheckMatch(TkEnd, TkIf, line);
        Function.PatchToHere(escapes);
    }

    public void Block()
    {
        Function.EnterBlock(false);
        StatementList();
        Function.LeaveBlock();
    }

    public void WhileStatement(int line)
    {
        Next();
        var top = Function.Label();
        var conditionExit = Condition();
        Function.EnterBlock(true);
        Loops.Add(default);
        CheckNext(TkDo);
        Block();
        // `continue` targets the back-jump: the point the loop body falls through to.
        Function.ContinueLabel(Function.Block.ActiveVariableCount);
        Function.JumpTo(top);
        Scanner.CheckMatch(TkEnd, TkWhile, line);
        Function.LeaveBlock();
        Loops.Shrink(Loops.Length - 1);
        Function.PatchToHere(conditionExit);
    }

    public void RepeatStatement(int line)
    {
        var top = Function.Label();
        Function.EnterBlock(true); // loop block
        Function.EnterBlock(false); // scope block
        Loops.Add(new() { IsRepeat = true, MinContinueLevel = -1 });
        Next();
        StatementList();
        Scanner.CheckMatch(TkUntil, TkRepeat, line);

        // `continue` targets the condition, which shares the body's scope: a local the
        // continue jumped over is undefined there (Luau rejects reading one), so its level is
        // armed for the duration of the condition.
        var loop = Loops[Loops.Length - 1];
        Loops.Shrink(Loops.Length - 1);
        Function.ContinueLabel(Function.Block.ActiveVariableCount);

        var savedFunction = RepeatConditionFunction;
        var savedLevel = RepeatConditionMinLevel;
        var savedLine = RepeatConditionLine;
        if (loop.MinContinueLevel >= 0)
        {
            RepeatConditionFunction = Function;
            RepeatConditionMinLevel = loop.MinContinueLevel;
            RepeatConditionLine = loop.MinContinueLine;
        }

        var conditionExit = Condition();
        RepeatConditionFunction = savedFunction;
        RepeatConditionMinLevel = savedLevel;
        RepeatConditionLine = savedLine;

        if (Function.Block.HasUpValue)
        {
            Function.PatchClose(conditionExit, Function.Block.ActiveVariableCount);
        }

        Function.LeaveBlock(); // finish scope
        Function.PatchList(conditionExit, top); // close loop
        Function.LeaveBlock(); // finish loop
    }

    public int Condition()
    {
        var e = Expression();
        if (e.Kind == Kind.Nil)
        {
            e.Kind = Kind.False;
        }

        return Function.GoIfTrue(e).F;
    }

    public void GotoStatement(int pc)
    {
        var line = Scanner.LineNumber;
        if (TestNext(TkGoto))
        {
            Function.MakeGoto(CheckName(), line, pc);
        }
        else
        {
            Next();
            Function.MakeGoto("break", line, pc);
        }
    }

    /// <summary>
    /// Luau `continue`: jumps to the end of the innermost loop (its `until` condition for a
    /// repeat loop). It reuses the pending-goto machinery of `break`; the difference is the
    /// label created by the loop, whose variable level is the loop body's *outer* level. That
    /// is what lets a `continue` jump over locals declared after it (Luau allows that for
    /// for/while) while still closing the upvalues those locals opened.
    /// </summary>
    public void ContinueStatement(int line)
    {
        if (Loops.Length == 0)
        {
            // Also the case of a `continue` inside a function nested in a loop: the loop
            // stack is truncated when the nested body starts.
            Scanner.SyntaxError("continue statement must be inside a loop");
            return;
        }

        var index = Loops.Length - 1;
        var context = Loops[index];
        if (context.MinContinueLevel < 0 || Function.ActiveVariableCount < context.MinContinueLevel)
        {
            context.MinContinueLevel = Function.ActiveVariableCount;
            context.MinContinueLine = line;
            Loops[index] = context;
        }

        Function.MakeGoto("continue", line, Function.Jump());
    }

    /// <summary>
    /// True when a statement that starts with the identifier `continue` is the Luau continue
    /// statement rather than an expression using a variable of that name (`continue()`,
    /// `continue.x = 1`, `continue = 1`, `continue[1] = 2`, ...).
    /// </summary>
    bool IsContinueStatement()
    {
        return Scanner.LookAhead()
            is not ('=' or ',' or '.' or '[' or ':' or '(' or '{' or TkString);
    }

    public void SkipEmptyStatements()
    {
        while (T == ';' || T == TkDoubleColon)
        {
            Statement();
        }
    }

    public void LabelStatement(string label, int line)
    {
        Function.CheckRepeatedLabel(label);
        CheckNext(TkDoubleColon);
        var l = Function.MakeLabel(label, line);
        SkipEmptyStatements();
        if (BlockFollow(false))
        {
            ActiveLabels[l].ActiveVariableCount = Function.Block.ActiveVariableCount;
        }

        Function.FindGotos(l);
    }

    public void ParameterList()
    {
        var n = 0;
        var isVarArg = false;
        if (T != ')')
        {
            for (var first = true; first || (!isVarArg && TestNext(',')); first = false)
            {
                switch (T)
                {
                    case TkName:
                        Function.MakeLocalVariable(CheckName());
                        if (T == ':')
                        {
                            Next();
                            SkipTypeAnnotation();
                        }

                        n++;
                        break;
                    case TkDots:
                        Next();
                        isVarArg = true;
                        if (T == ':')
                        {
                            Next();
                            SkipTypeAnnotation();
                        }

                        break;
                    default:
                        Scanner.SyntaxError("<name> or '...' expected");
                        break;
                }
            }
        }

        // TODO the following lines belong in a *function method
        Function.Proto.IsVarArg = isVarArg;
        Function.AdjustLocalVariables(n);
        Function.Proto.ParameterCount = Function.ActiveVariableCount;
        Function.ReserveRegisters(Function.ActiveVariableCount);
    }

    public ExprDesc Body(bool isMethod, int line, bool discard = false, string? name = null)
    {
        Function.OpenFunction(line);
        if (name != null)
        {
            Function.Proto.Name = name;
        }
        else if (PendingFunctionName != null)
        {
            Function.Proto.Name = PendingFunctionName;
            PendingFunctionName = null;
        }

        if (T == '<')
        {
            // generic type parameters, e.g. 'function f<T, U...>()'
            SkipGenericTypeParameters();
        }

        CheckNext('(');
        if (isMethod)
        {
            Function.MakeLocalVariable("self");
            Function.AdjustLocalVariables(1);
        }

        ParameterList();
        CheckNext(')');
        if (T == ':')
        {
            // return type annotation
            Next();
            SkipTypeAnnotation();
        }

        // A `continue` in the body may not target a loop of the enclosing function.
        var savedLoopCount = Loops.Length;
        StatementList();
        Loops.Shrink(savedLoopCount);
        Function.Proto.LastLineDefined = Scanner.LineNumber;
        Scanner.CheckMatch(TkEnd, TkFunction, line);
        if (discard)
        {
            // Luau type function: parse the body but do not create a closure
            Function.CloseFunctionDiscard();
            return default;
        }

        return Function.CloseFunction();
    }

    public (ExprDesc, bool IsMethod) FunctionName(out string name)
    {
        var firstName = CheckName();
        var e = Function.SingleVariable(firstName);
        name = firstName;
        for (; T == '.'; )
        {
            e = Function.ExpressionToAnyRegisterOrUpValue(e);
            Next(); // skip '.'
            name = CheckName();
            e = Function.Indexed(e, Function.EncodeString(name));
        }

        if (T == ':')
        {
            e = Function.ExpressionToAnyRegisterOrUpValue(e);
            Next(); // skip ':'
            name = CheckName();
            e = Function.Indexed(e, Function.EncodeString(name));
            return (e, true);
        }

        return (e, false);
    }

    public void FunctionStatement(int line)
    {
        Next();
        var (v, m) = FunctionName(out var name);
        Function.StoreVariable(v, Body(m, line, name: name));
        Function.FixLine(line);
    }

    public void LocalFunction()
    {
        var name = CheckName();
        Function.MakeLocalVariable(name);
        Function.AdjustLocalVariables(1);
        Function.LocalVariable(Body(false, Scanner.LineNumber, name: name).Info).StartPc = Function
            .Proto
            .CodeList
            .Length;
    }

    public void LocalStatement()
    {
        var v = 0;
        string? firstName = null;
        for (var first = true; first || TestNext(','); first = false)
        {
            var name = CheckName();
            firstName ??= name;
            Function.MakeLocalVariable(name);
            if (T == ':')
            {
                Next();
                SkipTypeAnnotation();
            }

            v++;
        }

        if (TestNext('='))
        {
            // `local X = function() ... end`: remember X so the anonymous function's
            // prototype gets the variable's name (e.g. mainmenu's `local MainMenu = function()`).
            if (v == 1)
            {
                PendingFunctionName = firstName;
            }

            var (e, n) = ExpressionList();
            Function.AdjustAssignment(v, n, e);
        }
        else
        {
            ExprDesc e = default;
            Function.AdjustAssignment(v, 0, e);
        }

        Function.AdjustLocalVariables(v);
        PendingFunctionName = null;
    }

    /// <summary>Maps a Luau compound-assignment token to its binary operator.</summary>
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

    /// <summary>
    /// Luau `const` binding: `const name {, name} = explist` or `const function name() ...`.
    /// The binding is immutable — assigning to it, including through a compound assignment or
    /// a captured upvalue, is a compile error — but the value it refers to is not.
    /// `const` is a contextual keyword: code using it as a name keeps working, because only a
    /// `const` followed by a name or `function` is treated as a declaration.
    /// </summary>
    public void ConstStatement()
    {
        Next(); // consume 'const'

        if (TestNext(TkFunction))
        {
            LocalFunction();
            Function.MarkConst(Function.ActiveVariableCount - 1, default);
            return;
        }

        var v = 0;
        string? firstName = null;
        var firstIndex = Function.ActiveVariableCount;
        for (var first = true; first || TestNext(','); first = false)
        {
            var name = CheckName();
            firstName ??= name;
            Function.MakeLocalVariable(name);
            if (T == ':')
            {
                Next();
                SkipTypeAnnotation();
            }

            v++;
        }

        // Luau requires an initializer in a const declaration.
        if (!TestNext('='))
        {
            Scanner.SyntaxError("Missing initializer in const declaration");
        }

        if (v == 1)
        {
            PendingFunctionName = firstName;
        }

        var (e, n) = ExpressionList();

        // Only a genuine single literal initializer is inlined. The *parsed* expression is
        // inspected rather than the first token, because the compiler folds expressions such
        // as `1 + 2` into a single literal.
        var literal = v == 1 && n == 1 ? CaptureConstLiteral(e) : default;

        Function.AdjustAssignment(v, n, e);
        Function.AdjustLocalVariables(v);

        for (var i = 0; i < v; i++)
        {
            Function.MarkConst(firstIndex + i, v == 1 ? literal : default);
        }

        PendingFunctionName = null;
    }

    /// <summary>
    /// Builds the inlinable literal of a single-variable `const` declaration. Only numbers,
    /// booleans and nil are captured: tables/functions keep their identity through the local,
    /// and strings are left alone because a constant index is only valid inside the prototype
    /// that created it.
    /// </summary>
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
    /// Skips a run of Luau attributes (`@native`, `@[deprecated { use = "x" }]`). Attribute
    /// names and parameter literals are not validated because this implementation ignores
    /// them, but every delimiter is balanced so the token stream stays aligned.
    /// </summary>
    public void SkipAttributes()
    {
        while (T == '@')
        {
            Next(); // consume '@'
            if (T != '[')
            {
                CheckName(); // '@name' takes no parameters
                continue;
            }

            Next(); // consume '['
            while (true)
            {
                if (TestNext(','))
                {
                    continue;
                }

                if (TestNext(']'))
                {
                    break;
                }

                CheckName();
                SkipAttributeParameters();
            }
        }
    }

    /// <summary>Skips the optional literal parameters of one attribute: `(...)`, a table or a string.</summary>
    void SkipAttributeParameters()
    {
        switch (T)
        {
            case TkString:
                Next();
                return;
            case '{':
                SkipBalanced('{', '}');
                return;
            case '(':
                SkipBalanced('(', ')');
                return;
        }
    }

    void SkipBalanced(int open, int close)
    {
        var depth = 0;
        while (true)
        {
            if (T == TkEos)
            {
                Scanner.SyntaxError("unfinished attribute");
                return;
            }

            if (T == open)
            {
                depth++;
            }
            else if (T == close)
            {
                depth--;
                if (depth == 0)
                {
                    Next();
                    return;
                }
            }

            Next();
        }
    }

    /// <summary>Parses a function declaration that carries Luau attributes.</summary>
    public void AttributeStatement(int line)
    {
        SkipAttributes();

        switch (T)
        {
            case TkFunction:
                FunctionStatement(line);
                return;
            case TkLocal:
                Next();
                CheckNext(TkFunction);
                LocalFunction();
                return;
        }

        if (T == TkName && Scanner.Token.S == "const" && Scanner.LookAhead() == TkFunction)
        {
            ConstStatement();
            return;
        }

        Scanner.SyntaxError(
            "Expected 'function', 'local function' or 'const function' after attribute"
        );
    }

    public void ExpressionStatement()
    {
        var e = SuffixedExpression();
        var compoundOperator = CompoundAssignmentOperator(T);
        if (compoundOperator != OprNoBinary)
        {
            CompoundAssignment(e, compoundOperator);
            return;
        }

        if (T == '=' || T == ',')
        {
            Assignment(new(ref Unsafe.NullRef<AssignmentTarget>(), e), 1);
        }
        else
        {
            CheckCondition(e.Kind == Kind.Call, "syntax error");
            Function.Instruction(e).C = 1; // call statement uses no results
        }
    }

    /// <summary>
    /// `target op= value` (Luau): a single variable target that is evaluated once, with the
    /// right-hand side parsed as a full expression (`a += b + c` is `a = a + (b + c)`).
    /// </summary>
    public void CompoundAssignment(ExprDesc target, int op)
    {
        CheckCondition(target.Kind is Kind.Local or Kind.UpValue or Kind.Indexed, "syntax error");

        var line = Scanner.LineNumber;
        Next(); // consume the compound operator

        var current = Function.BeginCompoundAssignment(target, op);
        var value = Expression();
        Function.EndCompoundAssignment(target, op, current, value, line);
    }

    public void ReturnStatement()
    {
        var f = Function;
        if (BlockFollow(true) || T == ';')
        {
            f.ReturnNone();
        }
        else
        {
            var (e, n) = ExpressionList();
            f.Return(e, n);
        }

        TestNext(';');
    }

    public void TypeStatement(int line)
    {
        // skip 'type' keyword
        Next();
        if (T == TkFunction)
        {
            // 'type function NAME funcbody' — parsed and discarded
            Next();
            CheckName();
            Body(false, line, true);
            return;
        }

        CheckName();
        if (T == '<')
        {
            SkipGenericTypeParameters();
        }

        CheckNext('=');
        SkipTypeAnnotation();
    }

    public void Statement()
    {
        var line = Scanner.LineNumber;
        using var _ = EnterLevel();
        switch (T)
        {
            case ';':
                Next();
                break;
            case TkIf:
                IfStatement(line);
                break;
            case TkWhile:
                WhileStatement(line);
                break;
            case TkDo:
                Next();
                Block();
                Scanner.CheckMatch(TkEnd, TkDo, line);
                break;
            case TkFor:
                ForStatement(line);
                break;
            case TkRepeat:
                RepeatStatement(line);
                break;
            case TkFunction:
                FunctionStatement(line);
                break;
            case TkLocal:
                Next();
                if (TestNext(TkFunction))
                {
                    LocalFunction();
                }
                else
                {
                    LocalStatement();
                }

                break;
            case TkDoubleColon:
                Next();
                LabelStatement(CheckName(), line);
                break;
            case '@':
                // Luau attributes: parsed and ignored.
                AttributeStatement(line);
                break;
            case TkReturn:
                Next();
                ReturnStatement();
                break;
            case TkBreak:
            case TkGoto:
                GotoStatement(Function.Jump());
                break;
            default:
                if (T == TkName)
                {
                    var name = Scanner.Token.S;
                    if (name == "continue" && IsContinueStatement())
                    {
                        Next(); // consume 'continue'
                        ContinueStatement(line);
                        break;
                    }

                    if (
                        name == "const"
                        && Scanner.LookAhead() is TkName or TkFunction
                    )
                    {
                        ConstStatement();
                        break;
                    }

                    if (name == "type" && Scanner.LookAhead() is TkName or TkFunction)
                    {
                        TypeStatement(line);
                        break;
                    }

                    if (
                        name == "export"
                        && Scanner.LookAhead() == TkName
                        && Scanner.LookAheadToken.S == "type"
                    )
                    {
                        Next(); // skip 'export'
                        if (Scanner.LookAhead() is TkName or TkFunction)
                        {
                            TypeStatement(line);
                        }
                        else
                        {
                            Scanner.SyntaxError("type name expected");
                        }

                        break;
                    }
                }

                ExpressionStatement();
                break;
        }

        Assert(
            Function.Proto.MaxStackSize >= Function.FreeRegisterCount
                && Function.FreeRegisterCount >= Function.ActiveVariableCount
        );
        Function.FreeRegisterCount = Function.ActiveVariableCount;
    }

    internal void MainFunction()
    {
        Function.OpenMainFunction();
        Next();
        StatementList();
        Scanner.Check(TkEos);
        Function = Function.CloseMainFunction();
    }

    public static Prototype Parse(LuaState l, TextReader r, string name)
    {
        using var internPool = new StringInternPool(4);
        using var p = Get(
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
        p.MainFunction();
        return f.Proto.CreatePrototypeAndRelease();
    }

    public static void Dump(
        Prototype prototype,
        IBufferWriter<byte> writer,
        bool useLittleEndian = true
    )
    {
        DumpState state = new(writer, useLittleEndian ^ BitConverter.IsLittleEndian);
        state.Dump(prototype);
    }

    public static byte[] Dump(Prototype prototype, bool useLittleEndian = true)
    {
        ArrayBufferWriter<byte> writer = new();
        Dump(prototype, writer, useLittleEndian);
        return writer.WrittenSpan.ToArray();
    }

    public static Prototype Undump(ReadOnlySpan<byte> span, ReadOnlySpan<char> name)
    {
        if (name.Length > 0)
        {
            name = name[0] switch
            {
                '@' or '=' => name[1..],
                '\e' => "binary string",
                _ => name,
            };
        }

        using var internPool = new StringInternPool(4);
        UndumpState state = new(span, name, internPool);
        return state.Undump();
    }
}
