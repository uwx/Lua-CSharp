using System.Buffers.Binary;
using Lua.CodeAnalysis.Compilation;
using Lua.CodeAnalysis.Compilation.Ast;
using Lua.Internal;
using Lua.Runtime;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Compiler-rewrite plan Milestone 4: the precompiled-chunk codec's first round-trip test. The
/// milestone added a per-prototype template section to <c>Dump.cs</c>, and in doing so had to fix
/// two pre-existing codec bugs that a fused literal containing <c>4</c> or <c>1.5</c> would
/// otherwise have turned into silent data corruption:
///
/// <list type="bullet">
/// <item><see cref="LuaValueType.Integer"/> had no case in either direction, so an integer constant
/// round-tripped to <b>nil</b> -- and this compiler emits <c>Integer</c> for every integer literal.
/// <c>{ gap = 4 }</c> is exactly that literal.</item>
/// <item>The writer's <c>Number</c> case passed the value's raw IEEE bits through a numeric
/// long-to-double <em>conversion</em> instead of reinterpreting them, corrupting every constant
/// that is not a small integer-valued double.</item>
/// </list>
///
/// Both bugs are invisible to every other test in this suite, because nothing else in it serializes
/// bytecode. Every assertion here is therefore about a value surviving a write-then-read, never
/// about a compiler output -- the shape tests live in <see cref="DupTableTests"/>.
/// </summary>
public class DumpRoundTripTests
{
    /// <summary>Compiles <paramref name="source"/> and round-trips the prototype through the codec.</summary>
    static (Prototype Direct, Prototype Restored) RoundTrip(string source, bool littleEndian = true)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var body = AstParser.ParseToAst(state, source, "chunk");
        var direct = CodeGenerator.Generate(state, body, "chunk");
        var restored = Prototype.FromBytecode(direct.ToBytecode(littleEndian), "chunk");
        return (direct, restored);
    }

    /// <summary>
    /// Runs a prototype as a chunk with no arguments. Handing it to Lua through <c>_ENV</c> keeps
    /// the call machinery out of the picture: whatever comes back came from the prototype itself.
    /// </summary>
    static async Task<LuaValue[]> RunAsync(Prototype prototype)
    {
        var state = LuaState.Create();
        state.OpenStandardLibraries();
        state.Environment["__chunk"] = new LuaClosure(state, prototype);
        return await state.DoStringAsync("return __chunk()");
    }

    /// <summary>
    /// A value's identity for comparison purposes: type and value, spelled out, so a failure reads
    /// as "expected 4, got nil" instead of a LuaValue.ToString() that cannot distinguish them.
    /// </summary>
    static string Describe(LuaValue value)
    {
        return value.Type switch
        {
            LuaValueType.Nil => "nil",
            LuaValueType.Boolean => value.Read<bool>() ? "true" : "false",
            LuaValueType.Integer => value.Read<long>().ToString(),
            LuaValueType.Number => value.Read<double>().ToString("R"),
            LuaValueType.String => $"\"{value.Read<string>()}\"",
            _ => value.Type.ToString(),
        };
    }

    /// <summary>
    /// Flattens a template into the only thing that is observable about it: the array part in order,
    /// then the hash entries in insertion order. The array part is written whole including its
    /// capacity, which is why a one-element array literal shows eight slots -- the same
    /// <c>GrowArray</c> rounding the runtime path gets, so it is part of what must survive.
    /// </summary>
    static string Flatten(LuaTable template)
    {
        var parts = new List<string>();
        foreach (var value in template.GetArraySpan())
        {
            parts.Add(Describe(value));
        }

        var entries = new List<KeyValuePair<LuaValue, LuaValue>>();
        template.CopyHashEntriesTo(entries);
        foreach (var entry in entries)
        {
            parts.Add($"{Describe(entry.Key)}={Describe(entry.Value)}");
        }

        return string.Join("|", parts);
    }

    static void AssertSameResults(LuaValue[] expected, LuaValue[] actual)
    {
        Assert.That(
            actual.Select(Describe),
            Is.EqualTo(expected.Select(Describe)),
            "the round-tripped chunk returned something else"
        );
    }

    // ------------------------------------------------------------------ constants

    /// <summary>
    /// The two codec bugs, as three constants: an integer (was: silently nil), a fraction (was:
    /// reinterpreting its bits as a huge denormal-ish double), and a value whose bits are nowhere
    /// near a small integer (<c>1e300</c>).
    /// </summary>
    [Test]
    public void Constants_KeepTheirTypesAndValues()
    {
        var (direct, restored) = RoundTrip(
            "local a, b, c, d, e = 4, -7, 1.5, 1e300, true\nreturn a, b, c, d, e"
        );

        Assert.That(restored.Constants.Length, Is.EqualTo(direct.Constants.Length));
        for (var i = 0; i < direct.Constants.Length; i++)
        {
            Assert.That(
                Describe(restored.Constants[i]),
                Is.EqualTo(Describe(direct.Constants[i])),
                $"constant {i}"
            );
        }

        // The integer case specifically: without the LuaValueType.Integer case in the reader this
        // is LuaValue.Nil, which is the failure mode the milestone had to fix.
        Assert.That(
            restored.Constants.ToArray().Select(Describe),
            Does.Contain("4"),
            "the integer constant did not survive"
        );
    }

    /// <summary>Constants and templates survive a big-endian dump too (the reversed-endian paths).</summary>
    [Test]
    public async Task BigEndianDump_RoundTrips()
    {
        var (direct, restored) = RoundTrip(
            "local t = { 1, 2, x = 3, y = 1.5 }\nreturn #t, t.x, t.y",
            littleEndian: false
        );

        Assert.That(restored.Templates.Length, Is.EqualTo(direct.Templates.Length));
        Assert.That(Flatten(restored.Templates[0]), Is.EqualTo(Flatten(direct.Templates[0])));
        AssertSameResults(await RunAsync(direct), await RunAsync(restored));
    }

    // ------------------------------------------------------------------ templates

    /// <summary>
    /// The template section itself, rebuilt field for field. The literal covers every constant type
    /// the predicate can fold (integer, fraction, string, boolean, nil) plus a two-element array
    /// part, so the one template exercises every branch of <c>WriteConstant</c> on the way in.
    /// </summary>
    [Test]
    public void Templates_AreRebuiltExactly()
    {
        var (direct, restored) = RoundTrip(
            "local t = { 1, 2, x = 3, y = 1.5, s = 'z', b = true, n = nil }\nreturn t"
        );

        Assert.That(direct.Templates.Length, Is.EqualTo(1), "the fixture must fuse");
        Assert.That(restored.Templates.Length, Is.EqualTo(1));
        Assert.That(Flatten(restored.Templates[0]), Is.EqualTo(Flatten(direct.Templates[0])));
    }

    /// <summary>
    /// A nil-valued field is a <em>dead entry</em>: present in the hash dictionary (so <c>next</c>
    /// handed that key still finds it and reports the pair after it), absent from <c>pairs</c>. The
    /// dump has to write it, which is why the walk behind <c>WriteTemplates</c> cannot be the
    /// <c>pairs</c> enumerator -- that one skips dead entries by design, so a restored template
    /// would simply not have the key, and <c>next(t, "z")</c> would report the end of iteration
    /// instead of <c>"y"</c>. Both halves of that are asserted here: the entry is in the template
    /// (<c>Does.Contain</c>, which the symmetric flattened comparison above cannot see, since a
    /// shared bug would drop it from both sides), and the behavior that depends on it matches.
    /// </summary>
    [Test]
    public async Task NilValuedTemplateField_SurvivesAsADeadEntry()
    {
        var (direct, restored) = RoundTrip(
            """
            local t = { z = nil, y = 1, x = 2 }
            local count = 0
            for _ in pairs(t) do count = count + 1 end
            local k, v = next(t, "z")
            local k2, v2 = next(t, "y")
            return count, t.z == nil, k, v, k2, v2, next(t, "x") == nil
            """
        );

        Assert.That(direct.Templates.Length, Is.EqualTo(1), "the fixture must fuse");
        Assert.That(
            Flatten(direct.Templates[0]),
            Does.Contain("\"z\"=nil"),
            "the template itself must carry the dead entry"
        );
        Assert.That(
            Flatten(restored.Templates[0]),
            Does.Contain("\"z\"=nil"),
            "the codec dropped the dead entry"
        );
        Assert.That(Flatten(restored.Templates[0]), Is.EqualTo(Flatten(direct.Templates[0])));

        // count is 2 on both sides either way -- skipping dead entries is what `pairs` does -- so
        // the iteration-from-a-dead-key results are the observable half of this test.
        AssertSameResults(await RunAsync(direct), await RunAsync(restored));
    }

    /// <summary>
    /// Templates live inside the per-function section, so a nested function's own pool has to
    /// round-trip recursively -- and the copy semantics have to survive that too.
    /// </summary>
    [Test]
    public async Task ChildPrototypeTemplates_RoundTrip()
    {
        var (direct, restored) = RoundTrip(
            """
            local function make() return { x = 1, y = 2 } end
            local a, b = make(), make()
            a.x = 99
            return a.x, b.x, make().x
            """
        );

        Assert.That(restored.ChildPrototypes.Length, Is.EqualTo(1));
        Assert.That(restored.ChildPrototypes[0].Templates.Length, Is.EqualTo(1));
        Assert.That(
            Flatten(restored.ChildPrototypes[0].Templates[0]),
            Is.EqualTo(Flatten(direct.ChildPrototypes[0].Templates[0]))
        );

        AssertSameResults(await RunAsync(direct), await RunAsync(restored));
    }

    // ------------------------------------------------------------------ whole chunks

    /// <summary>
    /// The end-to-end version: a chunk whose fused literal straddles every constant type runs
    /// identically before and after a round trip.
    /// </summary>
    [Test]
    public async Task RoundTrippedChunk_ExecutesIdentically()
    {
        var (direct, restored) = RoundTrip(
            """
            local t = { 1, 2, x = 3, y = 'z', f = 1.5, b = true, z = nil }
            local count = 0
            for _ in pairs(t) do count = count + 1 end
            local nested = { a = -1, b = -2.5 }
            return #t, t.x, t.y, t.f, t.b, t.z == nil, count, nested.a, nested.b
            """
        );

        AssertSameResults(await RunAsync(direct), await RunAsync(restored));
    }

    // ------------------------------------------------------------------ format versioning

    /// <summary>
    /// A format 0 chunk -- what a reader before Milestone 4 both wrote and read -- still loads, and
    /// reads as having no templates. The fixture is built by hand rather than by mutating a format 1
    /// dump, because format 0 has no template section at all: flipping the header byte on a real
    /// dump would leave those four bytes in the stream, which is a layout no pre-M4 writer produced.
    /// </summary>
    [Test]
    public void FormatZeroChunk_LoadsWithNoTemplates()
    {
        var prototype = Prototype.FromBytecode(EmptyChunk(format: 0), "@f0");

        Assert.That(prototype.Code.Length, Is.EqualTo(0));
        Assert.That(prototype.Constants.Length, Is.EqualTo(0));
        Assert.That(prototype.Templates.Length, Is.EqualTo(0));
    }

    /// <summary>Format 1 with an empty pool is the same stream plus a zero template count.</summary>
    [Test]
    public void FormatOneChunk_WithNoTemplates_Loads()
    {
        var prototype = Prototype.FromBytecode(EmptyChunk(format: 1), "@f1");
        Assert.That(prototype.Templates.Length, Is.EqualTo(0));
    }

    /// <summary>
    /// An unknown format is rejected at <c>Header.Validate</c>, before any of the body is read --
    /// which is the property that lets format 1 chunks be shipped to an older reader safely: it
    /// says so, rather than misparsing the bytes after the header.
    /// </summary>
    [Test]
    public void UnknownFormat_IsRejectedLoudly()
    {
        // The name is passed without a chunk-name sigil: Undump strips a leading '@'/'=' (they
        // mark "this is a file name" in a Lua chunk name), and this assertion is about the message.
        var exception = Assert.Throws<LuaUndumpException>(
            () => Prototype.FromBytecode(EmptyChunk(format: 2), "f2")
        );

        Assert.That(exception!.Message, Does.Contain("f2"));
        Assert.That(exception.Message, Does.Contain("incompatible"));
    }

    /// <summary>
    /// The bytes of a minimal empty chunk -- header, one function with no code, constants, children
    /// or upvalues, a chunk name, empty debug sections -- in the layout <c>Dump.cs</c> writes. The
    /// only thing that varies with <paramref name="format"/> is whether the four bytes of the
    /// template count are present, which is exactly what the format field means.
    /// </summary>
    static byte[] EmptyChunk(byte format)
    {
        var bytes = new List<byte>();
        bytes.AddRange("\eLua"u8);
        bytes.Add((byte)((Constants.VersionMajor << 4) | Constants.VersionMinor));
        bytes.Add(format);
        bytes.Add(1); // endianness: 1 = little endian
        bytes.Add(4); // int size
        bytes.Add((byte)IntPtr.Size); // pointer size
        bytes.Add(4); // instruction size
        bytes.Add(8); // number size
        bytes.Add(0); // integral number
        bytes.AddRange(new byte[] { 0x19, 0x93, 0x0d, 0x0a, 0x1a, 0x0a });

        void Int(int value)
        {
            Span<byte> span = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            bytes.AddRange(span);
        }

        Int(0); // line defined
        Int(0); // last line defined
        bytes.Add(0); // parameter count
        bytes.Add(2); // max stack size
        bytes.Add(0); // has variable arguments
        Int(0); // code length
        Int(0); // constants length
        Int(0); // child prototypes length
        Int(0); // upvalues length
        if (format >= 1)
        {
            Int(0); // templates length
        }

        // Chunk name: length including the terminator, whose width follows the pointer size in the
        // header (the reader picks Int or Long the same way), then the bytes and the terminator.
        var name = System.Text.Encoding.UTF8.GetBytes("@f" + format);
        if (IntPtr.Size == 8)
        {
            Span<byte> nameLength = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(nameLength, name.Length + 1);
            bytes.AddRange(nameLength);
        }
        else
        {
            Int(name.Length + 1);
        }

        bytes.AddRange(name);
        bytes.Add(0);

        Int(0); // line info length
        Int(0); // local variables length
        Int(0); // upvalue names length

        return bytes.ToArray();
    }
}
