This file provides guidance to AI agent when working with code in this repository.

## Project Overview

Lua-CSharp is a high-performance Lua interpreter implemented in C# for .NET and Unity. It provides a Lua 5.2 interpreter with async/await integration, Source Generator support for easy C#-Lua interop, and Unity support.

## Common Development Commands

### Building

```bash
# Build entire solution
dotnet build

# Build in Release mode
dotnet build -c Release

# Build specific project
dotnet build src/Lua/Lua.csproj
```

### Running

Write lua to `sandbox/ConsoleApp1/test.lua`, then:

```bash
# Run simple tests
dotnet run --project sandbox/ConsoleApp1/ConsoleApp1.csproj

# For pattern matching testing, you can run specific Lua scripts like:
# echo 'print(string.gsub("hello", "()l", function(pos) return "[" .. pos .. "]" end))' > sandbox/ConsoleApp1/test.lua
# echo 'print(string.gsub("abc", "", "."))' > sandbox/ConsoleApp1/test.lua  
# echo 'print(string.gsub("(hello) and (world)", "%b()", function(s) return s:upper() end))' > sandbox/ConsoleApp1/test.lua
```


### Testing

```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run specific test project
dotnet test tests/Lua.Tests/Lua.Tests.csproj
```

### Benchmarking

```bash
# Run performance benchmarks
dotnet run -c Release --project sandbox/Benchmark/Benchmark.csproj
```

### Packaging

```bash
# Create NuGet package
dotnet pack -c Release
```

## Architecture Overview

### Core Components

1. **Lua Runtime (`src/Lua/`)**
   - `LuaState.cs`: Main entry point for Lua execution
   - `LuaValue.cs`: Represents values in Lua (nil, boolean, number, string, table, function, userdata, thread)
   - `Runtime/LuaVirtualMachine.cs`: Core VM implementation that executes Lua bytecode
   - `Runtime/OpCode.cs`, `Runtime/Instruction.cs`: VM instruction definitions
   - `CodeAnalysis/`: Lexer, parser, and compiler that converts Lua source to bytecode

2. **Source Generator (`src/Lua.SourceGenerator/`)**
   - Generates code for classes marked with `[LuaObject]` attribute
   - Enables seamless C#-Lua interop by auto-generating wrapper code
   - Key file: `LuaObjectGenerator.cs`

3. **Standard Libraries (`src/Lua/Standard/`)**
   - Implementations of Lua standard libraries (math, string, table, io, etc.)
   - Entry point: `OpenLibsExtensions.cs`

### Key Design Patterns

1. **Async/Await Integration**
   - All Lua execution methods are async (`DoStringAsync`, `DoFileAsync`)
   - LuaFunction can wrap async C# methods
   - Enables non-blocking execution of Lua scripts

2. **Value Representation**
   - `LuaValue` is a discriminated union struct
   - Implicit conversions between C# and Lua types
   - Zero-allocation for primitive types

3**Memory Management**
   - Heavy use of object pooling (`Pool.cs`, `PooledArray.cs`, `PooledList.cs`)
   - Stack-based value types where possible
   - Careful management of closures and upvalues

### Unity Integration (`src/Lua.Unity/`)

- Custom asset importer for `.lua` files
- Integration with Unity's Resources and Addressables systems
- Works with both Mono and IL2CPP

### Testing Structure

- Unit tests in `tests/Lua.Tests/`
- Lua test suite from official Lua 5.2 in `tests/Lua.Tests/tests-lua/`
- Benchmarks comparing with MoonSharp and NLua in `sandbox/Benchmark/`

## Luau Syntax Support

This fork implements Lua 5.2 plus a Lua 5.3 integer type plus Luau syntax. Where each Luau feature lives:

| Feature | Implementation |
|---|---|
| Compound assignment (`+= -= *= /= //= %= ^= ..=`) | `Parser.CompoundAssignment` + `Function.Infix`/`Postfix`; the target is evaluated once (register-holding helper) |
| `continue` | Contextual keyword; `Function.ContinueLabel` resolves it through the pending-goto machinery, with the `continue` label exempt from the "jumps into the scope of local" check |
| Floor division (`//`, `//=`, `__idiv`) | `OpCode.IDiv`; int//int stays Integer, `x // 0` yields inf/NaN (matching Luau, unlike Lua 5.3's error) |
| Generalized iteration (`for k, v in t do`) | A single non-call expression compiles to a `LuaBuiltin.IterResolver` call (`OpCode.LoadBuiltin`); tables and `__iter` are supported, bare functions keep the classic triple |
| `\u{...}` escapes, `0x`/`0b` literals, `_` digit separators | `Scanner.ReadUnicodeEscape` / `Scanner.ReadNumber` / `Parser.TryParseIntegerLiteral` |
| `const` | Read-only binding (compile error on reassignment, plain or compound, including through an upvalue); literal initializers are inlined by `Function.TryGetConstLiteral` |
| If-then-else expressions | `Parser.IfElseExpression` (statement `if` is unaffected) |
| String interpolation (`` `a{x}b` ``) | `Scanner.ReadInterpSection` (with the brace stack) + `Parser.InterpolatedString`, lowered to one `LuaBuiltin.InterpolationBuilder` call |
| Attributes (`@native`, `@[deprecated {use = "..."}]`) | Parsed and ignored (`Parser.SkipAttributes`) |
| Type annotations, aliases, casts, generics | Parsed and ignored (`SkipTypeAnnotation`, `TypeStatement`, `SkipTypeInstantiation`) |

### Luau gotchas

| Gotcha | Rule |
|---|---|
| Contextual keywords | `continue`, `const`, `type`, `export` and `typeof` (and `type function`) are not reserved: they stay valid identifiers, and are recognised by name + lookahead. Never add them to `Scanner.Tokens`. |
| New opcode | Append it after `ExtraArg` (existing numeric values, dumps and markers must not shift) and add entries to `Instruction.opNames`, `Instruction.opModes`, `LuaVirtualMachine.Markers` and the VM switch. |
| Interpolated strings | Single-line only. A bare `}` is literal text; only `{` opens a hole; `{{` is an error; `\{`, `\}` and `` \` `` are escapes; `\u{...}` must not open a hole. |
| Interpolation is not `string.format` | `%` in a literal is literal text, and values convert with `tostring` rules (incl. `__tostring`). Luau instead lowers to `("fmt"):format(...)` with `%*`; the observable result is the same. |
| `for x in f do` | A bare function value keeps the classic `f(state, control)` protocol; only non-call, non-vararg expressions get `__iter`/`next` treatment. |
| `const` inlining | Only a single literal initializer (number/string/boolean/nil) is inlined into use sites; other initializers keep the local (so `const t = {}` still has table identity). |

### Deliberate divergences from Luau

- Strings are UTF-16, so `#"\u{1F600}"` is 2 (not Luau's 4 UTF-8 bytes).
- Unknown escapes (`"\q"`) and `"\{"` inside *ordinary* strings are errors (stock Lua behaviour, relied on by `tests-lua`); Luau accepts them.
- `//` keeps an Integer result for int//int (Lua 5.3 integer semantics) instead of Luau's single number type.
- Interpolation does not go through an overridable `string.format`.
- Some error message texts differ (e.g. the missing-`else` message for if-expressions).

## Important Notes

- The project targets .NET Standard 2.1, .NET 6.0, and .NET 8.0
- Uses C# 13 language features
- Heavy use of unsafe code for performance
- Strings are UTF-16 (differs from standard Lua)

## TODO

- **ILuaStream Interface Changes**: The ILuaStream interface has been updated with new methods:
  - Added `IsOpen` property to track stream state
  - Added `ReadNumberAsync()` for reading numeric values (supports formats like "6.0", "-3.23", "15e12", hex numbers)
  - Changed `ReadLineAsync()` to accept a `keepEol` parameter for controlling line ending behavior
  - Renamed `ReadStringAsync()` to `ReadAsync()`
  - Added `CloseAsync()` method for async stream closing
  - ✅ Implemented `ReadNumberAsync()` in all implementations
  - Need to properly implement the `keepEol` parameter in `ReadLineAsync()` for TextLuaStream