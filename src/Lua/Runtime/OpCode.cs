namespace Lua.Runtime;

public enum OpCode : byte
{
    Move, // A B     R(A) := R(B)
    LoadK, // A Bx    R(A) := Kst(Bx)
    LoadKX, // A       R(A) := Kst(extra arg)
    LoadBool, // A B C   R(A) := (Bool)B; if (C) pc++
    LoadNil, // A B     R(A), R(A+1), ..., R(A+B) := nil

    GetUpVal, // A B     R(A) := UpValue[B]
    GetTabUp, // A B C   R(A) := UpValue[B][RK(C)]
    GetTable, // A B C   R(A) := R(B)[RK(C)]

    SetTabUp, // A B C   UpValue[A][RK(B)] := RK(C)
    SetUpVal, // A B     UpValue[B] := R(A)
    SetTable, // A B C   R(A)[RK(B)] := RK(C)

    NewTable, // A B C   R(A) := {} (size = B,C)

    Self, // A B C   R(A+1) := R(B); R(A) := R(B)[RK(C)]

    Add, // A B C   R(A) := RK(B) + RK(C)
    Sub, // A B C   R(A) := RK(B) - RK(C)
    Mul, // A B C   R(A) := RK(B) * RK(C)
    Div, // A B C   R(A) := RK(B) / RK(C)
    Mod, // A B C   R(A) := RK(B) % RK(C)
    Pow, // A B C   R(A) := RK(B) ^ RK(C)
    Unm, // A B     R(A) := -R(B)
    Not, // A B     R(A) := not R(B)
    Len, // A B     R(A) := length of R(B)

    Concat, // A B C   R(A) := R(B).. ... ..R(C)

    Jmp, // A sBx   pc+=sBx; if (A) close all upvalues >= R(A - 1)
    Eq, // A B C   if ((RK(B) == RK(C)) ~= A) then pc++
    Lt, // A B C   if ((RK(B) <  RK(C)) ~= A) then pc++
    Le, // A B C   if ((RK(B) <= RK(C)) ~= A) then pc++

    Test, // A C     if not (R(A) <=> C) then pc++
    TestSet, // A B C   if (R(B) <=> C) then R(A) := R(B) else pc++

    Call, // A B C   R(A), ... ,R(A+C-2) := R(A)(R(A+1), ... ,R(A+B-1))
    TailCall, // A B C   return R(A)(R(A+1), ... ,R(A+B-1))
    Return, // A B     return R(A), ... ,R(A+B-2)      (see note)

    ForLoop, // A sBx   R(A)+=R(A+2);

    //         if R(A) <?= R(A+1) then { pc+=sBx; R(A+3)=R(A) }
    ForPrep, // A sBx   R(A)-=R(A+2); pc+=sBx

    TForCall, // A C     R(A+3), ... ,R(A+2+C) := R(A)(R(A+1), R(A+2));
    TForLoop, // A sBx   if R(A+1) ~= nil then { R(A)=R(A+1); pc += sBx }

    SetList, // A B C   R(A)[(C-1)*FPF+i] := R(A+i), 1 <= i <= B

    Closure, // A Bx    R(A) := closure(KPROTO[Bx])

    VarArg, // A B     R(A), R(A+1), ..., R(A+B-2) = vararg

    ExtraArg, // Ax      extra (larger) argument for previous opcode

    // --- Luau additions (NFM-World fork). Appended after ExtraArg so every existing
    // opcode keeps its numeric value (dumped chunks stay loadable). ---

    IDiv, // A B C   R(A) := RK(B) // RK(C)
    LoadBuiltin, // A Bx    R(A) := state builtin[Bx]

    // A sBx, followed by an ExtraArg whose Ax is a constant-pool index (consumed inline, the
    // same way LoadKX consumes a following ExtraArg -- see LuaVirtualMachine.cs's LoadKX case).
    // Fuses `x == <const>` / `x ~= <const>` compare-and-branch into one dispatch instead of
    // Eq+Jmp. Only ever emitted when the constant side's type makes __eq categorically
    // unreachable (nil/bool/number/string), so this never bypasses a metamethod.
    JmpIfEqK, // A sBx   if R(A) == Kst(extra arg) then pc += sBx
    JmpIfNeK, // A sBx   if R(A) ~= Kst(extra arg) then pc += sBx

    // A B C, followed by an ExtraArg whose Ax is the *second* key's constant-pool index
    // (consumed inline, the same way LoadKX and JmpIfEqK consume a following ExtraArg).
    // Fuses the two-level chain `UpValue[B][RK(C)][Kst(extra arg)]` into one dispatch:
    // `math.floor` (B = the `_ENV` upvalue, C = "math", extra = "floor") or `M.nested.x` for a
    // captured `M`. A/B/C are exactly GetTabUp's operands, so the fused instruction is a strict
    // superset of that one -- it is only ever emitted where GetTabUp + GetTable would have been.
    //
    // Fast-path-only: it performs both lookups as raw table reads and, on ANY miss, rewrites
    // itself in place into the ordinary GetTabUp + GetTable pair and re-executes that, so
    // metamethods, __index chains and error messages stay identical to the unfused compilation.
    // See OpGetImport in LuaVirtualMachine.cs.
    GetImport, // A B C   R(A) := UpValue[B][RK(C)][Kst(extra arg)]

    // A, followed by an ExtraArg whose Ax is an index into this prototype's table-template pool
    // (Prototype.Templates). R(A) := a fresh copy of template[extra arg].
    // (See OpDupTable in LuaVirtualMachine.cs.)
    //
    // Fuses an all-constant constructor -- `{ display = "flex", inset = 0 }`, the shape of a Luau
    // style table -- into one dispatch instead of a NewTable plus a LoadK/SetTable per field.
    // The template is built at compile time by replaying the constructor's field list, so the copy
    // is exactly what the unfused compilation would have built, at the same capacities.
    //
    // Unlike GetImport there is no deopt: fusing is a compile-time property of the literal, the
    // template is already built, and a copy cannot fail. Templates are not in Constants, so no
    // LoadK, LoadKX or RK operand can name one -- DupTable's ExtraArg only ever copies *out* of
    // one -- which is what makes sharing a single instance across every execution safe.
    DupTable, // A       R(A) := copy of template[extra arg]
}
