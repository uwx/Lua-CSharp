using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Lua;

/// <summary>
/// Raw bytecode instruction count executed by <see cref="Lua.Runtime.LuaVirtualMachine"/>'s
/// dispatch loop. Deliberately a plain (non-Interlocked) counter -- a single LuaState
/// executes on one thread at a time, and this sits in the single hottest loop in the whole
/// interpreter, so any synchronization here would skew exactly what it's trying to measure.
/// Not wired into any tracing system on purpose - read the field directly (e.g. snapshot
/// before/after a window of interest) rather than treating this as a general-purpose profiler.
/// </summary>
public static class LuaVmDiagnostics
{
    internal static long instructionCount;

    public static long InstructionCount => instructionCount;

    public static void Reset()
    {
        instructionCount = 0;
    }

    [Conditional("LUA_VM_DIAGNOSTICS")]
    internal static void CountInstruction()
    {
        instructionCount++;
    }
}
