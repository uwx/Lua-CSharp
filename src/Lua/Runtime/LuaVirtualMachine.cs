using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FixedMathSharp;
using Lua.Internal;
using Lua.Internal.CompilerServices;
using NFMWorldLibrary.FixedMath;

// ReSharper disable MethodHasAsyncOverload

// ReSharper disable InconsistentNaming

namespace Lua.Runtime;

[SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly")]
public static partial class LuaVirtualMachine
{
    class VirtualMachineExecutionContext : IPoolNode<VirtualMachineExecutionContext>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VirtualMachineExecutionContext Get(
            LuaState state,
            in CallStackFrame frame,
            CancellationToken cancellationToken
        )
        {
            if (!pool.TryPop(out var executionContext))
            {
                executionContext = new();
            }

            executionContext.Init(state, frame, cancellationToken);
            return executionContext;
        }

        void Init(LuaState state, in CallStackFrame frame, CancellationToken cancellationToken)
        {
            Stack = state.Stack;
            State = state;
            LuaClosure = (LuaClosure)frame.Function;
            FrameBase = frame.Base;
            VariableArgumentCount = frame.VariableArgumentCount;
            CurrentReturnFrameBase = frame.ReturnBase;
            CancellationToken = cancellationToken;
            Pc = -1;
            Instruction = default;
            PostOperation = PostOperationType.None;
            BaseCallStackCount = state.CallStackFrameCount;
            LastHookPc = -1;
            Task = default;
            NextIteratorTable = null;
            NextIteratorSlot = -1;
        }

        public LuaGlobalState GlobalState => State.GlobalState;

        public LuaStack Stack = default!;
        public LuaClosure LuaClosure = default!;
        public LuaState State = default!;

        public Prototype Prototype => LuaClosure.Proto;

        public int FrameBase;
        public int VariableArgumentCount;
        public CancellationToken CancellationToken;
        public int Pc;
        public Instruction Instruction;
        public int CurrentReturnFrameBase;
        public ValueTask<int> Task;
        public int LastHookPc;

        /// <summary>
        /// One-entry traversal cursor for the builtin <c>next</c> fast path in
        /// <see cref="TForCall"/>: the table of the loop being stepped and the string-dictionary
        /// slot its control key occupies. Lets the next step resume the walk without
        /// re-hashing the key. Only ever set from a lookup on that table, and validated
        /// against the slot's current contents before use.
        /// </summary>
        public LuaTable? NextIteratorTable;
        public int NextIteratorSlot;

        public bool IsTopLevel => BaseCallStackCount == State.CallStackFrameCount;

        public int BaseCallStackCount;

        public PostOperationType PostOperation;

        static LinkedPool<VirtualMachineExecutionContext> pool;

        VirtualMachineExecutionContext? nextNode;

        ref VirtualMachineExecutionContext? IPoolNode<VirtualMachineExecutionContext>.NextNode =>
            ref nextNode;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Pop(Instruction instruction, int frameBase)
        {
            var count = instruction.B - 1;
            var src = instruction.A + frameBase;
            if (count == -1)
            {
                count = Stack.Count - src;
            }

            return PopFromBuffer(src, count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool PopFromBuffer(int src, int srcCount)
        {
            var result = Stack.GetBuffer().Slice(src, srcCount);
            Re:
            var frames = State.GetCallStackFrames();
            if (frames.Length == BaseCallStackCount)
            {
                var returnBase = frames[^1].ReturnBase;
                if (src != returnBase)
                {
                    result.CopyTo(Stack.AsSpan()[returnBase..]);
                }

                Stack.PopUntil(returnBase + srcCount);
                return false;
            }

            ref readonly var frame = ref frames[^1];
            Pc = frame.CallerInstructionIndex;
            State.LastPc = Pc;
            ref readonly var lastFrame = ref frames[^2];
            LuaClosure = Unsafe.As<LuaClosure>(lastFrame.Function);
            CurrentReturnFrameBase = frame.ReturnBase;
            var callInstruction = Prototype.Code[Pc];
            if (callInstruction.OpCode == OpCode.TailCall)
            {
                State.PopCallStackFrame();
                goto Re;
            }

            FrameBase = lastFrame.Base;
            VariableArgumentCount = lastFrame.VariableArgumentCount;

            var opCode = callInstruction.OpCode;
            if (opCode is OpCode.Eq or OpCode.Lt or OpCode.Le)
            {
                var compareResult = srcCount > 0 && result[0].ToBoolean();
                if ((frame.Flags & CallStackFrameFlags.ReversedLe) != 0)
                {
                    compareResult = !compareResult;
                }

                if (compareResult != (callInstruction.A == 1))
                {
                    Pc++;
                }

                State.PopCallStackFrameWithStackPop();
                return true;
            }

            var target = callInstruction.A + FrameBase;
            var targetCount = result.Length;
            switch (opCode)
            {
                case OpCode.Call:
                {
                    var c = callInstruction.C;
                    if (c != 0)
                    {
                        targetCount = c - 1;
                    }

                    break;
                }
                case OpCode.TForCall:
                    target += 3;
                    targetCount = callInstruction.C;
                    break;
                case OpCode.Self:
                    Stack.Get(target) = result.Length == 0 ? LuaValue.Nil : result[0];
                    State.PopCallStackFrameWithStackPop(target + 2);
                    return true;
                case OpCode.SetTable or OpCode.SetTabUp:
                    target = frame.Base;
                    targetCount = 0;
                    break;
                // Other opcodes has one result
                default:
                    Stack.Get(target) = result.Length == 0 ? LuaValue.Nil : result[0];
                    State.PopCallStackFrameWithStackPop(Math.Max(target + 1, CurrentReturnFrameBase));
                    return true;
            }

            Stack.EnsureCapacity(target + targetCount);
            if (0 < targetCount && src != target)
            {
                if (targetCount < result.Length)
                {
                    result = result.Slice(0, targetCount);
                }

                result.CopyTo(Stack.GetBuffer().Slice(target, targetCount));
            }

            Stack.PopUntil(target + Math.Min(targetCount, srcCount));
            Stack.NotifyTop(target + targetCount);
            State.PopCallStackFrame();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(in CallStackFrame frame)
        {
            Pc = -1;
            LuaClosure = (LuaClosure)frame.Function;
            FrameBase = frame.Base;
            CurrentReturnFrameBase = frame.ReturnBase;
            VariableArgumentCount = frame.VariableArgumentCount;
        }

        public void PopOnTopCallStackFrames()
        {
            var count = State.CallStackFrameCount;
            if (count == BaseCallStackCount)
            {
                return;
            }

            State.PopCallStackFrameUntil(BaseCallStackCount);
        }

        bool ExecutePostOperation(int varArgs, PostOperationType postOperation)
        {
            var stackCount = Stack.Count;
            var resultsSpan = Stack.GetBuffer()[CurrentReturnFrameBase..];
            var lastTop = CurrentReturnFrameBase - varArgs;
            switch (postOperation)
            {
                case PostOperationType.Nop:
                    break;
                case PostOperationType.SetResult:
                    var RA = Instruction.A + FrameBase;
                    Stack.Get(RA) =
                        stackCount > CurrentReturnFrameBase
                            ? Stack.Get(CurrentReturnFrameBase)
                            : LuaValue.Nil;
                    Stack.NotifyTop(RA + 1);
                    Stack.PopUntil(Math.Max(RA + 1, lastTop));
                    break;
                case PostOperationType.TForCall:
                    TForCallPostOperation(this);
                    break;
                case PostOperationType.Call:
                    CallPostOperation(this);
                    break;
                case PostOperationType.TailCall:
                    if (
                        !PopFromBuffer(CurrentReturnFrameBase, Stack.Count - CurrentReturnFrameBase)
                    )
                    {
                        return false;
                    }

                    break;
                case PostOperationType.Self:
                    SelfPostOperation(this, lastTop, resultsSpan);
                    break;
                case PostOperationType.Compare:
                    ComparePostOperation(this, lastTop, resultsSpan);
                    break;
            }

            return true;
        }

        /// <summary>
        /// Drives this closure's instruction dispatch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Threading invariant that every opcode handler must uphold.</b> Handlers do not await
        /// their own pending work; they park it in <see cref="Task"/> and return <c>false</c>, and
        /// this loop awaits it. That means a handler's task is already live -- and can already be
        /// running its continuation on a thread-pool thread -- while the dispatching thread is
        /// still unwinding out of <see cref="MoveNext"/> and has not yet reached
        /// <c>await Task</c>. Two threads are genuinely executing against one
        /// <see cref="LuaState"/> for the width of that window. This is not hypothetical: a
        /// re-entrancy guard over <c>MoveNext</c> and the resume block below observes it several
        /// times per full test-suite run, always in the same shape (the dispatching thread inside
        /// <c>MoveNext</c>, a thread-pool thread inside a nested closure's resume block).
        /// </para>
        /// <para>
        /// What makes that safe is an invariant, not a lock: <b>once a handler has created its
        /// task, it must not touch anything the task can also touch</b> -- not
        /// <see cref="LuaState.Stack"/>, not the call stack, and not the fields of this context.
        /// Every handler therefore finishes its stack and frame bookkeeping and writes
        /// <see cref="PostOperation"/> *before* the call that produces the task, and afterwards
        /// writes only <see cref="Task"/> (which no task ever reads) and its own locals.
        /// </para>
        /// <para>
        /// <see cref="Concat(VirtualMachineExecutionContext)"/> is the one handler where this is
        /// delicate, because its pending task is an async helper that closes over *this* context
        /// and keeps writing to it rather than running against a nested context of its own. It
        /// used to write <c>PostOperation = None</c> after starting that task, which raced the
        /// helper's own <c>DontPop</c> write; when the dispatching thread won, the resume code
        /// below popped a frame belonging to the caller and collapsed the call stack. Adding a
        /// handler that awaits or mutates shared state after creating its task would reintroduce
        /// the same class of bug, silently and rarely.
        /// </para>
        /// </remarks>
        [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<int> ExecuteClosureAsyncImpl()
        {
            var returnFrameBase = CurrentReturnFrameBase;
            var toCatchFlag = false;
            try
            {
                while (MoveNext(this))
                {
                    toCatchFlag = true;
                    if (!Task.IsCompleted && State.IsSyncExecution)
                    {
                        ThrowAttemptedToYieldDuringSyncExecution(State);
                    }

                    await Task;
                    Task = default;

                    // A Concat that suspended can only have suspended inside its metamethod call,
                    // and Concat(context, target, total) sets DontPop unconditionally before it
                    // completes -- so by the time this resume runs (which is ordered after that
                    // completion), DontPop is the only value that can legitimately be here, and
                    // seeing None means that write was raced away by the dispatcher. That is the
                    // bug documented on Concat(VirtualMachineExecutionContext); this assert is
                    // what catches it coming back. Nop is excluded because the per-instruction
                    // hook legitimately suspends with Nop while Instruction is still the Concat.
                    Debug.Assert(
                        Instruction.OpCode != OpCode.Concat
                            || PostOperation != PostOperationType.None,
                        "Concat resumed with PostOperation == None: the helper's DontPop was "
                            + "overwritten, and the frame pop below will collapse the call stack."
                    );

                    ref readonly var frame = ref State.GetCurrentFrame();
                    CurrentReturnFrameBase = frame.ReturnBase;
                    var variableArgumentCount = frame.VariableArgumentCount;
                    if (
                        PostOperation
                        is not (PostOperationType.TailCall or PostOperationType.DontPop)
                    )
                    {
                        State.PopCallStackFrame();
                    }

                    if (!ExecutePostOperation(variableArgumentCount, PostOperation))
                    {
                        break;
                    }

                    toCatchFlag = false;

                    ThrowIfCancellationRequested();
                }

                return State.Stack.Count - returnFrameBase;
            }
            catch (Exception e)
            {
                if (toCatchFlag)
                {
                    State.CloseUpValues(FrameBase);
                    if (e is not (LuaRuntimeException or LuaCanceledException))
                    {
                        Exception newException =
                            e is OperationCanceledException
                                ? new LuaCanceledException(State, CancellationToken, e)
                                : new LuaRuntimeException(State, e);
                        PopOnTopCallStackFrames();
                        throw newException;
                    }

                    PopOnTopCallStackFrames();
                }

                throw;
            }
            finally
            {
                pool.TryPush(this);
            }

            static void ThrowAttemptedToYieldDuringSyncExecution(LuaState state)
            {
                throw new LuaYieldException(
                    state,
                    "attempt to yield during synchronous execution"
                );
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ThrowIfCancellationRequested()
        {
            if (!CancellationToken.IsCancellationRequested)
            {
                return;
            }

            Throw();

            void Throw()
            {
                GetstateWithCurrentPc(this).ThrowIfCancellationRequested(CancellationToken);
            }
        }
    }

    enum PostOperationType
    {
        None,
        Nop,
        SetResult,
        TForCall,
        Call,
        TailCall,
        Self,
        Compare,
        DontPop,
    }

    internal static ValueTask<int> ExecuteClosureAsync(
        LuaState state,
        CancellationToken cancellationToken
    )
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new LuaStackOverflowException();
        }

        ref readonly var frame = ref state.GetCurrentFrame();

        var context = VirtualMachineExecutionContext.Get(state, in frame, cancellationToken);

        return context.ExecuteClosureAsyncImpl();
    }

    static long DummyHookCount;
    static bool DummyLineHookEnabled;
    static bool DummyHooksActive;

    static bool MoveNext(VirtualMachineExecutionContext context)
    {
        try
        {
            // This is a label to restart the execution when new function is called or restarted
            Restart:
            ref var instructionsHead = ref Unsafe.AsRef(in context.Prototype.Code[0]);
            var frameBase = context.FrameBase;
            var stack = context.Stack;
            stack.EnsureCapacity(frameBase + context.Prototype.MaxStackSize);
            ref var constHead = ref MemoryMarshalEx.UnsafeElementAt(context.Prototype.Constants, 0);
            ref var lineHookFlag = ref context.State.IsInHook
                ? ref DummyLineHookEnabled
                : ref context.State.IsLineHookEnabled;
            ref var hookCount = ref context.State.IsInHook
                ? ref DummyHookCount
                : ref context.State.HookCount;
            // Fast path: skip all per-instruction hook bookkeeping when no count/line
            // hooks are installed. This is a ref so sethook()/removehook() mid-execution
            // still take effect immediately.
            ref var hooksActive = ref context.State.IsInHook
                ? ref DummyHooksActive
                : ref context.State.HooksEnabled;
            goto Loop;

            LineHook:
            {
                context.LastHookPc = context.Pc;
                if (ExecutePerInstructionHook(context))
                {
                    {
                        context.PostOperation = PostOperationType.Nop;
                        return true;
                    }
                }

                --context.Pc;
            }

            Loop:
            while (true)
            {
                var instruction = Unsafe.Add(ref instructionsHead, ++context.Pc);
                context.Instruction = instruction;
                LuaVmDiagnostics.CountInstruction();
                if (hooksActive)
                {
                    if (--hookCount == 0 || (lineHookFlag && context.Pc != context.LastHookPc))
                    {
                        goto LineHook;
                    }

                    context.LastHookPc = -1;
                }
                var iA = instruction.A;
                var opCode = instruction.OpCode;
                switch (opCode)
                {
                    case OpCode.Move:
                        OpMove(stack, frameBase, iA, instruction);
                        continue;
                    case OpCode.LoadK:
                        OpLoadK(stack, iA, frameBase, ref constHead, instruction);
                        continue;
                    case OpCode.LoadKX:
                        OpLoadKX(context, stack, iA, frameBase, ref constHead, ref instructionsHead);
                        continue;
                    case OpCode.LoadBool:
                        OpLoadBool(context, stack, iA, frameBase, instruction);
                        continue;
                    case OpCode.LoadBuiltin:
                        OpLoadBuiltin(context, stack, iA, frameBase, instruction);
                        continue;
                    case OpCode.LoadNil:
                        int ra1;
                        OpLoadNil(iA, frameBase, instruction, stack);
                        continue;
                    case OpCode.GetUpVal:
                        OpGetUpVal(context, stack, iA, frameBase, instruction);
                        continue;
                    case OpCode.GetTabUp:
                    case OpCode.GetTable:
                        if (OpGetTable(context, stack, frameBase, ref constHead, instruction, out var doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.GetImport:
                        if (OpGetImport(context, stack, frameBase, ref constHead, ref instructionsHead, instruction, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.DupTable:
                        OpDupTable(context, stack, iA, frameBase, ref instructionsHead);
                        continue;
                    case OpCode.SetTabUp:
                    case OpCode.SetTable:
                        if (OpSetTable(context, stack, frameBase, ref constHead, instruction, opCode, iA, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.SetUpVal:
                        OpSetUpVal(context, instruction, stack, iA, frameBase);
                        continue;
                    case OpCode.NewTable:
                        OpNewTable(stack, iA, frameBase, instruction);
                        continue;
                    case OpCode.Self:
                        if (OpSelf(context, stack, frameBase, ref constHead, instruction, iA, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.Add:
                    case OpCode.Sub:
                    case OpCode.Mul:
                    case OpCode.Div:
                    case OpCode.Mod:
                    case OpCode.Pow:
                    case OpCode.IDiv:
                        if (OpBinaryArith(context, stack, frameBase, ref constHead, instruction, iA, opCode, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.Unm:
                        if (OpUnaryArith(context, stack, frameBase, instruction, iA, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.Not:
                        OpNot(stack, frameBase, iA, instruction);
                        continue;
                    case OpCode.Len:
                        if (OpLen(context, stack, frameBase, instruction, iA, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.Concat:
                        Markers.Concat();
                        if (Concat(context))
                        {
                            // if (doRestart) goto Restart;
                            continue;
                        }

                        return true;
                    case OpCode.Jmp:
                        OpJmp(context, instruction, iA, frameBase);
                        continue;
                    case OpCode.Eq:
                        if (OpEq(context, stack, frameBase, ref constHead, instruction, iA, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.Lt:
                    case OpCode.Le:
                        if (OpBinaryCmp(context, stack, frameBase, ref constHead, instruction, opCode, iA, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.JmpIfEqK:
                    case OpCode.JmpIfNeK:
                        OpJmpIf(context, ref instructionsHead, stack, iA, frameBase, ref constHead, opCode, instruction);
                        continue;
                    case OpCode.Test:
                        OpTest(context, stack, iA, frameBase, instruction);
                        continue;
                    case OpCode.TestSet:
                        OpTestSet(context, stack, instruction, frameBase, iA);
                        continue;
                    case OpCode.Call:
                        Markers.Call();
                        if (Call(context, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.TailCall:
                        Markers.TailCall();
                        if (TailCall(context, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            if (context.IsTopLevel)
                            {
                                goto End;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.Return:
                        Markers.Return();
                        context.State.CloseUpValues(frameBase);
                        if (context.Pop(instruction, frameBase))
                        {
                            goto Restart;
                        }

                        goto End;
                    case OpCode.ForLoop:
                        OpForLoop(context, stack, iA, frameBase, instruction);
                        continue;
                    case OpCode.ForPrep:
                        if (OpForPrep(context, stack, iA, frameBase, instruction))
                            return true;
                        continue;
                    case OpCode.TForCall:
                        Markers.TForCall();

                        if (TForCall(context, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        return true;
                    case OpCode.TForLoop:
                        Markers.TForLoop();
                        ref var forState = ref stack.Get(iA + frameBase + 1);

                        if (forState.Type is not LuaValueType.Nil)
                        {
                            Unsafe.Add(ref forState, -1) = forState;
                            context.Pc += instruction.SBx;
                        }

                        continue;
                    case OpCode.SetList:
                        Markers.SetList();
                        SetList(context);
                        continue;
                    case OpCode.Closure:
                        Markers.Closure();

                        ra1 = iA + frameBase + 1;
                        stack.EnsureCapacity(ra1);
                        stack.Get(ra1 - 1) = new LuaClosure(
                            context.State,
                            context.Prototype.ChildPrototypes[instruction.Bx]
                        );
                        stack.NotifyTop(ra1);

                        continue;
                    case OpCode.VarArg:
                        Markers.VarArg();

                        VarArg(context);

                        [MethodImpl(MethodImplOptions.NoInlining)]
                        static void VarArg(VirtualMachineExecutionContext context)
                        {
                            var instruction = context.Instruction;
                            var iA = instruction.A;
                            var frameBase = context.FrameBase;
                            var frameVariableArgumentCount = context.VariableArgumentCount;
                            var count =
                                instruction.B == 0 ? frameVariableArgumentCount : instruction.B - 1;
                            var ra = iA + frameBase;
                            var stack = context.Stack;
                            stack.EnsureCapacity(ra + count);
                            ref var stackHead = ref stack.Get(0);
                            for (var i = 0; i < count; i++)
                            {
                                Unsafe.Add(ref stackHead, ra + i) =
                                    frameVariableArgumentCount > i
                                        ? Unsafe.Add(
                                            ref stackHead,
                                            frameBase - (frameVariableArgumentCount - i)
                                        )
                                        : default;
                            }

                            stack.NotifyTop(ra + count);
                        }

                        continue;
                    case OpCode.ExtraArg:
                    default:
                        ThrowLuaNotImplementedException(context, context.Instruction.OpCode);
                        return true;
                }
            }

            End:
            context.PostOperation = PostOperationType.None;
            return false;
        }
        catch (Exception e)
        {
            context.State.CloseUpValues(context.FrameBase);
            if (e is not (LuaRuntimeException or LuaCanceledException))
            {
                Exception newException =
                    e is OperationCanceledException
                        ? new LuaCanceledException(context.State, context.CancellationToken, e)
                        : new LuaRuntimeException(context.State, e);
                context.PopOnTopCallStackFrames();
                throw newException;
            }

            context.PopOnTopCallStackFrames();
            throw;
        }
    }

    private static bool OpForPrep(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        Instruction instruction)
    {
        Markers.ForPrep();
        ref var indexRef = ref stack.Get(iA + frameBase);

        // Integer fast path: all three control values are already integers,
        // so keep them Integer and iterate with long arithmetic (the loop
        // variable stays an integer; no double round-trip).
        if (
            indexRef.Type == LuaValueType.Integer
            && Unsafe.Add(ref indexRef, 1).Type == LuaValueType.Integer
            && Unsafe.Add(ref indexRef, 2).Type == LuaValueType.Integer
        )
        {
            indexRef =
                indexRef.ReadAsInt64()
                - Unsafe.Add(ref indexRef, 2).ReadAsInt64();
            stack.NotifyTop(iA + frameBase + 1);
            context.Pc += instruction.SBx;
            return false;
        }

        if (!indexRef.TryReadDouble(out var init))
        {
            ThrowLuaRuntimeException(
                context,
                "'for' initial value must be a number"
            );
            return true;
        }

        if (!Unsafe.Add(ref indexRef, 1).TryReadDouble(out var limitValue))
        {
            ThrowLuaRuntimeException(context, "'for' limit must be a number");
            return true;
        }

        if (!Unsafe.Add(ref indexRef, 2).TryReadDouble(out var step))
        {
            ThrowLuaRuntimeException(context, "'for' step must be a number");
            return true;
        }

        indexRef = init - step;
        // Normalize the control slots to Number (double) so ForLoop's
        // ReadAsDouble fast path works even when the bounds were
        // Integer literals (e.g. `for i = 1, #t`).
        Unsafe.Add(ref indexRef, 1) = limitValue;
        Unsafe.Add(ref indexRef, 2) = step;
        stack.NotifyTop(iA + frameBase + 1);
        context.Pc += instruction.SBx;
        return false;
    }

    private static void OpForLoop(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        Instruction instruction)
    {
        Markers.ForLoop();

        ref var indexRef = ref stack.Get(iA + frameBase);

        // Integer fast path. ForPrep guarantees all control slots share a
        // type, so an Integer index implies Integer limit and step.
        if (indexRef.Type == LuaValueType.Integer)
        {
            var intLimit = Unsafe.Add(ref indexRef, 1).ReadAsInt64();
            var intStep = Unsafe.Add(ref indexRef, 2).ReadAsInt64();
            var intIndex = indexRef.ReadAsInt64() + intStep;

            if (intStep >= 0 ? intIndex <= intLimit : intLimit <= intIndex)
            {
                context.Pc += instruction.SBx;
                indexRef = intIndex;
                Unsafe.Add(ref indexRef, 3) = intIndex;
                stack.NotifyTop(iA + frameBase + 4);
                context.ThrowIfCancellationRequested();
                return;
            }

            stack.NotifyTop(iA + frameBase + 1);
            return;
        }

        var limit = Unsafe.Add(ref indexRef, 1).ReadAsDouble();
        var step = Unsafe.Add(ref indexRef, 2).ReadAsDouble();
        var index = indexRef.ReadAsDouble() + step;

        if (step >= 0 ? index <= limit : limit <= index)
        {
            context.Pc += instruction.SBx;
            indexRef = index;
            Unsafe.Add(ref indexRef, 3) = index;
            stack.NotifyTop(iA + frameBase + 4);
            context.ThrowIfCancellationRequested();
            return;
        }

        stack.NotifyTop(iA + frameBase + 1);
        return;
    }

    private static void OpTestSet(VirtualMachineExecutionContext context, LuaStack stack, Instruction instruction,
        int frameBase, int iA)
    {
        Markers.TestSet();
        ref readonly var vb = ref stack.Get(instruction.B + frameBase);
        if (vb.ToBoolean() != (instruction.C == 1))
        {
            context.Pc++;
        }
        else
        {
            stack.GetWithNotifyTop(iA + frameBase) = vb;
        }

        return;
    }

    private static void OpTest(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        Instruction instruction)
    {
        Markers.Test();
        if (stack.Get(iA + frameBase).ToBoolean() != (instruction.C == 1))
        {
            context.Pc++;
        }

        return;
    }

    private static void OpJmpIf(
        VirtualMachineExecutionContext context,
        ref Instruction instructionsHead,
        LuaStack stack,
        int iA,
        int frameBase,
        ref LuaValue constHead,
        OpCode opCode,
        Instruction instruction)
    {
        // Fused compare+branch for `x == <const>` / `x ~= <const>`: the
        // constant side's type (nil/bool/number/string, enforced at compile
        // time in EncodeComparison) means __eq is categorically unreachable
        // here regardless of the register's runtime type, so this is always a
        // raw LuaValue equality check -- no metamethod probe needed, unlike the
        // general Eq case above.
        //
        // instruction.SBx was fixed up (FixJump) against this instruction's OWN
        // pc, but consuming the following ExtraArg advances context.Pc by 1
        // first -- capture the main instruction's pc before that so the branch
        // target math matches what FixJump actually computed.
        var jmpIfKPc = context.Pc;
        var jmpIfKExtra = Unsafe.Add(ref instructionsHead, ++context.Pc).Ax;
        ref readonly var vb = ref stack.Get(iA + frameBase);
        // Low 8 bits: constant-pool index (guaranteed <= MaxIndexRK, 8 bits, by
        // EncodeComparison -- the fast path only fires when the constant already
        // fit an RK operand). High bits: an optional upvalue-close level, packed
        // in here (instead of this instruction's own A, which holds the compare
        // register) by PatchClose when this jump is used as a `break`/`goto`
        // that exits a scope with captured locals -- see PatchClose.
        ref readonly var vc = ref Unsafe.Add(ref constHead, jmpIfKExtra & 0xFF);
        if ((vb == vc) == (opCode == OpCode.JmpIfEqK))
        {
            // Only close on the branch actually being taken -- unlike plain
            // Jmp (always taken), this compare only exits the enclosing scope
            // when it fires; closing unconditionally would freeze captured
            // locals for a scope that's still active on the fall-through path.
            if (jmpIfKExtra >> 8 is var closeLevel and not 0)
            {
                context.State.CloseUpValues(frameBase + closeLevel - 1);
            }

            context.Pc = jmpIfKPc + instruction.SBx;
        }
    }

    private static bool OpBinaryCmp(VirtualMachineExecutionContext context, LuaStack stack, int frameBase,
        ref LuaValue constHead, Instruction instruction, OpCode opCode, int iA, out bool doRestart)
    {
        Markers.Lt();
        Markers.Le();
        doRestart = false;

        ref var stackHead = ref stack.Get(frameBase);
        ref readonly var vb = ref RKB(ref stackHead, ref constHead, instruction);
        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);

        // Integer comparison fast path (no double round-trip).
        if (vb.Type == LuaValueType.Integer && vc.Type == LuaValueType.Integer)
        {
            var compareResult = opCode == OpCode.Lt
                ? vb.ReadAsInt64() < vc.ReadAsInt64()
                : vb.ReadAsInt64() <= vc.ReadAsInt64();
            if (compareResult != (iA == 1))
            {
                context.Pc++;
            }

            return true;
        }

        if (vb.TryReadNumber(out var numB) && vc.TryReadNumber(out var numC))
        {
            var compareResult = opCode == OpCode.Lt ? numB < numC : numB <= numC;
            if (compareResult != (iA == 1))
            {
                context.Pc++;
            }

            return true;
        }

        // Fixed64 comparison (TryReadFixed64 converts Number → Fixed64)
        if (vb.TryReadFixed64(out var f64B) && vc.TryReadFixed64(out var f64C))
        {
            var compareResult = opCode == OpCode.Lt ? f64B < f64C : f64B <= f64C;
            if (compareResult != (iA == 1))
            {
                context.Pc++;
            }

            return true;
        }

        // f64AngleSingle comparison
        if (vb.TryReadFixed64Angle(out var angB) && vc.TryReadFixed64Angle(out var angC))
        {
            var compareResult = opCode == OpCode.Lt ? angB < angC : angB <= angC;
            if (compareResult != (iA == 1))
            {
                context.Pc++;
            }

            return true;
        }

        if (vb.TryReadString(out var strB) && vc.TryReadString(out var strC))
        {
            var c = StringComparer.Ordinal.Compare(strB, strC);
            var compareResult = opCode == OpCode.Lt ? c < 0 : c <= 0;
            if (compareResult != (iA == 1))
            {
                context.Pc++;
            }

            return true;
        }

        if (
            context.GlobalState.TryGetMetatable(vb, out _)
            || context.GlobalState.TryGetMetatable(vc, out _)
        )
        {
            if (
                ExecuteCompareOperationMetaMethod(
                    vb,
                    vc,
                    context,
                    opCode,
                    out doRestart
                )
            )
            {
                return true;
            }

            return false;
        }

        // Neither operand carries a metatable that could provide __lt/__le,
        // so the comparison is invalid — raise the same error the metamethod
        // path would have produced.
        LuaRuntimeException.AttemptInvalidOperation(
            GetstateWithCurrentPc(context),
            "compare",
            vb,
            vc
        );
        return false;
    }

    private static bool OpEq(VirtualMachineExecutionContext context, LuaStack stack, int frameBase, ref LuaValue constHead,
        Instruction instruction, int iA, out bool doRestart)
    {
        doRestart = false;
        
        Markers.Eq();

        ref var stackHead = ref stack.Get(frameBase);
        ref readonly var vb = ref RKB(ref stackHead, ref constHead, instruction);
        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
        if (vb == vc)
        {
            if (iA != 1)
            {
                context.Pc++;
            }

            doRestart = false;
            return true;
        }

        // Lua 5.2 semantics: __eq is only consulted when both operands are
        // the same kind (table or userdata) and at least one carries a
        // metatable. Mixed-type and primitive comparisons are never equal via
        // a metamethod, so resolve them directly and skip the probe.
        if (
            vb.Type == vc.Type
            && vb.Type
                is LuaValueType.Table
                or LuaValueType.UserData
                or LuaValueType.UserData2
            && (
                context.GlobalState.TryGetMetatable(vb, out _)
                || context.GlobalState.TryGetMetatable(vc, out _)
            )
        )
        {
            if (
                ExecuteCompareOperationMetaMethod(
                    vb,
                    vc,
                    context,
                    OpCode.Eq,
                    out doRestart
                )
            )
            {
                return true;
            }

            return false;
        }

        if (iA == 1)
        {
            context.Pc++;
        }

        // Not equal and not metamethod-eligible (mismatched primitive types, e.g.) -- handled
        // synchronously, same as the vb==vc case above; must return true (continue the loop),
        // not false (which the caller reads as "suspend/restart needed", incorrectly halting
        // MoveNext on every unequal primitive comparison).
        return true;
    }

    private static void OpJmp(VirtualMachineExecutionContext context, Instruction instruction, int iA, int frameBase)
    {
        Markers.Jmp();

        context.Pc += instruction.SBx;
        if (iA != 0)
        {
            context.State.CloseUpValues(frameBase + iA - 1);
        }

        context.ThrowIfCancellationRequested();
        return;
    }

    private static bool OpLen(VirtualMachineExecutionContext context, LuaStack stack, int frameBase,
        Instruction instruction, int iA, out bool doRestart)
    {
        Markers.Len();
        ref var stackHead = ref stack.FastGet(frameBase);
        ref readonly var vb = ref Unsafe.Add(ref stackHead, instruction.B);

        if (vb.TryReadString(out var str))
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = str.Length;
            stack.NotifyTop(ra1);
            doRestart = false;
            return true;
        }

        if (ExecuteUnaryOperationMetaMethod(vb, context, OpCode.Len, out doRestart))
        {
            return true;
        }

        return false;
    }

    private static bool OpSelf(VirtualMachineExecutionContext context, LuaStack stack, int frameBase, ref LuaValue constHead,
        Instruction instruction, int iA, out bool doRestart)
    {
        doRestart = false;
        Markers.Self();

        ref var stackHead = ref stack.FastGet(frameBase);
        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
        LuaValue table = Unsafe.Add(ref stackHead, instruction.B);

        doRestart = false;
        if (
            (
                table.TryReadTable(out var luaTable)
                && luaTable.TryGetValue(vc, out var resultValue)
            )
            || GetTableValueSlowPath(
                table,
                vc,
                context,
                out resultValue,
                out doRestart
            )
        )
        {
            if (doRestart)
            {
                return true;
            }

            Unsafe.Add(ref stackHead, iA) = resultValue;
            Unsafe.Add(ref stackHead, iA + 1) = table;
            stack.NotifyTop(iA + frameBase + 2);
            return true;
        }

        return false;
    }

    private static bool OpGetTable(VirtualMachineExecutionContext context, LuaStack stack, int frameBase,
        ref LuaValue constHead, Instruction instruction, out bool doRestart)
    {
        Markers.GetTabUp();
        Markers.GetTable();

        ref var stackHead = ref stack.FastGet(frameBase);
        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
        ref readonly var vb = ref instruction.OpCode == OpCode.GetTable
            ? ref Unsafe.Add(ref stackHead, instruction.B)
            : ref context.LuaClosure.GetUpValueRef(instruction.B);
        doRestart = false;
        if (
            (
                vb.TryReadTable(out var luaTable)
                && luaTable.TryGetValue(vc, out var resultValue)
            )
            || GetTableValueSlowPath(
                vb,
                vc,
                context,
                out resultValue,
                out doRestart
            )
        )
        {
            if (doRestart)
            {
                return true;
            }

            stack.GetWithNotifyTop(instruction.A + frameBase) = resultValue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fused two-level import read, <c>R(A) := UpValue[B][RK(C)][Kst(extra)]</c> -- the
    /// <c>math.floor</c> / <c>M.nested.x</c> shape (see <see cref="OpCode.GetImport"/>).
    ///
    /// Fast-path-only, per the compiler-rewrite plan's Milestone 3 decision. Both levels are done
    /// as raw table reads -- the very same <c>TryReadTable</c>/<c>TryGetValue</c> pair
    /// <see cref="OpGetTable"/> uses, so this cannot succeed anywhere the unfused pair would have
    /// taken its own fast path, nor fail where it would have. On any miss it *rewrites itself in
    /// place* back into that pair (level 1's destination, level 2's table, and level 2's
    /// destination are all register A, so the rewrite is exact) and restarts at the same pc, which
    /// hands the rest of the work to the existing, proven machinery:
    /// <see cref="GetTableValueSlowPath"/>, <c>__index</c> chains, <see cref="CallGetTableFunc"/>'s
    /// restart/frame handling, <c>PostOperation</c>, and -- because the rewritten instructions are
    /// a real GetTabUp and a real GetTable -- the upvalue-vs-stack-register distinction in
    /// <c>ThrowInvalidOperationForNoIndex</c> and the exact "attempt to index" error text.
    ///
    /// The rewrite is permanent for this instruction, but permanently correct: the rewritten pair
    /// is literally the unfused compilation of the same expression, so a transient miss (say,
    /// <c>_ENV.math</c> being a proxy on the first iteration and a plain table later) costs speed
    /// and never behavior.
    /// </summary>
    private static bool OpGetImport(VirtualMachineExecutionContext context, LuaStack stack, int frameBase,
        ref LuaValue constHead, ref Instruction instructionsHead, Instruction instruction, out bool doRestart)
    {
        Markers.GetImport();
        doRestart = false;

        // Same pc-capture discipline as OpJmpIf: instruction.SBx-style pc-relative math aside,
        // consuming the trailing ExtraArg below advances context.Pc, and the deopt needs this
        // instruction's own pc to rewrite and re-execute it.
        var importPc = context.Pc;
        var key2Index = Unsafe.Add(ref instructionsHead, ++context.Pc).Ax;

        ref var stackHead = ref stack.FastGet(frameBase);
        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
        ref readonly var vb = ref context.LuaClosure.GetUpValueRef(instruction.B);

        if (
            vb.TryReadTable(out var level1Table)
            && level1Table.TryGetValue(vc, out var level1)
            && level1.TryReadTable(out var level2Table)
            && level2Table.TryGetValue(Unsafe.Add(ref constHead, key2Index), out var result)
        )
        {
            stack.GetWithNotifyTop(instruction.A + frameBase) = result;
            return true;
        }

        // Deopt: back to the ordinary two-step lookup, in place. key2Index is guaranteed to fit an
        // RK operand by the compiler (Function.GetImport asserts it), so this is always encodable
        // in the two words this instruction already occupies.
        Unsafe.Add(ref instructionsHead, importPc) =
            Instruction.CreateABC(OpCode.GetTabUp, instruction.A, instruction.B, instruction.C);
        Unsafe.Add(ref instructionsHead, importPc + 1) =
            Instruction.CreateABC(OpCode.GetTable, instruction.A, instruction.A, Instruction.AsConstant(key2Index));

        // Re-execute this same pc. The dispatcher fetches with a pre-increment
        // (`Unsafe.Add(ref instructionsHead, ++context.Pc)`), so aiming one below does that.
        context.Pc = importPc - 1;
        doRestart = true;
        return true;
    }

    private static void OpMove(LuaStack stack, int frameBase, int iA, Instruction instruction)
    {
        Markers.Move();
        ref var stackHead = ref stack.FastGet(frameBase);
        Unsafe.Add(ref stackHead, iA) = Unsafe.Add(ref stackHead, instruction.B);
        stack.NotifyTop(iA + frameBase + 1);
        return;
    }

    private static void OpLoadK(LuaStack stack, int iA, int frameBase, ref LuaValue constHead, Instruction instruction)
    {
        Markers.LoadK();
        stack.GetWithNotifyTop(iA + frameBase) = Unsafe.Add(
            ref constHead,
            instruction.Bx
        );
        return;
    }

    private static void OpLoadKX(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        ref LuaValue constHead, ref Instruction instructionsHead)
    {
        Markers.LoadKX();
        stack.GetWithNotifyTop(iA + frameBase) = Unsafe.Add(
            ref constHead,
            Unsafe.Add(ref instructionsHead, ++context.Pc).Ax
        );
    }

    private static void OpLoadBool(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        Instruction instruction)
    {
        Markers.LoadBool();
        stack.GetWithNotifyTop(iA + frameBase) = instruction.B != 0;
        if (instruction.C != 0)
        {
            context.Pc++;
        }

        return;
    }

    private static void OpLoadBuiltin(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        Instruction instruction)
    {
        Markers.LoadBuiltin();
        stack.GetWithNotifyTop(iA + frameBase) = context.GlobalState.GetBuiltin(
            instruction.Bx
        );
        return;
    }

    private static void OpLoadNil(int iA, int frameBase, Instruction instruction, LuaStack stack)
    {
        Markers.LoadNil();
        var ra1 = iA + frameBase + 1;
        var iB = instruction.B;
        ref var stackHead = ref stack.FastGet(ra1 - 1);
        for (var i = 0; i <= iB; i++)
        {
            Unsafe.Add(ref stackHead, i) = default;
        }

        stack.NotifyTop(ra1 + iB);
        return;
    }

    private static void OpGetUpVal(VirtualMachineExecutionContext context, LuaStack stack, int iA, int frameBase,
        Instruction instruction)
    {
        Markers.GetUpVal();
        stack.GetWithNotifyTop(iA + frameBase) = context.LuaClosure.GetUpValue(
            instruction.B
        );
        return;
    }

    private static bool OpSetTable(VirtualMachineExecutionContext context, LuaStack stack, int frameBase,
        ref LuaValue constHead, Instruction instruction, OpCode opCode, int iA, out bool doRestart)
    {
        doRestart = false;
        
        Markers.SetTabUp();
        Markers.SetTable();

        ref var stackHead = ref stack.FastGet(frameBase);
        ref readonly var vb = ref RKB(ref stackHead, ref constHead, instruction);
        if (vb.TryReadNumber(out var numB))
        {
            if (double.IsNaN(numB))
            {
                ThrowLuaRuntimeException(context, "table index is NaN");
                return false;
            }
        }

        var table =
            opCode == OpCode.SetTabUp
                ? context.LuaClosure.GetUpValue(iA)
                : Unsafe.Add(ref stackHead, iA);

        if (table.TryReadTable(out var luaTable))
        {
            ref var valueRef = ref luaTable.FindValue(vb);
            if (
                !Unsafe.IsNullRef(ref valueRef)
                && valueRef.Type != LuaValueType.Nil
            )
            {
                // Overwriting a live entry: if it's a metamethod key, the
                // inline metamethod cache must be invalidated.
                if (MetamethodCache.IsMetamethodKey(vb))
                {
                    MetamethodCache.Invalidate();
                }

                valueRef = RKC(ref stackHead, ref constHead, instruction);
                LuaTableDiagnostics.RecordSetTableFast();
                doRestart = false;
                return true;
            }
        }

        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
#if LUA_VM_DIAGNOSTICS
        var __diagStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var __slowResult = SetTableValueSlowPath(table, vb, vc, context, out doRestart);
#if LUA_VM_DIAGNOSTICS
        LuaTableDiagnostics.RecordSetTableSlow(System.Diagnostics.Stopwatch.GetTimestamp() - __diagStart);
#endif
        if (__slowResult)
        {
            return true;
        }

        return false;
    }

    private static void OpSetUpVal(VirtualMachineExecutionContext context, Instruction instruction, LuaStack stack, int iA,
        int frameBase)
    {
        Markers.SetUpVal();
        context.LuaClosure.SetUpValue(instruction.B, stack.FastGet(iA + frameBase));
        return;
    }

    private static void OpNewTable(LuaStack stack, int iA, int frameBase, Instruction instruction)
    {
        Markers.NewTable();
        stack.GetWithNotifyTop(iA + frameBase) = new LuaTable(
            instruction.B,
            instruction.C
        );
        return;
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: R(A) := a fresh copy of the all-constant table template
    /// named by the trailing ExtraArg. See <see cref="OpCode.DupTable"/> for why sharing the
    /// template across executions is safe, and <c>LuaTable.CloneTemplate</c> for what a copy is.
    /// </summary>
    private static void OpDupTable(VirtualMachineExecutionContext context, LuaStack stack, int iA,
        int frameBase, ref Instruction instructionsHead)
    {
        Markers.DupTable();
        // Consumes the trailing ExtraArg word with the same convention LoadKX and OpGetImport use,
        // leaving context.Pc *on* that word: the dispatcher fetches with a pre-increment
        // (`Unsafe.Add(ref instructionsHead, ++context.Pc)`, line 441), so the next iteration steps
        // over it correctly and nothing else can observe it.
        var templateIndex = Unsafe.Add(ref instructionsHead, ++context.Pc).Ax;
        stack.GetWithNotifyTop(iA + frameBase) = context
            .Prototype.Templates[templateIndex]
            .CloneTemplate();
        return;
    }

    private static void OpNot(LuaStack stack, int frameBase, int iA, Instruction instruction)
    {
        Markers.Not();
        ref var stackHead = ref stack.FastGet(frameBase);
        Unsafe.Add(ref stackHead, iA) = !Unsafe
            .Add(ref stackHead, instruction.B)
            .ToBoolean();
        stack.NotifyTop(iA + frameBase + 1);
    }

    private static bool OpUnaryArith(VirtualMachineExecutionContext context,
        LuaStack stack,
        int frameBase,
        Instruction instruction,
        int iA,
        out bool doRestart)
    {
        doRestart = false;
        
        Markers.Unm();
        ref var stackHead = ref stack.FastGet(frameBase);
        ref var vb = ref Unsafe.Add(ref stackHead, instruction.B);

        // Integer unary minus (wraps on long.MinValue, matching Lua 5.3)
        if (vb.Type == LuaValueType.Integer)
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = -vb.ReadAsInt64();
            stack.NotifyTop(ra1);
            return true;
        }

        // Fixed64 unary minus (must precede TryReadDouble to preserve type)
        if (vb.Type == LuaValueType.Fixed64)
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = -vb.ReadAsFixed64();
            stack.NotifyTop(ra1);
            return true;
        }

        // Fixed64Vector3 unary minus (must precede TryReadDouble)
        if (vb.Type == LuaValueType.Fixed64Vector3)
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = -vb.ReadAsF64Vector3();
            stack.NotifyTop(ra1);
            return true;
        }

        // f64Euler unary minus (component negation, wrapped)
        if (vb.Type == LuaValueType.Fixed64Euler)
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = -vb.ReadAsF64Euler();
            stack.NotifyTop(ra1);
            return true;
        }

        // f64AngleSingle unary minus
        if (vb.Type == LuaValueType.Fixed64Angle)
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = -vb.ReadAsF64Angle();
            stack.NotifyTop(ra1);
            return true;
        }

        if (vb.TryReadDouble(out var numB))
        {
            var ra1 = iA + frameBase + 1;
            Unsafe.Add(ref stackHead, iA) = -numB;
            stack.NotifyTop(ra1);
            return true;
        }

        return ExecuteUnaryOperationMetaMethod(vb, context, OpCode.Unm, out doRestart);
    }

    private static bool OpBinaryArith(
        VirtualMachineExecutionContext context,
        LuaStack stack,
        int frameBase,
        ref LuaValue constHead,
        Instruction instruction,
        int iA,
        OpCode opCode,
        out bool doRestart)
    {
        doRestart = false;
        
        Markers.Add();
        Markers.Sub();
        Markers.Mul();
        Markers.Div();
        Markers.Mod();
        Markers.Pow();
        Markers.IDiv();

        ref LuaValue stackHead = ref stack.FastGet(frameBase);
        ref readonly LuaValue vb = ref RKB(ref stackHead, ref constHead, instruction);
        ref readonly LuaValue vc = ref RKC(ref stackHead, ref constHead, instruction);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static double Mod(double a, double b)
        {
            var mod = a % b;
            if ((b > 0 && mod < 0) || (b < 0 && mod > 0))
            {
                mod += b;
            }

            return mod;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static long IntegerMod(long a, long b)
        {
            var mod = a % b;
            if ((b > 0 && mod < 0) || (b < 0 && mod > 0))
            {
                mod += b;
            }

            return mod;
        }

        // Floor division for integers (rounds toward -inf, unlike '/').
        [MethodImpl(MethodImplOptions.NoInlining)]
        static long IntegerFloorDiv(long a, long b)
        {
            var quotient = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0)))
            {
                quotient--;
            }

            return quotient;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double ArithmeticOperation(OpCode code, double a, double b)
        {
            return code switch
            {
                OpCode.Add => a + b,
                OpCode.Sub => a - b,
                OpCode.Mul => a * b,
                OpCode.Div => a / b,
                OpCode.Mod => Mod(a, b),
                OpCode.Pow => Math.Pow(a, b),
                OpCode.IDiv => Math.Floor(a / b),
                _ => 0,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Fixed64 Fixed64ArithmeticOperation(OpCode code, Fixed64 a, Fixed64 b)
        {
            return code switch
            {
                OpCode.Add => a + b,
                OpCode.Sub => a - b,
                OpCode.Mul => a * b,
                OpCode.Div => a / b,
                OpCode.Mod => a % b,
                // Fixed64.Pow not supported — falls through to metamethod/error
                _ => Fixed64.Zero,
            };
        }

        // Number + Number fast path
        if (vb.Type == LuaValueType.Number && vc.Type == LuaValueType.Number)
        {
            Unsafe.Add(ref stackHead, iA) = ArithmeticOperation(
                opCode,
                vb.ReadAsDouble(),
                vc.ReadAsDouble()
            );
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // Integer + Integer fast path (Lua 5.3 semantics: + - * % stay
        // integer; / and ^ always produce float). Mod by zero falls through
        // to the double path (NaN), matching existing behavior. For '//'
        // (Luau) a zero divisor yields ±inf/NaN through the float path, and
        // long.MinValue // -1 would overflow, so both fall through too.
        if (vb.Type == LuaValueType.Integer && vc.Type == LuaValueType.Integer)
        {
            var a = vb.ReadAsInt64();
            var b = vc.ReadAsInt64();
            var isIntegerPath =
                opCode is OpCode.Add or OpCode.Sub or OpCode.Mul
                || (
                    (opCode is OpCode.Mod or OpCode.IDiv)
                    && b != 0
                    && (opCode != OpCode.IDiv || !(a == long.MinValue && b == -1))
                );

            if (isIntegerPath)
            {
                var result = opCode switch
                {
                    OpCode.Add => a + b,
                    OpCode.Sub => a - b,
                    OpCode.Mul => a * b,
                    OpCode.IDiv => IntegerFloorDiv(a, b),
                    _ => IntegerMod(a, b),
                };
                Unsafe.Add(ref stackHead, iA) = result;
                stack.NotifyTop(iA + frameBase + 1);
                return true;
            }
        }

        // Fixed64 + Fixed64 fast path (same-type only, no cross-type coercion).
        // '//' has no Fixed64 form, so it falls through to the double path.
        if (
            opCode != OpCode.IDiv
            && vb.Type == LuaValueType.Fixed64
            && vc.Type == LuaValueType.Fixed64
        )
        {
            Unsafe.Add(ref stackHead, iA) = Fixed64ArithmeticOperation(
                opCode,
                vb.ReadAsFixed64(),
                vc.ReadAsFixed64()
            );
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // Fixed64Vector3 + Fixed64Vector3 (add/sub only)
        if ((opCode == OpCode.Add || opCode == OpCode.Sub)
            && vb.Type == LuaValueType.Fixed64Vector3
            && vc.Type == LuaValueType.Fixed64Vector3)
        {
            var vecA = vb.ReadAsF64Vector3();
            var vecB = vc.ReadAsF64Vector3();
            Unsafe.Add(ref stackHead, iA) = opCode == OpCode.Add ? vecA + vecB : vecA - vecB;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // Fixed64Vector3 * Fixed64 scalar, Fixed64Vector3 / Fixed64 scalar
        if ((opCode == OpCode.Mul || opCode == OpCode.Div)
            && vb.Type == LuaValueType.Fixed64Vector3
            && vc.Type == LuaValueType.Fixed64)
        {
            var vec = vb.ReadAsF64Vector3();
            var scalar = vc.ReadAsFixed64();
            Unsafe.Add(ref stackHead, iA) = opCode == OpCode.Mul ? vec * scalar : vec / scalar;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // Fixed64 * Fixed64Vector3 (commutative mul only)
        if (opCode == OpCode.Mul
            && vb.Type == LuaValueType.Fixed64
            && vc.Type == LuaValueType.Fixed64Vector3)
        {
            var scalar = vb.ReadAsFixed64();
            var vec = vc.ReadAsF64Vector3();
            Unsafe.Add(ref stackHead, iA) = scalar * vec;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // Fixed64Vector3 * Fixed64Vector3 component-wise, Fixed64Vector3 / Fixed64Vector3 component-wise
        if ((opCode == OpCode.Mul || opCode == OpCode.Div)
            && vb.Type == LuaValueType.Fixed64Vector3
            && vc.Type == LuaValueType.Fixed64Vector3)
        {
            var vecA = vb.ReadAsF64Vector3();
            var vecB = vc.ReadAsF64Vector3();
            Unsafe.Add(ref stackHead, iA) = opCode == OpCode.Mul ? vecA * vecB : vecA / vecB;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // f64Euler + f64Euler (component-wise, wrapped)
        if ((opCode == OpCode.Add || opCode == OpCode.Sub)
            && vb.Type == LuaValueType.Fixed64Euler
            && vc.Type == LuaValueType.Fixed64Euler)
        {
            var a = vb.ReadAsF64Euler();
            var b = vc.ReadAsF64Euler();
            Unsafe.Add(ref stackHead, iA) = opCode == OpCode.Add ? a + b : a - b;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // f64Euler * f64AngleSingle scalar, f64Euler / f64AngleSingle scalar (wrapped)
        if ((opCode == OpCode.Mul || opCode == OpCode.Div)
            && vb.Type == LuaValueType.Fixed64Euler
            && vc.Type == LuaValueType.Fixed64Angle)
        {
            var euler = vb.ReadAsF64Euler();
            var scalar = vc.ReadAsF64Angle();
            Unsafe.Add(ref stackHead, iA) = opCode == OpCode.Mul ? euler * scalar : euler / scalar;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // f64AngleSingle * f64Euler (commutative scalar mul)
        if (opCode == OpCode.Mul
            && vb.Type == LuaValueType.Fixed64Angle
            && vc.Type == LuaValueType.Fixed64Euler)
        {
            var scalar = vb.ReadAsF64Angle();
            var euler = vc.ReadAsF64Euler();
            Unsafe.Add(ref stackHead, iA) = scalar * euler;
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // f64AngleSingle + f64AngleSingle (radians-based)
        if (
            opCode is OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div
            && vb.Type == LuaValueType.Fixed64Angle
            && vc.Type == LuaValueType.Fixed64Angle
        )
        {
            var a = vb.ReadAsF64Angle();
            var b = vc.ReadAsF64Angle();
            Unsafe.Add(ref stackHead, iA) = opCode switch
            {
                OpCode.Add => (LuaValue)(a + b),
                OpCode.Sub => (LuaValue)(a - b),
                OpCode.Mul => (LuaValue)(a * b),
                OpCode.Div => (LuaValue)(a / b),
                _ => LuaValue.Nil,
            };
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        // Cross-type (Fixed64/Number/Fixed64Angle with different types) is intentionally not supported.
        // Skip TryReadDouble coercion so the metamethod fallback produces the error.
        var skipCoercion =
            ((vb.Type == LuaValueType.Fixed64 || vc.Type == LuaValueType.Fixed64)
             && vb.Type != vc.Type)
            || ((vb.Type == LuaValueType.Fixed64Angle || vc.Type == LuaValueType.Fixed64Angle)
                && vb.Type != vc.Type);

        if (!skipCoercion && vb.TryReadDouble(out var numB) && vc.TryReadDouble(out var numC))
        {
            Unsafe.Add(ref stackHead, iA) = ArithmeticOperation(opCode, numB, numC);
            stack.NotifyTop(iA + frameBase + 1);
            return true;
        }

        return ExecuteBinaryOperationMetaMethod(vb, vc, context, opCode, out doRestart);
    }

    static void ThrowLuaRuntimeException(VirtualMachineExecutionContext context, string message)
    {
        throw new LuaRuntimeException(GetstateWithCurrentPc(context), message);
    }

    static void ThrowLuaNotImplementedException(
        VirtualMachineExecutionContext context,
        OpCode opcode
    )
    {
        throw new LuaRuntimeException(
            GetstateWithCurrentPc(context),
            $"OpCode {opcode} is not implemented"
        );
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static double Mod(double a, double b)
    {
        var mod = a % b;
        if ((b > 0 && mod < 0) || (b < 0 && mod > 0))
        {
            mod += b;
        }

        return mod;
    }

    static void SelfPostOperation(
        VirtualMachineExecutionContext context,
        int lastTop,
        Span<LuaValue> results
    )
    {
        var stack = context.Stack;
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        var RB = instruction.B + context.FrameBase;
        ref var stackHead = ref stack.Get(0);
        var table = Unsafe.Add(ref stackHead, RB);
        Unsafe.Add(ref stackHead, RA + 1) = table;
        Unsafe.Add(ref stackHead, RA) = results.Length == 0 ? LuaValue.Nil : results[0];
        stack.PopUntil(Math.Max(RA + 2, lastTop));
        stack.NotifyTop(RA + 2);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Concat(VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var b = instruction.B;
        var c = instruction.C;
        stack.SetTop(context.FrameBase + c + 1);
        var a = instruction.A;

        // Reset PostOperation *before* starting the task below, never after.
        //
        // Every opcode handler parks its pending work in context.Task and returns to
        // ExecuteClosureAsyncImpl, which is what awaits it -- so from the moment that task exists,
        // a thread-pool thread may already be running its continuation while this thread is still
        // unwinding out of MoveNext. For every other opcode that is harmless, because the task
        // runs against its own nested execution context and never touches this one. Concat is the
        // exception: its task is an async helper that closes over *this* context and writes
        // PostOperation = DontPop to it before completing, telling the resume code that the
        // metamethod call already popped its own frame.
        //
        // Writing None here after the task has started therefore races that DontPop, and when
        // this thread wins, the resume code takes the "pop a frame" branch it was told to skip and
        // collapses the call stack -- surfacing as debug.getinfo(2) == nil inside a metamethod, or
        // as an ArgumentOutOfRangeException out of PopOnTopCallStackFrames. Hoisting the write
        // above the call makes it happen strictly before a second thread can exist. It costs
        // nothing: on the synchronously-completing path the helper's own DontPop still lands
        // afterwards, exactly as it did when this line sat below the early return.
        context.PostOperation = PostOperationType.None;

        var task = Concat(context, context.FrameBase + a, c - b + 1);
        if (task.IsCompleted)
        {
            _ = task.Result;
            return true;
        }

#if DEBUG
        // Test seam for ConcatDispatchRaceTests. Sits at the *top* of the window described above
        // -- the task exists and may already be running elsewhere, and this thread has not yet
        // done its remaining bookkeeping -- so a test can stall here and make the ordering
        // requirement deterministic instead of waiting on a once-per-few-dozen-suite-runs flake.
        // Keep it above every remaining write, or a handler that reintroduced a post-task write
        // would slip past it. Debug-only, and only on the suspending-concat path, so Release
        // dispatch is untouched.
        ConcatSuspendedHookForTests?.Invoke();
#endif

        context.Task = task;

        return false;
    }

#if DEBUG
    internal static Action? ConcatSuspendedHookForTests;
#endif

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    static async ValueTask<int> Concat(
        VirtualMachineExecutionContext context,
        int target,
        int total
    )
    {
        static bool ToString(ref LuaValue v)
        {
            if (v.Type == LuaValueType.String)
            {
                return true;
            }

            if (v.Type is LuaValueType.Number or LuaValueType.Integer)
            {
                v = v.ToString();
                return true;
            }

            return false;
        }

        var stack = context.Stack;
        do
        {
            var top = context.State.Stack.Count;
            var n = 2;
            ref var lhs = ref stack.Get(top - 2);
            ref var rhs = ref stack.Get(top - 1);
            if (!(lhs.Type is LuaValueType.String or LuaValueType.Number or LuaValueType.Integer) || !ToString(ref rhs))
            {
                await ExecuteBinaryOperationMetaMethod(top - 2, lhs, rhs, context, OpCode.Concat);
            }
            else if (rhs.ReadAsString().Length == 0)
            {
                ToString(ref lhs);
            }
            else if (lhs.TryReadString(out var str) && str.Length == 0)
            {
                lhs = rhs;
            }
            else
            {
                var tl = rhs.ReadAsString().Length;

                var i = 1;
                for (; i < total; i++)
                {
                    ref var v = ref stack.Get(top - i - 1);
                    if (!ToString(ref v))
                    {
                        break;
                    }

                    tl += v.ReadAsString().Length;
                }

                n = i;
                stack.Get(top - n) = string.Create(
                    tl,
                    (stack, top - n),
                    static (span, pair) =>
                    {
                        var (stack, index) = pair;
                        foreach (var v in stack.AsSpan().Slice(index))
                        {
                            var s = v.ReadAsString();
                            if (s.Length == 0)
                            {
                                continue;
                            }

                            s.AsSpan().CopyTo(span);
                            span = span[s.Length..];
                        }
                    }
                );
            }

            total -= n - 1;
            stack.PopUntil(top - (n - 1));
        } while (total > 1);

        // This opcode's own dispatcher (Concat(VirtualMachineExecutionContext), the sync
        // wrapper) never pushes a frame of its own -- any frame push/pop that happened while
        // this loop ran was the metamethod call above, and it already popped its own frame (see
        // ExecuteBinaryOperationMetaMethod's finally). So the caller awaiting this task --
        // ExecuteClosureAsyncImpl's own resume code, which unconditionally pops a frame unless
        // told not to -- must always be told DontPop here, unconditionally, once this loop has
        // gone through even one metamethod call. Restated here rather than left to
        // ExecuteBinaryOperationMetaMethod's own write because this is a do-while over several
        // operands ("a..b..c"): a later iteration that concatenates plain strings does not go
        // through a metamethod at all, so the last write before this task completes has to be
        // this one. (It is unconditional, not guarded on "did a metamethod run", because on the
        // purely synchronous path the caller never reads PostOperation -- Concat()'s
        // task.IsCompleted branch returns straight into the dispatch loop, and every suspension
        // site sets PostOperation itself before suspending.)
        context.PostOperation = PostOperationType.DontPop;
        stack.Get(target) = stack.AsSpan()[^1];

        return 1;
    }

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<LuaValue> Concat(
        LuaState state,
        int total,
        CancellationToken ct
    )
    {
        static bool ToString(ref LuaValue v)
        {
            if (v.Type == LuaValueType.String)
            {
                return true;
            }

            if (v.Type is LuaValueType.Number or LuaValueType.Integer)
            {
                v = v.ToString();
                return true;
            }

            return false;
        }

        var stack = state.Stack;
        do
        {
            var top = stack.Count;
            var n = 2;
            ref var lhs = ref stack.Get(top - 2);
            ref var rhs = ref stack.Get(top - 1);
            if (!(lhs.Type is LuaValueType.String or LuaValueType.Number or LuaValueType.Integer) || !ToString(ref rhs))
            {
                var value = await ExecuteBinaryOperationMetaMethod(
                    state,
                    lhs,
                    rhs,
                    OpCode.Concat,
                    ct
                );
                stack.Get(top - 2) = value;
            }
            else if (rhs.ReadAsString().Length == 0)
            {
                ToString(ref lhs);
            }
            else if (lhs.TryReadString(out var str) && str.Length == 0)
            {
                lhs = rhs;
            }
            else
            {
                var tl = rhs.ReadAsString().Length;

                var i = 1;
                for (; i < total; i++)
                {
                    ref var v = ref stack.Get(top - i - 1);
                    if (!ToString(ref v))
                    {
                        break;
                    }

                    tl += v.ReadAsString().Length;
                }

                n = i;
                stack.Get(top - n) = string.Create(
                    tl,
                    (stack, top - n),
                    static (span, pair) =>
                    {
                        var (stack, index) = pair;
                        foreach (var v in stack.AsSpan().Slice(index))
                        {
                            var s = v.ReadAsString();
                            if (s.Length == 0)
                            {
                                continue;
                            }

                            s.AsSpan().CopyTo(span);
                            span = span[s.Length..];
                        }
                    }
                );
            }

            total -= n - 1;
            stack.PopUntil(top - (n - 1));
        } while (total > 1);

        return stack.AsSpan()[^1];
    }

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder))]
    static async ValueTask ExecuteBinaryOperationMetaMethod(
        int target,
        LuaValue vb,
        LuaValue vc,
        VirtualMachineExecutionContext context,
        OpCode opCode
    )
    {
        context.ThrowIfCancellationRequested();
        var (name, description) = opCode.GetNameAndDescription();
        if (
            vb.TryGetMetamethod(context.GlobalState, name, out var metamethod)
            || vc.TryGetMetamethod(context.GlobalState, name, out metamethod)
        )
        {
            var stack = context.Stack;
            var argCount = 2;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(
                        context.GlobalState,
                        Metamethods.Call,
                        out metamethod
                    ) && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                    argCount++;
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(
                        GetstateWithCurrentPc(context),
                        "call",
                        metamethod
                    );
                }
            }

            stack.Push(vb);
            stack.Push(vc);
            var varArgCount = func.GetVariableArgumentCount(argCount);

            var newFrame = func.CreateNewFrame(
                context,
                stack.Count - argCount + varArgCount,
                target,
                varArgCount
            );
            var state = context.State;
            state.PushCallStackFrame(newFrame);
            try
            {
                var functionContext = new LuaFunctionExecutionContext
                {
                    State = state,
                    ArgumentCount = argCount,
                    ReturnFrameBase = target,
                };
                if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
                {
                    await ExecuteCallHook(functionContext, context.CancellationToken);
                    stack.PopUntil(target + 1);
                    context.PostOperation = PostOperationType.DontPop;
                    context.ThrowIfCancellationRequested();
                    return;
                }

                await func.Func(functionContext, context.CancellationToken);
                stack.PopUntil(target + 1);
                context.PostOperation = PostOperationType.DontPop;
                context.ThrowIfCancellationRequested();
                return;
            }
            finally
            {
                state.PopCallStackFrame();
            }
        }

        LuaRuntimeException.AttemptInvalidOperation(
            GetstateWithCurrentPc(context),
            description,
            vb,
            vc
        );
        return;
    }

    static bool Call(VirtualMachineExecutionContext context, out bool doRestart)
    {
        context.ThrowIfCancellationRequested();
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        var newBase = RA + 1;
        var va = context.Stack.Get(RA);
        var isMetamethod = false;
        if (!va.TryReadFunction(out var func))
        {
            if (
                va.TryGetMetamethod(context.GlobalState, Metamethods.Call, out var metamethod)
                && metamethod.TryReadFunction(out func)
            )
            {
                newBase -= 1;
                isMetamethod = true;
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperationOnLuaStack(
                    GetstateWithCurrentPc(context),
                    "call",
                    context.Pc,
                    instruction.A
                );
            }
        }

        var state = context.State;
        var (argumentCount, variableArgumentCount) = PrepareForFunctionCall(
            state,
            func,
            instruction,
            newBase,
            isMetamethod
        );
        newBase += variableArgumentCount;
        state.Stack.PopUntil(newBase + argumentCount);

        var newFrame = func.CreateNewFrame(context, newBase, RA, variableArgumentCount);

        state.PushCallStackFrame(newFrame);
        if (state.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
        {
            context.PostOperation = PostOperationType.Call;
            context.Task = ExecuteCallHook(context, newFrame, argumentCount);
            doRestart = false;
            return false;
        }

        if (func is LuaClosure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        doRestart = false;
        return FuncCall(context, state, func, argumentCount, newFrame.ReturnBase);

        static bool FuncCall(
            VirtualMachineExecutionContext context,
            LuaState state,
            LuaFunction func,
            int argumentCount,
            int returnBase
        )
        {
            var task = func.Func(
                new()
                {
                    State = state,
                    ArgumentCount = argumentCount,
                    ReturnFrameBase = returnBase,
                },
                context.CancellationToken
            );

            if (!task.IsCompleted)
            {
                context.PostOperation = PostOperationType.Call;
                context.Task = task;
                return false;
            }

            var awaiter = task.GetAwaiter();

            awaiter.GetResult();
            context.State.ThrowIfCancellationRequested(context.CancellationToken);
            var instruction = context.Instruction;
            var ic = instruction.C;

            if (ic != 0)
            {
                var resultCount = ic - 1;
                var stack = context.Stack;
                var top = instruction.A + context.FrameBase + resultCount;
                stack.EnsureCapacity(top);
                stack.PopUntil(top);
                stack.NotifyTop(top);
            }

            context.State.PopCallStackFrame();
            return true;
        }
    }

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<int> Call(
        LuaState state,
        int funcIndex,
        int returnBase,
        CancellationToken cancellationToken
    )
    {
        state.ThrowIfCancellationRequested(cancellationToken);
        var stack = state.Stack;
        var newBase = funcIndex + 1;
        var va = stack.Get(funcIndex);
        if (!va.TryReadFunction(out var func))
        {
            if (
                va.TryGetMetamethod(state.GlobalState, Metamethods.Call, out va)
                && va.TryReadFunction(out func)
            )
            {
                newBase--;
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperation(state, "call", va);
            }
        }

        var (argCount, variableArgumentCount) = PrepareForFunctionCall(state, func, newBase);
        newBase += variableArgumentCount;
        var newFrame = new CallStackFrame
        {
            Base = newBase,
            VariableArgumentCount = variableArgumentCount,
            Function = func,
            ReturnBase = returnBase,
        };

        state.PushCallStackFrame(newFrame);
        try
        {
            var functionContext = new LuaFunctionExecutionContext
            {
                State = state,
                ArgumentCount = argCount,
                ReturnFrameBase = returnBase,
            };
            if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
            {
                await ExecuteCallHook(functionContext, cancellationToken);
            }
            else
            {
                await func.Func(functionContext, cancellationToken);
            }

            state.ThrowIfCancellationRequested(cancellationToken);
            return state.Stack.Count - returnBase;
        }
        catch (OperationCanceledException operationCanceledException)
        {
            if (operationCanceledException is not LuaCanceledException)
            {
                throw new LuaCanceledException(
                    state,
                    cancellationToken,
                    operationCanceledException
                );
            }

            throw;
        }
        finally
        {
            state.PopCallStackFrame();
        }
    }

    static void CallPostOperation(VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var ic = instruction.C;

        if (ic != 0)
        {
            var resultCount = ic - 1;
            var stack = context.Stack;
            var top = instruction.A + context.FrameBase + resultCount;
            stack.EnsureCapacity(top);
            stack.PopUntil(top);
            stack.NotifyTop(top);
        }
    }

    static bool TailCall(VirtualMachineExecutionContext context, out bool doRestart)
    {
        context.ThrowIfCancellationRequested();
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;
        var newBase = RA + 1;
        var isMetamethod = false;
        var state = context.State;

        state.CloseUpValues(context.FrameBase);

        var va = stack.Get(RA);
        if (!va.TryReadFunction(out var func))
        {
            if (
                va.TryGetMetamethod(state.GlobalState, Metamethods.Call, out var metamethod)
                && metamethod.TryReadFunction(out func)
            )
            {
                isMetamethod = true;
                newBase -= 1;
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperation(
                    GetstateWithCurrentPc(context),
                    "call",
                    metamethod
                );
            }
        }

        var (argumentCount, variableArgumentCount) = PrepareForFunctionTailCall(
            state,
            func,
            instruction,
            newBase,
            isMetamethod
        );
        newBase = context.FrameBase + variableArgumentCount;
        stack.PopUntil(newBase + argumentCount);
        var lastFrame = state.GetCurrentFrame();
        state.LastPc = context.Pc;
        state.LastCallerFunction = lastFrame.Function;
        context.State.PopCallStackFrame();
        var newFrame = func.CreateNewTailCallFrame(
            context,
            newBase,
            lastFrame.ReturnBase,
            variableArgumentCount
        );

        newFrame.CallerInstructionIndex = lastFrame.CallerInstructionIndex;
        newFrame.Version = lastFrame.Version;
        state.PushCallStackFrame(newFrame);

        if (state.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
        {
            context.PostOperation = PostOperationType.TailCall;
            context.Task = ExecuteCallHook(context, newFrame, argumentCount, true);
            doRestart = false;
            return false;
        }

        if (func is LuaClosure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        doRestart = false;
        var task = func.Func(
            new()
            {
                State = state,
                ArgumentCount = argumentCount,
                ReturnFrameBase = lastFrame.ReturnBase,
            },
            context.CancellationToken
        );

        if (!task.IsCompleted)
        {
            context.PostOperation = PostOperationType.TailCall;
            context.Task = task;
            return false;
        }

        task.GetAwaiter().GetResult();
        context.ThrowIfCancellationRequested();
        if (
            !context.PopFromBuffer(lastFrame.ReturnBase, context.Stack.Count - lastFrame.ReturnBase)
        )
        {
            return true;
        }

        doRestart = true;
        return true;
    }

    static bool TForCall(VirtualMachineExecutionContext context, out bool doRestart)
    {
        context.ThrowIfCancellationRequested();
        doRestart = false;
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;
        var isMetamethod = false;
        var iteratorRaw = stack.Get(RA);
        if (!iteratorRaw.TryReadFunction(out var iterator))
        {
            if (
                iteratorRaw.TryGetMetamethod(
                    context.GlobalState,
                    Metamethods.Call,
                    out var metamethod
                ) && metamethod.TryReadFunction(out iterator)
            )
            {
                isMetamethod = true;
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperation(
                    GetstateWithCurrentPc(context),
                    "call",
                    metamethod
                );
            }
        }

        // Fast path: a loop over the builtin `next`, i.e. `for k, v in pairs(t)`.
        // `next` is a C# function, so the generic path below pays a full Lua->C# call per
        // KEY (frame push, argument marshalling through the generic `TryRead` dispatch
        // chain, delegate invoke, frame pop). Traversing the table in place removes that
        // call entirely; the observable result (the two values written at RA+3/RA+4 and the
        // final stack top) is identical. Skipped whenever a hook or a pending exception
        // needs the frame to exist, and for `__call`-dispatched iterators, where the
        // arguments differ.
        if (
            iterator.IsBuiltinNext
            && !isMetamethod
            && context.State.CurrentException is null
            && (context.State.CallOrReturnHookMask.Value == 0 || context.State.IsInHook)
            && stack.Get(RA + 1).TryReadTable(out var nextTable)
        )
        {
            TForCallNext(context, nextTable, RA);
            return true;
        }

        var newBase = RA + 3 + instruction.C;

        if (isMetamethod)
        {
            stack.Get(newBase) = iteratorRaw;
            stack.Get(newBase + 1) = stack.Get(RA + 1);
            stack.Get(newBase + 2) = stack.Get(RA + 2);
            stack.SetTop(newBase + 3);
        }
        else
        {
            stack.Get(newBase) = stack.Get(RA + 1);
            stack.Get(newBase + 1) = stack.Get(RA + 2);
            stack.SetTop(newBase + 2);
        }

        var argumentCount = isMetamethod ? 3 : 2;
        var variableArgumentCount = iterator.GetVariableArgumentCount(argumentCount);
        if (variableArgumentCount != 0)
        {
            PrepareVariableArgument(stack, newBase, argumentCount, variableArgumentCount);
            newBase += variableArgumentCount;
        }

        stack.PopUntil(newBase + argumentCount);

        var newFrame = iterator.CreateNewFrame(context, newBase, RA + 3, variableArgumentCount);
        context.State.PushCallStackFrame(newFrame);
        if (context.State.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
        {
            context.PostOperation = PostOperationType.TForCall;
            context.Task = ExecuteCallHook(context, newFrame, stack.Count - newBase);
            doRestart = false;
            return false;
        }

        if (iterator is LuaClosure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        var task = iterator.Func(
            new()
            {
                State = context.State,
                ArgumentCount = stack.Count - newBase,
                ReturnFrameBase = newFrame.ReturnBase,
            },
            context.CancellationToken
        );
        if (!task.IsCompleted)
        {
            context.PostOperation = PostOperationType.TForCall;
            context.Task = task;

            return false;
        }

        task.GetAwaiter().GetResult();
        context.ThrowIfCancellationRequested();
        context.State.PopCallStackFrame();
        TForCallPostOperation(context);
        return true;
    }

    /// <summary>
    /// Runs one step of a loop over the builtin <c>next</c>: exactly what
    /// <c>BasicLibrary.Next</c> plus <see cref="TForCallPostOperation"/> do, but without
    /// entering the C# function.
    /// </summary>
    static void TForCallNext(VirtualMachineExecutionContext context, LuaTable table, int RA)
    {
        var stack = context.Stack;
        var control = stack.Get(RA + 2);

        KeyValuePair<LuaValue, LuaValue> pair = default;
        var slot = -1;
        bool hasPair;

        // `next` is handed the key the previous step returned, so the slot that key occupies
        // is already known. Validate it instead of re-hashing the key: slots only move on a
        // swap-erase removal (which string tables have no production caller for) and vanish
        // on clear, so a stale cursor reads as a miss and falls back to the hash path.
        if (
            control.Type == LuaValueType.String
            && ReferenceEquals(table, context.NextIteratorTable)
            && table.SlotStillHolds(context.NextIteratorSlot, control.ReadAsString())
        )
        {
            hasPair = table.TryNextFromSlot(context.NextIteratorSlot, out pair, out slot);
        }
        else if (control.Type == LuaValueType.String)
        {
            hasPair = table.TryGetNextFromString(control.ReadAsString(), out pair, out slot);
        }
        else
        {
            // Control is nil (first step) or a non-string key: no slot to hand back.
            hasPair = table.TryGetNext(control, out pair);
        }

        // Same write sequence as `Return(result0, result1)` followed by
        // TForCallPostOperation: results over the return base, then the top at A + 3 + C so
        // that extra loop variables read as nil.
        var returnBase = RA + 3;
        stack.SetTop(returnBase + 2);
        stack.FastGet(returnBase) = hasPair ? pair.Key : LuaValue.Nil;
        stack.FastGet(returnBase + 1) = hasPair ? pair.Value : LuaValue.Nil;
        stack.SetTop(RA + context.Instruction.C + 3);

        context.NextIteratorTable = hasPair ? table : null;
        context.NextIteratorSlot = slot;
    }

    // ReSharper disable once InconsistentNaming
    static void TForCallPostOperation(VirtualMachineExecutionContext context)
    {
        var stack = context.Stack;
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        stack.SetTop(RA + instruction.C + 3);
    }

    static void SetList(VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;

        if (!stack.Get(RA).TryReadTable(out var table))
        {
            throw new LuaRuntimeException(GetstateWithCurrentPc(context), "internal error");
        }

        var count = instruction.B == 0 ? stack.Count - (RA + 1) : instruction.B;
        var c = instruction.C;
        if (c == 0)
        {
            context.Pc++;
            c = context.Prototype.Code[context.Pc].Ax;
        }

        table.EnsureArrayCapacity(((c - 1) * 50) + count);
        stack.GetBuffer().Slice(RA + 1, count).CopyTo(table.GetArraySpan()[((c - 1) * 50)..]);
        stack.PopUntil(RA + 1);
    }

    static void ComparePostOperation(
        VirtualMachineExecutionContext context,
        int lastTop,
        Span<LuaValue> results
    )
    {
        var compareResult = results.Length != 0 && results[0].ToBoolean();
        if (compareResult != (context.Instruction.A == 1))
        {
            context.Pc++;
        }

        results.Clear();
        context.Stack.PopUntil(lastTop);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref readonly LuaValue RKB(
        ref LuaValue stack,
        ref LuaValue constants,
        Instruction instruction
    )
    {
        var index = instruction.B;
        return ref index >= 256
            ? ref Unsafe.Add(ref constants, index - 256)
            : ref Unsafe.Add(ref stack, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref readonly LuaValue RKC(
        ref LuaValue stack,
        ref LuaValue constants,
        Instruction instruction
    )
    {
        var index = instruction.C;
        return ref index >= 256
            ? ref Unsafe.Add(ref constants, index - 256)
            : ref Unsafe.Add(ref stack, index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool GetTableValueSlowPath(
        LuaValue table,
        LuaValue key,
        VirtualMachineExecutionContext context,
        out LuaValue value,
        out bool doRestart
    )
    {
        var targetTable = table;
        const int MAX_LOOP = 100;
        doRestart = false;
        var skip = targetTable.Type == LuaValueType.Table;
        for (var i = 0; i < MAX_LOOP; i++)
        {
            if (table.TryReadTable(out var luaTable))
            {
                if (!skip && luaTable.TryGetValue(key, out value))
                {
                    return true;
                }

                skip = false;

                var metatable = luaTable.Metatable;
                if (metatable != null && metatable.TryGetValue(Metamethods.Index, out table))
                {
                    goto Function;
                }

                value = default;
                return true;
            }

            if (
                !table.TryGetMetamethod(
                    context.GlobalState,
                    Metamethods.Index,
                    out var metatableValue
                )
            )
            {
                ThrowInvalidOperationForNoIndex(context, i, table);
            }

            table = metatableValue;
            Function:
            if (table.TryReadFunction(out var function))
            {
                return CallGetTableFunc(
                    targetTable,
                    function,
                    key,
                    context,
                    out value,
                    out doRestart
                );
            }
        }

        throw new LuaRuntimeException(GetstateWithCurrentPc(context), "loop in gettable");

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInvalidOperationForNoIndex(
            VirtualMachineExecutionContext context,
            int loopCount,
            in LuaValue table
        )
        {
            if (loopCount != 0)
            {
                LuaRuntimeException.AttemptInvalidOperation(
                    GetstateWithCurrentPc(context),
                    "index",
                    table
                );
            }
            else if (context.Instruction.OpCode != OpCode.GetTabUp)
            {
                LuaRuntimeException.AttemptInvalidOperationOnLuaStack(
                    GetstateWithCurrentPc(context),
                    "index",
                    context.Pc,
                    context.Instruction.B
                );
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperationOnUpValues(
                    GetstateWithCurrentPc(context),
                    "index",
                    context.Instruction.B
                );
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallGetTableFunc(
        LuaValue table,
        LuaFunction indexTable,
        LuaValue key,
        VirtualMachineExecutionContext context,
        out LuaValue result,
        out bool doRestart
    )
    {
        doRestart = false;
        var stack = context.Stack;
        stack.Push(table);
        stack.Push(key);
        var newFrame = indexTable.CreateNewFrame(context, stack.Count - 2);

        context.State.PushCallStackFrame(newFrame);
        if (context.State.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
        {
            context.PostOperation =
                context.Instruction.OpCode == OpCode.GetTable
                    ? PostOperationType.SetResult
                    : PostOperationType.Self;
            context.Task = ExecuteCallHook(context, newFrame, 2);
            doRestart = false;
            result = default;
            return false;
        }

        if (indexTable is LuaClosure)
        {
            context.Push(newFrame);
            doRestart = true;
            result = default;
            return true;
        }

        var task = indexTable.Func(
            new()
            {
                State = context.State,
                ArgumentCount = 2,
                ReturnFrameBase = newFrame.ReturnBase,
            },
            context.CancellationToken
        );

        if (!task.IsCompleted)
        {
            context.PostOperation =
                context.Instruction.OpCode == OpCode.GetTable
                    ? PostOperationType.SetResult
                    : PostOperationType.Self;
            context.Task = task;
            result = default;
            return false;
        }

        var awaiter = task.GetAwaiter();
        awaiter.GetResult();
        var results = stack.GetBuffer()[newFrame.ReturnBase..];
        result = results.Length == 0 ? default : results[0];
        context.State.PopCallStackFrameWithStackPop();
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ValueTask<LuaValue> ExecuteGetTableSlowPath(
        LuaState state,
        LuaValue table,
        LuaValue key,
        CancellationToken ct
    )
    {
        var targetTable = table;
        const int MAX_LOOP = 100;
        var skip = targetTable.Type == LuaValueType.Table;
        for (var i = 0; i < MAX_LOOP; i++)
        {
            if (table.TryReadTable(out var luaTable))
            {
                if (!skip && luaTable.TryGetValue(key, out var value))
                {
                    return new(value);
                }

                skip = false;

                var metatable = luaTable.Metatable;
                if (metatable != null && metatable.TryGetValue(Metamethods.Index, out table))
                {
                    goto Function;
                }

                return default;
            }

            if (
                !table.TryGetMetamethod(
                    state.GlobalState,
                    Metamethods.Index,
                    out var metatableValue
                )
            )
            {
                LuaRuntimeException.AttemptInvalidOperation(state, "index", table);
            }

            table = metatableValue;
            Function:
            if (table.TryReadFunction(out var function))
            {
                return CallGetTableFunc(state, function, targetTable, key, ct);
            }
        }

        throw new LuaRuntimeException(state, "loop in gettable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    static async ValueTask<LuaValue> CallGetTableFunc(
        LuaState state,
        LuaFunction indexTable,
        LuaValue table,
        LuaValue key,
        CancellationToken ct
    )
    {
        var stack = state.Stack;
        var top = stack.Count;
        stack.Push(table);
        stack.Push(key);
        var varArgCount = indexTable.GetVariableArgumentCount(2);

        var newFrame = new CallStackFrame
        {
            Base = state.Stack.Count - 2 + varArgCount,
            VariableArgumentCount = varArgCount,
            Function = indexTable,
            ReturnBase = top,
        };

        state.PushCallStackFrame(newFrame);
        var functionContext = new LuaFunctionExecutionContext
        {
            State = state,
            ArgumentCount = 2,
            ReturnFrameBase = top,
        };
        if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
        {
            await ExecuteCallHook(functionContext, ct);
        }

        await indexTable.Func(functionContext, ct);
        var results = stack.GetBuffer()[newFrame.ReturnBase..];
        var result = results.Length == 0 ? default : results[0];
        state.PopCallStackFrameWithStackPop();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool SetTableValueSlowPath(
        LuaValue table,
        LuaValue key,
        LuaValue value,
        VirtualMachineExecutionContext context,
        out bool doRestart
    )
    {
        var targetTable = table;
        const int MAX_LOOP = 100;
        doRestart = false;
        var skip = targetTable.Type == LuaValueType.Table;
        for (var i = 0; i < MAX_LOOP; i++)
        {
            if (table.TryReadTable(out var luaTable))
            {
                targetTable = luaTable;
                ref var valueRef = ref skip
                    ? ref Unsafe.NullRef<LuaValue>()
                    : ref luaTable.FindValue(key);
                skip = false;
                if (!Unsafe.IsNullRef(ref valueRef) && valueRef.Type != LuaValueType.Nil)
                {
                    valueRef = value;
                    return true;
                }

                var metatable = luaTable.Metatable;
                if (metatable == null || !metatable.TryGetValue(Metamethods.NewIndex, out table))
                {
                    if (Unsafe.IsNullRef(ref valueRef))
                    {
                        luaTable[key] = value;
                        return true;
                    }

                    valueRef = value;
                    return true;
                }

                goto Function;
            }

            if (
                !table.TryGetMetamethod(
                    context.GlobalState,
                    Metamethods.NewIndex,
                    out var metatableValue
                )
            )
            {
                ThrowInvalidOperationForNoNewIndex(context, i, table);
            }

            table = metatableValue;

            Function:
            if (table.TryReadFunction(out var function))
            {
                context.PostOperation = PostOperationType.Nop;
                return CallSetTableFunc(targetTable, function, key, value, context, out doRestart);
            }
        }

        throw new LuaRuntimeException(GetstateWithCurrentPc(context), "loop in settable");

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInvalidOperationForNoNewIndex(
            VirtualMachineExecutionContext context,
            int loopCount,
            in LuaValue table
        )
        {
            if (loopCount != 0)
            {
                LuaRuntimeException.AttemptInvalidOperation(
                    GetstateWithCurrentPc(context),
                    "index",
                    table
                );
            }
            else if (context.Instruction.OpCode != OpCode.SetTabUp)
            {
                LuaRuntimeException.AttemptInvalidOperationOnLuaStack(
                    GetstateWithCurrentPc(context),
                    "index",
                    context.Pc,
                    context.Instruction.A
                );
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperationOnUpValues(
                    GetstateWithCurrentPc(context),
                    "index",
                    context.Instruction.A
                );
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallSetTableFunc(
        LuaValue table,
        LuaFunction newIndexFunction,
        LuaValue key,
        LuaValue value,
        VirtualMachineExecutionContext context,
        out bool doRestart
    )
    {
        doRestart = false;
        var state = context.State;
        var stack = state.Stack;
        stack.Push(table);
        stack.Push(key);
        stack.Push(value);
        var newFrame = newIndexFunction.CreateNewFrame(context, stack.Count - 3);

        context.State.PushCallStackFrame(newFrame);
        if (context.State.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
        {
            context.PostOperation = PostOperationType.Nop;
            context.Task = ExecuteCallHook(context, newFrame, 3);
            doRestart = false;
            return false;
        }

        if (newIndexFunction is LuaClosure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        var task = newIndexFunction.Func(
            new()
            {
                State = state,
                ArgumentCount = 3,
                ReturnFrameBase = newFrame.ReturnBase,
            },
            context.CancellationToken
        );
        if (!task.IsCompleted)
        {
            context.PostOperation = PostOperationType.Nop;
            context.Task = task;
            return false;
        }

        task.GetAwaiter().GetResult();
        state.PopCallStackFrameWithStackPop();
        return true;
    }

    internal static ValueTask ExecuteSetTableSlowPath(
        LuaState state,
        LuaValue table,
        LuaValue key,
        LuaValue value,
        CancellationToken ct
    )
    {
        var targetTable = table;
        const int MAX_LOOP = 100;
        var skip = targetTable.Type == LuaValueType.Table;
        for (var i = 0; i < MAX_LOOP; i++)
        {
            if (table.TryReadTable(out var luaTable))
            {
                targetTable = luaTable;
                ref var valueRef = ref skip
                    ? ref Unsafe.NullRef<LuaValue>()
                    : ref luaTable.FindValue(key);
                skip = false;
                if (!Unsafe.IsNullRef(ref valueRef) && valueRef.Type != LuaValueType.Nil)
                {
                    valueRef = value;
                    return default;
                }

                var metatable = luaTable.Metatable;
                if (metatable == null || !metatable.TryGetValue(Metamethods.NewIndex, out table))
                {
                    if (Unsafe.IsNullRef(ref valueRef))
                    {
                        luaTable[key] = value;
                        return default;
                    }

                    valueRef = value;
                    return default;
                }

                goto Function;
            }

            if (
                !table.TryGetMetamethod(
                    state.GlobalState,
                    Metamethods.NewIndex,
                    out var metatableValue
                )
            )
            {
                LuaRuntimeException.AttemptInvalidOperation(state, "index", table);
            }

            table = metatableValue;

            Function:
            if (table.TryReadFunction(out var function))
            {
                return CallSetTableFunc(state, function, targetTable, key, value, ct);
            }
        }

        throw new LuaRuntimeException(state, "loop in settable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder))]
    static async ValueTask CallSetTableFunc(
        LuaState state,
        LuaFunction newIndexFunction,
        LuaValue table,
        LuaValue key,
        LuaValue value,
        CancellationToken ct
    )
    {
        var stack = state.Stack;
        var top = stack.Count;
        stack.Push(table);
        stack.Push(key);
        stack.Push(value);
        var varArgCount = newIndexFunction.GetVariableArgumentCount(3);

        var newFrame = new CallStackFrame
        {
            Base = state.Stack.Count - 3 + varArgCount,
            VariableArgumentCount = varArgCount,
            Function = newIndexFunction,
            ReturnBase = top,
        };

        state.PushCallStackFrame(newFrame);
        var functionContext = new LuaFunctionExecutionContext
        {
            State = state,
            ArgumentCount = 3,
            ReturnFrameBase = top,
        };
        if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
        {
            await ExecuteCallHook(functionContext, ct);
        }

        await newIndexFunction.Func(functionContext, ct);
        state.PopCallStackFrameWithStackPop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecuteBinaryOperationMetaMethod(
        LuaValue vb,
        LuaValue vc,
        VirtualMachineExecutionContext context,
        OpCode opCode,
        out bool doRestart
    )
    {
        var (name, description) = opCode.GetNameAndDescription();
        doRestart = false;
        if (
            vb.TryGetMetamethod(context.GlobalState, name, out var metamethod)
            || vc.TryGetMetamethod(context.GlobalState, name, out metamethod)
        )
        {
            var stack = context.Stack;
            var newBase = stack.Count;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(
                        context.GlobalState,
                        Metamethods.Call,
                        out metamethod
                    ) && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(
                        GetstateWithCurrentPc(context),
                        "call",
                        metamethod
                    );
                }
            }

            stack.Push(vb);
            stack.Push(vc);
            var (argCount, variableArgumentCount) = PrepareForFunctionCall(
                context.State,
                func,
                newBase
            );
            newBase += variableArgumentCount;
            var newFrame = func.CreateNewFrame(context, newBase, newBase, variableArgumentCount);

            context.State.PushCallStackFrame(newFrame);
            if (context.State.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
            {
                context.PostOperation = PostOperationType.SetResult;
                context.Task = ExecuteCallHook(context, newFrame, argCount);
                doRestart = false;
                return false;
            }

            if (func is LuaClosure)
            {
                context.Push(newFrame);
                doRestart = true;
                return true;
            }

            var task = func.Func(
                new()
                {
                    State = context.State,
                    ArgumentCount = argCount,
                    ReturnFrameBase = newFrame.ReturnBase,
                },
                context.CancellationToken
            );

            if (!task.IsCompleted)
            {
                context.PostOperation = PostOperationType.SetResult;
                context.Task = task;
                return false;
            }

            var destIndex = context.Instruction.A + context.FrameBase;
            if (newFrame.ReturnBase != destIndex)
            {
                stack.Get(destIndex) =
                    task.GetAwaiter().GetResult() != 0
                        ? stack.FastGet(newFrame.ReturnBase)
                        : default;
            }

            stack.PopUntil(Math.Max(destIndex + 1, newFrame.Base - newFrame.VariableArgumentCount));
            context.State.PopCallStackFrame();
            return true;
        }

        LuaRuntimeException.AttemptInvalidOperationOnLuaStack(
            GetstateWithCurrentPc(context),
            description,
            context.Pc,
            context.Instruction.B,
            context.Instruction.C
        );
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<LuaValue> ExecuteBinaryOperationMetaMethod(
        LuaState state,
        LuaValue vb,
        LuaValue vc,
        OpCode opCode,
        CancellationToken ct
    )
    {
        var (name, description) = opCode.GetNameAndDescription();

        if (
            vb.TryGetMetamethod(state.GlobalState, name, out var metamethod)
            || vc.TryGetMetamethod(state.GlobalState, name, out metamethod)
        )
        {
            var stack = state.Stack;
            var newBase = stack.Count;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(state.GlobalState, Metamethods.Call, out metamethod)
                    && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(state, "call", metamethod);
                }
            }

            stack.Push(vb);
            stack.Push(vc);
            var (argCount, variableArgumentCount) = PrepareForFunctionCall(state, func, newBase);
            newBase += variableArgumentCount;

            var newFrame = new CallStackFrame
            {
                Base = newBase,
                VariableArgumentCount = variableArgumentCount,
                Function = func,
                ReturnBase = newBase,
            };

            state.PushCallStackFrame(newFrame);
            try
            {
                var functionContext = new LuaFunctionExecutionContext
                {
                    State = state,
                    ArgumentCount = argCount,
                    ReturnFrameBase = newBase,
                };
                if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
                {
                    await ExecuteCallHook(functionContext, ct);
                }

                await func.Func(functionContext, ct);
                var results = stack.AsSpan()[newFrame.ReturnBase..];
                var result = results.Length == 0 ? default : results[0];
                return result;
            }
            finally
            {
                state.PopCallStackFrameWithStackPop();
            }
        }

        LuaRuntimeException.AttemptInvalidOperation(state, description, vb, vc);
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecuteUnaryOperationMetaMethod(
        LuaValue vb,
        VirtualMachineExecutionContext context,
        OpCode opCode,
        out bool doRestart
    )
    {
        var (name, description) = opCode.GetNameAndDescription();
        doRestart = false;
        var stack = context.Stack;
        if (vb.TryGetMetamethod(context.GlobalState, name, out var metamethod))
        {
            var newBase = stack.Count;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(
                        context.GlobalState,
                        Metamethods.Call,
                        out metamethod
                    ) && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(
                        GetstateWithCurrentPc(context),
                        "call",
                        metamethod
                    );
                }
            }

            stack.Push(vb);
            stack.Push(vb);
            var (argCount, variableArgumentCount) = PrepareForFunctionCall(
                context.State,
                func,
                newBase
            );
            newBase += variableArgumentCount;

            var newFrame = func.CreateNewFrame(context, newBase, newBase, variableArgumentCount);

            context.State.PushCallStackFrame(newFrame);
            if (context.State.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
            {
                context.PostOperation = PostOperationType.SetResult;
                context.Task = ExecuteCallHook(context, newFrame, argCount);
                doRestart = false;
                return false;
            }

            if (func is LuaClosure)
            {
                context.Push(newFrame);
                doRestart = true;
                return true;
            }

            var task = func.Func(
                new()
                {
                    State = context.State,
                    ArgumentCount = argCount,
                    ReturnFrameBase = newFrame.ReturnBase,
                },
                context.CancellationToken
            );

            if (!task.IsCompleted)
            {
                context.PostOperation = PostOperationType.SetResult;
                context.Task = task;
                return false;
            }

            var destIndex = context.Instruction.A + context.FrameBase;
            if (newFrame.ReturnBase != destIndex)
            {
                stack.Get(destIndex) =
                    task.GetAwaiter().GetResult() != 0
                        ? stack.FastGet(newFrame.ReturnBase)
                        : default;
            }

            stack.PopUntil(Math.Max(destIndex + 1, newFrame.Base - newFrame.VariableArgumentCount));
            context.State.PopCallStackFrame();
            return true;
        }

        if (opCode == OpCode.Len && vb.TryReadTable(out var table))
        {
            var RA = context.Instruction.A + context.FrameBase;
            stack.Get(RA) = table.ArrayLength;
            stack.NotifyTop(RA + 1);
            return true;
        }

        LuaRuntimeException.AttemptInvalidOperationOnLuaStack(
            GetstateWithCurrentPc(context),
            description,
            context.Pc,
            context.Instruction.B
        );
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<LuaValue> ExecuteUnaryOperationMetaMethod(
        LuaState state,
        LuaValue vb,
        OpCode opCode,
        CancellationToken cancellationToken
    )
    {
        state.ThrowIfCancellationRequested(cancellationToken);

        var (name, description) = opCode.GetNameAndDescription();

        if (vb.TryGetMetamethod(state.GlobalState, name, out var metamethod))
        {
            var stack = state.Stack;
            var newBase = stack.Count;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(state.GlobalState, Metamethods.Call, out metamethod)
                    && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(state, "call", metamethod);
                }
            }

            stack.Push(vb);
            stack.Push(vb);
            var (argCount, variableArgumentCount) = PrepareForFunctionCall(state, func, newBase);
            newBase += variableArgumentCount;
            var newFrame = new CallStackFrame
            {
                Base = newBase,
                VariableArgumentCount = variableArgumentCount,
                Function = func,
                ReturnBase = newBase,
            };

            state.PushCallStackFrame(newFrame);
            try
            {
                var functionContext = new LuaFunctionExecutionContext
                {
                    State = state,
                    ArgumentCount = argCount,
                    ReturnFrameBase = newBase,
                };
                if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
                {
                    await ExecuteCallHook(functionContext, cancellationToken);
                }
                else
                {
                    await func.Func(functionContext, cancellationToken);
                }

                state.ThrowIfCancellationRequested(cancellationToken);
                var results = stack.AsSpan()[newFrame.ReturnBase..];
                var result = results.Length == 0 ? default : results[0];
                results.Clear();
                return result;
            }
            finally
            {
                state.PopCallStackFrameWithStackPop();
            }
        }

        LuaRuntimeException.AttemptInvalidOperation(state, description, vb);
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecuteCompareOperationMetaMethod(
        LuaValue vb,
        LuaValue vc,
        VirtualMachineExecutionContext context,
        OpCode opCode,
        out bool doRestart
    )
    {
        var name = opCode.GetName();
        doRestart = false;
        var reverseLe = false;
        ReCheck:
        if (
            vb.TryGetMetamethod(context.GlobalState, name, out var metamethod)
            || vc.TryGetMetamethod(context.GlobalState, name, out metamethod)
        )
        {
            var stack = context.Stack;
            var argCount = 2;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(
                        context.GlobalState,
                        Metamethods.Call,
                        out metamethod
                    ) && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                    argCount++;
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(
                        GetstateWithCurrentPc(context),
                        "call",
                        metamethod
                    );
                }
            }

            stack.Push(vb);
            stack.Push(vc);
            var varArgCount = func.GetVariableArgumentCount(argCount);
            var newFrame = func.CreateNewFrame(context, stack.Count - argCount + varArgCount);
            if (reverseLe)
            {
                newFrame.Flags |= CallStackFrameFlags.ReversedLe;
            }

            context.State.PushCallStackFrame(newFrame);
            if (context.State.CallOrReturnHookMask.Value != 0 && !context.State.IsInHook)
            {
                context.PostOperation = PostOperationType.Compare;
                context.Task = ExecuteCallHook(context, newFrame, argCount);
                doRestart = false;
                return false;
            }

            if (func is LuaClosure)
            {
                context.Push(newFrame);
                doRestart = true;
                return true;
            }

            var task = func.Func(
                new()
                {
                    State = context.State,
                    ArgumentCount = argCount,
                    ReturnFrameBase = newFrame.ReturnBase,
                },
                context.CancellationToken
            );

            if (!task.IsCompleted)
            {
                context.PostOperation = PostOperationType.Compare;
                context.Task = task;
                return false;
            }

            var results = stack.AsSpan()[newFrame.ReturnBase..];
            var compareResult = results.Length > 0 && results[0].ToBoolean();
            compareResult = reverseLe ? !compareResult : compareResult;
            if (compareResult != (context.Instruction.A == 1))
            {
                context.Pc++;
            }

            stack.PopUntil(newFrame.Base - newFrame.VariableArgumentCount + 1);
            context.State.PopCallStackFrame();

            return true;
        }

        if (opCode == OpCode.Le && !reverseLe)
        {
            reverseLe = true;
            name = Metamethods.Lt;
            (vb, vc) = (vc, vb);
            goto ReCheck;
        }

        if (opCode != OpCode.Eq)
        {
            if (reverseLe)
            {
                (vb, vc) = (vc, vb);
            }

            LuaRuntimeException.AttemptInvalidOperation(
                GetstateWithCurrentPc(context),
                "compare",
                vb,
                vc
            );
        }
        else
        {
            if (context.Instruction.A == 1)
            {
                context.Pc++;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<bool> ExecuteCompareOperationMetaMethod(
        LuaState state,
        LuaValue vb,
        LuaValue vc,
        OpCode opCode,
        CancellationToken cancellationToken
    )
    {
        state.ThrowIfCancellationRequested(cancellationToken);

        var name = opCode.GetName();
        var reverseLe = false;
        ReCheck:
        if (
            vb.TryGetMetamethod(state.GlobalState, name, out var metamethod)
            || vc.TryGetMetamethod(state.GlobalState, name, out metamethod)
        )
        {
            var stack = state.Stack;
            var newBase = stack.Count;
            var callable = metamethod;
            if (!metamethod.TryReadFunction(out var func))
            {
                if (
                    metamethod.TryGetMetamethod(state.GlobalState, Metamethods.Call, out metamethod)
                    && metamethod.TryReadFunction(out func)
                )
                {
                    stack.Push(callable);
                }
                else
                {
                    LuaRuntimeException.AttemptInvalidOperation(state, "call", metamethod);
                }
            }

            stack.Push(vb);
            stack.Push(vc);
            var (argCount, variableArgumentCount) = PrepareForFunctionCall(state, func, newBase);
            newBase += variableArgumentCount;
            var newFrame = new CallStackFrame
            {
                Base = newBase,
                VariableArgumentCount = variableArgumentCount,
                Function = func,
                ReturnBase = newBase,
            };

            state.PushCallStackFrame(newFrame);
            try
            {
                var functionContext = new LuaFunctionExecutionContext
                {
                    State = state,
                    ArgumentCount = argCount,
                    ReturnFrameBase = newBase,
                };
                if (state.CallOrReturnHookMask.Value != 0 && !state.IsInHook)
                {
                    await ExecuteCallHook(functionContext, cancellationToken);
                }
                else
                {
                    await func.Func(functionContext, cancellationToken);
                }

                state.ThrowIfCancellationRequested(cancellationToken);
                var results = stack.AsSpan()[newFrame.ReturnBase..];
                var result = results.Length == 0 ? default : results[0];
                results.Clear();
                return result.ToBoolean();
            }
            finally
            {
                state.PopCallStackFrame();
            }
        }

        if (opCode == OpCode.Le && !reverseLe)
        {
            reverseLe = true;
            name = Metamethods.Lt;
            (vb, vc) = (vc, vb);
            goto ReCheck;
        }

        if (opCode != OpCode.Eq)
        {
            if (reverseLe)
            {
                (vb, vc) = (vc, vb);
            }

            var (_, description) = opCode.GetNameAndDescription();
            LuaRuntimeException.AttemptInvalidOperation(state, description, vb, vc);
        }

        return default;
    }

    // If there are variable arguments, the base of the stack is moved by that number and the values of the variable arguments are placed in front of it.
    // see: https://wubingzheng.github.io/build-lua-in-rust/en/ch08-02.arguments.html
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void PrepareVariableArgument(
        LuaStack stack,
        int argumentCount,
        int variableArgumentCount
    )
    {
        var top = stack.Count;
        var newBase = stack.Count - argumentCount;
        var temp = newBase;
        newBase += variableArgumentCount;
        stack.EnsureCapacity(newBase + argumentCount);
        stack.NotifyTop(newBase + argumentCount);
        var stackBuffer = stack.GetBuffer()[temp..];
        stackBuffer[..argumentCount].CopyTo(stackBuffer[variableArgumentCount..]);
        stackBuffer.Slice(argumentCount, variableArgumentCount).CopyTo(stackBuffer);
        stack.PopUntil(top);
    }

    // If there are variable arguments, the base of the stack is moved by that number and the values of the variable arguments are placed in front of it.
    // see: https://wubingzheng.github.io/build-lua-in-rust/en/ch08-02.arguments.html
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (int ArgumentCount, int VariableArgumentCount) PrepareVariableArgument(
        LuaStack stack,
        int newBase,
        int argumentCount,
        int variableArgumentCount
    )
    {
        var temp = newBase;
        newBase += variableArgumentCount;

        stack.EnsureCapacity(newBase + argumentCount);
        stack.NotifyTop(newBase + argumentCount);

        var stackBuffer = stack.GetBuffer()[temp..];
        stackBuffer[..argumentCount].CopyTo(stackBuffer[variableArgumentCount..]);
        stackBuffer.Slice(argumentCount, variableArgumentCount).CopyTo(stackBuffer);
        return (argumentCount, variableArgumentCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (int ArgumentCount, int VariableArgumentCount) PrepareForFunctionCall(
        LuaState state,
        LuaFunction function,
        Instruction instruction,
        int newBase,
        bool isMetamethod
    )
    {
        var argumentCount = instruction.B - 1;
        if (argumentCount == -1)
        {
            argumentCount = (ushort)(state.Stack.Count - newBase);
        }
        else
        {
            if (isMetamethod)
            {
                argumentCount += 1;
            }

            state.Stack.SetTop(newBase + argumentCount);
        }

        var variableArgumentCount = function.GetVariableArgumentCount(argumentCount);

        if (variableArgumentCount < 0)
        {
            state.Stack.SetTop(state.Stack.Count - variableArgumentCount);
            argumentCount -= variableArgumentCount;
            variableArgumentCount = 0;
        }

        if (variableArgumentCount == 0)
        {
            return (argumentCount, 0);
        }

        return PrepareVariableArgument(state.Stack, newBase, argumentCount, variableArgumentCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (int ArgumentCount, int VariableArgumentCount) PrepareForFunctionCall(
        LuaState state,
        LuaFunction function,
        int newBase
    )
    {
        var argumentCount = (int)(state.Stack.Count - newBase);

        var variableArgumentCount = function.GetVariableArgumentCount(argumentCount);

        if (variableArgumentCount < 0)
        {
            state.Stack.SetTop(state.Stack.Count - variableArgumentCount);
            argumentCount -= variableArgumentCount;
            variableArgumentCount = 0;
        }

        if (variableArgumentCount == 0)
        {
            return (argumentCount, 0);
        }

        return PrepareVariableArgument(state.Stack, newBase, argumentCount, variableArgumentCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (int ArgumentCount, int VariableArgumentCount) PrepareForFunctionTailCall(
        LuaState state,
        LuaFunction function,
        Instruction instruction,
        int newBase,
        bool isMetamethod
    )
    {
        var stack = state.Stack;

        var argumentCount = instruction.B - 1;
        if (instruction.B == 0)
        {
            argumentCount = (ushort)(stack.Count - newBase);
        }
        else
        {
            if (isMetamethod)
            {
                argumentCount += 1;
            }

            state.Stack.SetTop(newBase + argumentCount);
        }

        // In the case of tailcall, the local variables of the caller are immediately discarded, so there is no need to retain them.
        // Therefore, a call can be made without allocating new registers.
        var currentBase = state.GetCurrentFrame().Base;
        {
            var stackBuffer = stack.GetBuffer();
            if (argumentCount > 0)
            {
                stackBuffer
                    .Slice(newBase, argumentCount)
                    .CopyTo(stackBuffer.Slice(currentBase, argumentCount));
            }

            newBase = currentBase;
        }

        var variableArgumentCount = function.GetVariableArgumentCount(argumentCount);

        if (variableArgumentCount <= 0)
        {
            return (argumentCount, 0);
        }

        return PrepareVariableArgument(state.Stack, newBase, argumentCount, variableArgumentCount);
    }

    static LuaState GetstateWithCurrentPc(VirtualMachineExecutionContext context)
    {
        GetstateWithCurrentPc(context.State, context.Pc);
        return context.State;
    }

    static void GetstateWithCurrentPc(LuaState state, int pc)
    {
        var frame = state.GetCurrentFrame();
        state.PushCallStackFrame(
            frame with
            {
                CallerInstructionIndex = pc,
                Flags = frame.Flags & (0 ^ CallStackFrameFlags.TailCall),
            }
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static CallStackFrame CreateNewFrame(
        this LuaFunction function,
        VirtualMachineExecutionContext context,
        int newBase
    )
    {
        return new()
        {
            Base = newBase,
            ReturnBase = newBase,
            Function = function,
            VariableArgumentCount = 0,
            CallerInstructionIndex = context.Pc,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static CallStackFrame CreateNewFrame(
        this LuaFunction function,
        VirtualMachineExecutionContext context,
        int newBase,
        int returnBase,
        int variableArgumentCount
    )
    {
        return new()
        {
            Base = newBase,
            ReturnBase = returnBase,
            Function = function,
            VariableArgumentCount = variableArgumentCount,
            CallerInstructionIndex = context.Pc,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static CallStackFrame CreateNewTailCallFrame(
        this LuaFunction function,
        VirtualMachineExecutionContext context,
        int newBase,
        int returnBase,
        int variableArgumentCount
    )
    {
        return new()
        {
            Base = newBase,
            ReturnBase = returnBase,
            Function = function,
            VariableArgumentCount = variableArgumentCount,
            CallerInstructionIndex = context.Pc,
            Flags = CallStackFrameFlags.TailCall,
        };
    }
}
