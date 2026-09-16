# The two-pass compiler: architecture and extension points

This is the design note the compiler-rewrite plan's Milestone 5 asked for: what the AST +
codegen pipeline looks like from the inside, and where a new Luau-style optimization plugs into
it. It documents the architecture that Milestones 1–4 built, not a proposal. Two optimizations
live in it today — [GetImport](#worked-example-getimport) and [DupTable](#worked-example-duptable)
— and they are the worked examples below.

It is written for someone adding the third one. Read it before you start; several of the
constraints here are silent failure modes rather than compile errors.

## The pipeline

`Parser.Parse` is the whole production entry point, and it is three lines
(`src/Lua/CodeAnalysis/Compilation/Parser.cs:2037`):

```csharp
var body = AstParser.ParseToAst(l, r, name);
return CodeGenerator.Generate(l, body, name);
```

| Stage | File | Does | Does not |
|---|---|---|---|
| Scan | `Scanner.cs` | Tokens | — |
| Parse | `Ast/AstParser.cs` (44 KB) | Builds the AST **and all scope/name resolution**: blocks, locals, upvalues, `const` bindings, goto/label bookkeeping, `repeat`-`until` scope rules | Allocate registers, intern constants, emit a single instruction |
| — | `Ast/Expr.cs`, `Ast/Stat.cs` | The node types (data only) | — |
| Codegen | `Ast/CodeGenerator.cs` (64 KB) | Walks the AST, drives `Function.cs`'s proven machinery: register allocation, constant pool, instruction encoding, jump-list patching | Tokenize, decide scoping |

`Function.cs` (2.3k lines, the jump/goto/upvalue-closing/table-constructor machinery) is used
verbatim by both passes — it is not split. `Parser` is instantiated by `CodeGenerator` purely as
a state container (`.Function`, `.ActiveVariables`, `.PendingGotos`, `.Scanner.LastLine`); no
tokenizing happens in codegen and `Next()`/`T()`/`TestNext()` are never called. `Parser` also
retains its original single-pass `MainFunction()` path, which is now unreachable — that is
deliberate, and it is the reference implementation to read when you need to know what a
particular emission *used* to do.

Why two passes at all: the single-pass compiler emits as it parses, so by the time it has parsed
the second level of `math.floor` or the last field of `{ display = "flex", ... }` the earlier
levels are already committed bytecode. Neither optimization is expressible without seeing the
whole chain or the whole literal first. See the compiler-rewrite plan's Context section for the
full account of that decision.

## The one contract that matters most

**Name resolution happens twice, on purpose.**

`AstParser` resolves names as it builds the AST, and records the result on `NameExpr.Kind` /
`NameExpr.Index`. `CodeGenerator` **ignores those fields** and re-resolves every name from
scratch through `Function.SingleVariableHelper`, keyed off `NameExpr.Name`
(`CodeGenerator.ResolveNameExpr`, `CodeGenerator.cs:915`):

```csharp
var (e, found) = SingleVariableHelper(p.Function, name.Name, true);
```

The reason is that `Function.cs`'s bookkeeping — `ActiveVariableCount`, `FreeRegisterCount`,
`Block` nesting, `Proto.UpValuesList` — has to be replayed in lockstep with emission anyway, so
resolution has to happen again during the walk regardless; doing it fresh also re-derives the
same indices for free and keeps `CodeGenerator` a close mirror of `Parser.cs` for reviewability.

This has one consequence that will bite you, and it is the single most important thing in this
document:

> **A transform pass must not change which declarations are active at any point.**

Because codegen re-resolves by name against whatever declarations its own walk has opened,
deleting or reordering a `local` — or deleting a *block* that declares one, or reordering
statements across a declaration — silently **rebinds names** rather than producing a compile
error. A name that used to be a local becomes a global lookup; a shadowed outer local becomes
visible again. The AST's baked `NameExpr.Index` fields will look perfectly consistent while the
emitted bytecode indexes something else entirely. Bytecode-shape tests catch this; nothing else
will.

So the safe shape for a transform is **rewrite expressions in place, keep the declaration
structure exactly**. If you find you need to remove a declaration, you need to reason about
every later reference to that name in the same function, and you should probably not.

## Where a new pass goes

Two insertion points, in order of preference:

1. **Inside `CodeGenerator`'s visitors**, as a "try to fuse this" branch at the top of the
   relevant `Emit*` method that returns `false` without having touched anything on failure. Both
   existing optimizations do this — `TryEmitGetImport` (`CodeGenerator.cs:971`) from
   `EmitIndex`, `TryEmitDupTable` (`CodeGenerator.cs:1313`) from the table-constructor visitor.
   This is preferred because the pass gets the same `Parser`/`Function` state the unfused path
   has, and "bail out" means literally falling through to the code that was there before.
2. **As a separate AST→AST pass between parse and codegen.** Better if the transform is
   independent of emission (a whole-function analysis, a hoisting pass). Nothing wires one up
   today; it would go in `Parser.Parse` between the two calls. Read the contract above first.

A note on where `TryFoldToConstant` lives: it is currently `private static` in `CodeGenerator`
(`CodeGenerator.cs:1441`). A standalone pass that needs it should move it somewhere shared rather
than reimplement it — it is the definition of "this expression is a compile-time constant in this
compiler," and two copies would drift.

### The shape every optimization here follows

Both existing optimizations are built the same way, and the pattern is worth reusing:

- **A pure predicate, evaluated before anything is emitted.** The predicate is pure AST
  inspection with no emission and *no name resolution*, so deciding "no" costs nothing and
  cannot perturb compiler state — no register reserved, no constant interned, no pool reorder.
  That is what makes the fallback path bit-for-bit identical to the code from before the
  optimization existed.
- **One relocation result.** The fused instruction is emitted with a placeholder destination
  (`A = 0`) and returned as `Kind.Relocatable`, so the *consumer* binds the register through the
  existing `DischargeToRegister` path. Register bookkeeping around the call site is therefore
  untouched — this is why neither optimization needed a single change to register allocation.
- **The two-word instruction shape** for anything that needs more than 8-bit operands:
  the instruction plus a trailing `ExtraArg` whose `Ax` is a 24-bit index. `LoadKX`,
  `JmpIfEqK`, `GetImport` and `DupTable` all use it. The dispatcher's fetch pre-increments
  `context.Pc` (`LuaVirtualMachine.cs:441`) *before* the debug-hook check reads it, so the
  handler consumes the `ExtraArg` with `Unsafe.Add(ref instructionsHead, ++context.Pc).Ax` and
  leaves `Pc` on that word — which is why an `ExtraArg` is structurally unobservable outside its
  own handler.
- **Deopt, if the fast path can miss.** `GetImport` can miss at runtime (a proxy, an `__index`
  chain), so on a miss it *rewrites its own two words in place* into the real
  `GetTabUp` + `GetTable` pair and sets `context.Pc = pc - 1` to re-execute them. After one miss
  the instruction stream literally is the unfused compilation, so metamethods, frame restarts
  and error text are the proven machinery's, not a reimplementation. `DupTable` has no miss case
  at all — a template copy cannot fail — which is why it has no deopt path.

<a id="worked-example-getimport"></a>
### Worked example: GetImport

`GetImport A B C` + `ExtraArg (Ax = second key's constant index)` fuses `math.floor`-style
two-level dotted lookups. `A`/`B`/`C` mean exactly what they mean for `GetTabUp`
(`R(A) := UpValue[B][RK(C)]`), then the extra key is applied — so the fused instruction is a
strict superset of the pair it replaces, and it is only ever emitted where that pair would have
been.

`EmitIndex` tries it *before* recursing into `index.Object`, necessarily: the inner level would
otherwise be emitted eagerly. It fires only when both levels are dotted-sugar `IndexExpr`s over
string keys, the base re-resolves to a `Kind.UpValue`, and both keys fit `MaxIndexRK`. Both keys
are interned **before** anything is emitted, so the pool order matches the unfused path's.
Tests: `tests/Lua.Tests/CompilerEquivalence/GetImportTests.cs`.

<a id="worked-example-duptable"></a>
### Worked example: DupTable

`DupTable A` + `ExtraArg (Ax = index into the prototype's template pool)` copies a prebuilt
all-constant `LuaTable`. `B`/`C` are unused — the template already carries the capacity that
`NewTable`'s hints were for. The template is built at compile time by replaying the field list
with the *same primitives the runtime path uses* (`EnsureArrayCapacity` exactly as `SetList`
grows, the `LuaTable` indexer for hash fields), so the template's final state and capacities are
what the unfused path would have produced. The pool is a separate `LuaTable[]` on `Prototype` —
**not** in `Constants`, so no `LoadK`/`LoadKX`/RK operand can ever name a template; only
`DupTable`'s `ExtraArg` can, and its handler only ever copies *out*. That structural
unreachability is why one shared instance across every execution needs no freeze or guard.

The fold predicate accepts five node types plus unary minus over a number. Two deliberate
limits: a hash field's key must fold to a *string* (an integer key would interact with the array
part through `GrowArray`'s migration), and a table-valued field never folds (`{ x = {} }` would
put one shared table in the template and every clone would alias it). Tests:
`tests/Lua.Tests/CompilerEquivalence/DupTableTests.cs`.
`BuildTemplate` re-runs both folds under `Debug.Assert`, so a future divergence between "the
predicate approved this" and "the template can build it" is a debug-build failure, not a wrong
table.

## The two optimizations the plan still wants room for

### Constant propagation

The compiler already does a narrow, well-tested version of this: `const x = <literal>` is
inlined at its uses. The mechanism is `Function.TryGetConstLiteral` (`Function.cs:360`) driven by
the const-binding records the resolution pass keeps, and it is consulted inside
`SingleVariableHelper` — i.e. it is part of *name resolution*, and it inlines to a `Kind.Number`
/`Kind.String`-style descriptor rather than to an AST node.

Generalizing it means extending `TryFoldToConstant`'s reach from literal nodes to names that
resolve to constants. **The hazard is resolution, not folding.** The only authoritative way to
resolve a name is `SingleVariableHelper`, which:

- can create `_ENV` upvalues for enclosing functions as a side effect, and
- can *raise* Luau's "local undefined because a `continue` jumped over it in a repeat condition"
  diagnostic (`Function.cs:2143`).

So a speculative "would this name fold?" check has to either tolerate being re-run identically
later (idempotent for upvalue creation, and a throw is a throw), or be restructured so it is
asked only after the resolution that was going to happen anyway. This is exactly why Milestone 4
shipped literals only and deferred const-named fields to M4b rather than guessing.

The measured upper bound: a pure-AST count of constructors that would fuse today but for a
bare-name field is **37 tables / 107 name-valued fields** across the real 35-file corpus, against
the 131 fusions DupTable already gets. Material enough to be worth a follow-up increment if the
resolution can be made safe — and it is an upper bound, because a bare name may be a local or a
global and fold to nothing.

### Dead-branch elimination on statically-known conditions

The AST is shaped for this. `IfStat` is the whole `if/elseif*/else?` chain **flattened into one
node** (`Stat.cs:99`) with an `IfBranch` list, so dropping a branch is a list edit rather than a
restructuring; a `while` with a false condition is `WhileStat.Condition` being a constant.

Three traps:

1. **Removing a branch removes a block, and blocks declare locals** — see
   [the contract above](#the-one-contract-that-matters-most). Removing a branch whose body
   declares nothing is invisible to name resolution; removing one that declares a local is fine
   *only* because the block's scope is entirely inside the branch. Removing a branch whose body
   *is* a declaration is not something this pass gets to do.
2. **The `if cond then goto/break end` fast path is a codegen-level jump fusion**, not an AST
   shape (`TestThenBlock`, `Parser.cs:1207`). A branch removed after that decision is made can
   leave an escape jump with nothing to skip; check `EmitIfStatement`'s jump chain rather than
   assuming it collapses.
3. **Line info.** Every node carries a `Line`, and the goldens compare line stamps. A
   condition you delete was the source of some stamps; the ones you keep must still be stamped
   from real source positions.

`AndOrExpr` is likewise kept distinct from `BinaryExpr` precisely so that short-circuit
shortening (`true and f()`) is a codegen concern with jump lists, not a fold.

## Adding an opcode: the checklist

Both existing optimizations needed this, and one of the steps is a whole-suite failure if you
miss it. From `AGENTS.md`, plus what Milestones 3 and 4 learned:

1. Append to `OpCode.cs` **after `ExtraArg`** — new opcodes go at the end to preserve dumped-chunk
   compatibility.
2. `Instruction.opNames` **and** `Instruction.opModes`.
3. **`LuaDebug.OpModes`** — a *second, duplicate* table in `src/Lua/Internal/LuaDebug.cs`.
   omitting your opcode there makes `LuaDebug.TestAMode` throw `IndexOutOfRangeException` for any
   chunk containing it, which failed `constructs.lua`, `db.lua`, `literals.lua` and `files.lua`
   in Milestone 3.
4. A VM dispatcher arm and handler in `LuaVirtualMachine.cs`.
5. `LuaVirtualMachine.Markers` — a marker method **and** a call in `InitializeMarkers()`
   (`src/Lua/Runtime/LuaVirtualMachine.Markers.cs`). `[Conditional("CASE_MARKER")]` compiles them
   out entirely; they exist so ClrMd (the bench's JIT decompiler) can identify each handler.
6. `LuaDebug.GetName`/`GetFuncName` if the opcode can appear where `debug.getinfo` inspects it,
   and `Metamethods.GetName`/`GetNameAndDescription` if it enters metamethod dispatch.
7. `Dump.cs` if it can appear in a serialized chunk.

## Testing an optimization here

The pattern Milestones 3 and 4 established:

- **Shape tests** — exact instruction assertions for the fuse case (`Does.Contain("GETIMPORT 0")`),
  and a *must-not-fuse* test per bail-out condition. Use `BytecodeDump.Dump(..., includeLines: false)`
  for structural comparisons.
- **The strongest assertion is on the stream, not the source.** `GetImportTests`'
  `Deopt_RewritesTheInstructionStreamInPlace` asserts directly on the executed closure's
  `Prototype.Code` that the `GetImport` is gone and the real pair is there.
- **Mutation-test the test.** Break the mechanism deliberately and confirm the test fails with
  the message you expect; a test that passes for the wrong reason is worse than no test. This is
  how the DupTable round-trip test was proven to catch the codec bugs.
- **Review the golden diff, then regenerate.** `CorpusGoldenTests` dumps every real corpus file;
  deleting a `.dump` makes the test write it fresh and report Inconclusive. A fused instruction
  cascades (removing instructions shifts every later pc index), so canonicalize before diffing:
  blank the `[line N]` stamps, the leading `N:` pc, and any remaining numeric operands, then diff
  the *multiset of canonicalized lines*. What you want to see is the intended substitution and
  **nothing else of any other mnemonic** — for DupTable that was exactly 131 `DUPTABLE` + 131
  `EXTRAARG` added and 131 `NEWTABLE` + 644 `SETTABLE` + 9 `LOADK` + 3 `SETLIST` removed.

**What the goldens do and do not prove.** They were captured in Milestone 0 from the original
single-pass compiler and have tracked the new pipeline since the Milestone 2b cutover, so they are
a *drift detector*, not an equivalence proof. The independent proof that the new compiler emits
what the old one did is the Milestone 2 `CodeGenEquivalenceTests` run, taken while both compilers
were still reachable — and note that this file provides no such evidence now, because
`LuaState.Load` goes through `Parser.Parse`, so both sides of its comparison are the new compiler.

## File map

| Path | Role |
|---|---|
| `src/Lua/CodeAnalysis/Compilation/Parser.cs` | Production entry point (3 lines); the retired single-pass compiler (reference implementation) |
| `src/Lua/CodeAnalysis/Compilation/Function.cs` | Register allocation, constants, jump/goto/label patching, upvalue closure, const bindings — shared by both passes, unchanged |
| `src/Lua/CodeAnalysis/Compilation/Ast/AstParser.cs` | Tokens → AST, plus all resolution |
| `src/Lua/CodeAnalysis/Compilation/Ast/Expr.cs`, `Stat.cs` | Node types |
| `src/Lua/CodeAnalysis/Compilation/Ast/CodeGenerator.cs` | AST → bytecode; both optimizations live here |
| `src/Lua/CodeAnalysis/Compilation/Dump.cs` | Bytecode serialize/deserialize, incl. the template pool (format 1) |
| `src/Lua/Runtime/LuaVirtualMachine.cs` | Dispatch loop and handlers |
| `tests/Lua.Tests/CompilerEquivalence/` | `BytecodeDump`, `CorpusGoldenTests` + `Golden/`, `CodeGenEquivalenceTests`, and per-optimization test files |
