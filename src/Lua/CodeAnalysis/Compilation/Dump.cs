using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Lua.Internal;
using Lua.Runtime;

namespace Lua.CodeAnalysis.Compilation;

[SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct Header
{
    public static ReadOnlySpan<byte> LuaSignature => "\eLua"u8;
    public static ReadOnlySpan<byte> LuaTail => [0x19, 0x93, 0x0d, 0x0a, 0x1a, 0x0a];

    public fixed byte Signature[4];
    public byte Version,
        Format,
        Endianness,
        IntSize;
    public byte PointerSize,
        InstructionSize;
    public byte NumberSize,
        IntegralNumber;
    public fixed byte Tail[6];

    public const int Size = 18;

    public Header(bool isLittleEndian)
    {
        fixed (byte* signature = Signature)
        {
            LuaSignature.CopyTo(new(signature, 4));
        }

        Version = (Constants.VersionMajor << 4) | Constants.VersionMinor;
        // Format 1 = Milestone 4's DupTable template pool: a "number of templates" section follows
        // each function's upvalues. Format 0 dumps (no section) still load as long as they contain
        // no DupTable, and an older reader rejects a format 1 chunk loudly at Header.Validate
        // instead of misparsing the bytes after it.
        Format = 1;
        Endianness = (byte)(isLittleEndian ? 1 : 0);
        IntSize = 4;
        PointerSize = (byte)sizeof(IntPtr);
        InstructionSize = 4;
        NumberSize = 8;
        IntegralNumber = 0;
        fixed (byte* tail = Tail)
        {
            LuaTail.CopyTo(new(tail, 6));
        }
    }

    public void Validate(ReadOnlySpan<char> name)
    {
        fixed (byte* signature = Signature)
        {
            if (!LuaSignature.SequenceEqual(new(signature, 4)))
            {
                throw new LuaUndumpException($"{name.ToString()}: is not a precompiled chunk");
            }
        }

        var major = Version >> 4;
        var minor = Version & 0xF;
        if (major != Constants.VersionMajor || minor != Constants.VersionMinor)
        {
            throw new LuaUndumpException(
                $"{name.ToString()}: version mismatch in precompiled chunk {major}.{minor} != {Constants.VersionMajor}.{Constants.VersionMinor}"
            );
        }

        if (
            IntSize != 4
            || Format is not (0 or 1)
            || IntegralNumber != 0
            || PointerSize is not (4 or 8)
            || InstructionSize != 4
            || NumberSize != 8
        )
        {
            goto ErrIncompatible;
        }

        fixed (byte* tail = Tail)
        {
            if (!LuaTail.SequenceEqual(new(tail, 6)))
            {
                goto ErrIncompatible;
            }
        }

        return;
        ErrIncompatible:
        throw new LuaUndumpException($"{name.ToString()}: incompatible precompiled chunk");
    }
}

unsafe ref struct DumpState(IBufferWriter<byte> writer, bool reversedEndian)
{
    public readonly IBufferWriter<byte> Writer = writer;
    Span<byte> unWritten;

    void Write(ReadOnlySpan<byte> span)
    {
        var toWrite = span;
        var remaining = unWritten.Length;
        if (span.Length > remaining)
        {
            span[..remaining].CopyTo(unWritten);
            Writer.Advance(remaining);
            toWrite = span[remaining..];
            unWritten = Writer.GetSpan(toWrite.Length);
        }

        toWrite.CopyTo(unWritten);
        Writer.Advance(toWrite.Length);
        unWritten = unWritten[toWrite.Length..];
    }

    public bool IsReversedEndian => reversedEndian;

    void DumpHeader()
    {
        Header header = new(BitConverter.IsLittleEndian ^ IsReversedEndian);
        Write(new(&header, Header.Size));
    }

    public void Dump(Prototype prototype)
    {
        if (unWritten.Length == 0)
        {
            unWritten = Writer.GetSpan(Header.Size + 32);
        }

        DumpHeader();
        DumpFunction(prototype);
    }

    void DumpFunction(Prototype prototype)
    {
        WriteInt(prototype.LineDefined); // 4
        WriteInt(prototype.LastLineDefined); // 4
        WriteByte((byte)prototype.ParameterCount); // 1
        WriteByte((byte)prototype.MaxStackSize); // 1
        WriteByte((byte)(prototype.HasVariableArguments ? 1 : 0)); // 1
        WriteIntSpanWithLength(MemoryMarshal.Cast<Instruction, int>(prototype.Code)); // 4
        WriteConstants(prototype.Constants); // 4
        WritePrototypes(prototype.ChildPrototypes); // 4
        WriteUpValues(prototype.UpValues); // 4
        WriteTemplates(prototype.Templates); // 4, format 1 only

        // Debug
        WriteString(prototype.ChunkName);
        WriteIntSpanWithLength(prototype.LineInfo);
        WriteLocalVariables(prototype.LocalVariables);
        WriteInt(prototype.UpValues.Length);
        foreach (var desc in prototype.UpValues)
        {
            WriteString(desc.Name);
        }
    }

    void WriteInt(int v)
    {
        if (reversedEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        Write(new(&v, sizeof(int)));
    }

    void WriteLong(long v)
    {
        if (reversedEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        Write(new(&v, sizeof(long)));
    }

    void WriteByte(byte v)
    {
        Write(new(&v, sizeof(byte)));
    }

    void WriteDouble(double v)
    {
        var l = BitConverter.DoubleToInt64Bits(v);
        WriteLong(l);
    }

    void WriteIntSpanWithLength(ReadOnlySpan<int> v)
    {
        WriteInt(v.Length);
        if (IsReversedEndian)
        {
            foreach (var i in v)
            {
                var reversed = BinaryPrimitives.ReverseEndianness(i);
                Write(new(&reversed, 4));
            }
        }
        else
        {
            Write(MemoryMarshal.Cast<int, byte>(v));
        }
    }

    void WriteBool(bool v)
    {
        WriteByte(v ? (byte)1 : (byte)0);
    }

    void WriteString(string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        var len = bytes.Length;
        if (bytes.Length != 0)
        {
            len++;
        }

        if (sizeof(IntPtr) == 8)
        {
            WriteLong(len);
        }
        else
        {
            WriteInt(len);
        }

        if (len != 0)
        {
            Write(bytes);
            WriteByte(0);
        }
    }

    void WriteConstants(ReadOnlySpan<LuaValue> constants)
    {
        WriteInt(constants.Length);
        foreach (var c in constants)
        {
            WriteConstant(c);
        }
    }

    void WriteConstant(LuaValue c)
    {
        WriteByte((byte)c.Type);
        switch (c.Type)
        {
            case LuaValueType.Nil:
                break;
            case LuaValueType.Boolean:
                WriteBool(c.ReadAsBool());
                break;
            case LuaValueType.Number:
                // ReadAsDouble, not ReadAsInt64: the latter is the value's raw bits as an integer,
                // and passing them to WriteDouble(0.0) as a *number* converts them instead of
                // reinterpreting, which corrupts every constant that is not a small integer-valued
                // double.
                WriteDouble(c.ReadAsDouble());
                break;
            case LuaValueType.Integer:
                // Missing until Milestone 4, which made it a real round-trip bug rather than a
                // latent one: an unmapped type wrote its byte and nothing else, so an integer
                // constant came back as nil (`{ gap = 4 }` is exactly that case).
                WriteLong(c.ReadAsInt64());
                break;
            case LuaValueType.String:
                WriteString(c.UnsafeRead<string>());
                break;
        }
    }

    /// <summary>
    /// Compiler-rewrite plan Milestone 4: the all-constant table templates <see cref="OpCode.DupTable"/>
    /// copies from, as (array length, that many values, hash-entry count, key/value pairs each). The
    /// array part is written whole, nils included, and the hash part is written in insertion order,
    /// so the reader can rebuild the template exactly -- capacities included -- rather than merely an
    /// equal table. Values and keys are primitives by construction (the fold predicate admits
    /// nothing else), and <see cref="LuaTable.CopyHashEntriesTo"/> is the only part that is not
    /// already a flat span.
    /// <para>
    /// "Exactly" has to include nil-valued hash entries, which are written here like any other: a
    /// field the source literally set to nil is a dead entry in the template (it is in the hash part,
    /// and <c>pairs</c> skips it, but <c>next(t, thatKey)</c> still reports what follows it). See
    /// <see cref="LuaTable.CopyHashEntriesTo"/> -- it is the whole reason that method exists alongside
    /// the enumerator -- and <c>ReadTemplates</c>, which recreates the entry by assigning nil.
    /// </para>
    /// </summary>
    void WriteTemplates(ReadOnlySpan<LuaTable> templates)
    {
        WriteInt(templates.Length);
        foreach (var template in templates)
        {
            var array = template.GetArraySpan();
            WriteInt(array.Length);
            foreach (var v in array)
            {
                WriteConstant(v);
            }

            var hashEntries = new List<KeyValuePair<LuaValue, LuaValue>>();
            template.CopyHashEntriesTo(hashEntries);
            WriteInt(hashEntries.Count);
            foreach (var entry in hashEntries)
            {
                WriteConstant(entry.Key);
                WriteConstant(entry.Value);
            }
        }
    }

    void WritePrototypes(ReadOnlySpan<Prototype> prototypes)
    {
        WriteInt(prototypes.Length);
        foreach (var p in prototypes)
        {
            DumpFunction(p);
        }
    }

    void WriteLocalVariables(ReadOnlySpan<LocalVariable> localVariables)
    {
        WriteInt(localVariables.Length);
        foreach (var v in localVariables)
        {
            WriteString(v.Name);
            WriteInt(v.StartPc);
            WriteInt(v.EndPc);
        }
    }

    void WriteUpValues(ReadOnlySpan<UpValueDesc> upValues)
    {
        WriteInt(upValues.Length);
        foreach (var u in upValues)
        {
            // Bit 0 is luac's `instack` flag; bit 1 marks a by-value capture (an extension:
            // standard dumps never set it, so they load as by-reference).
            WriteByte((byte)((u.IsLocal ? 1 : 0) | (u.ByValue ? 2 : 0)));
            WriteByte((byte)u.Index);
        }
    }
}

unsafe ref struct UndumpState(
    ReadOnlySpan<byte> span,
    ReadOnlySpan<char> name,
    StringInternPool internPool
)
{
    public ReadOnlySpan<byte> Unread = span;
    bool otherEndian;
    int pointerSize;
    byte format;
    readonly ReadOnlySpan<char> name = name;

    void Throw(string why)
    {
        throw new LuaUndumpException($"{name.ToString()}: {why} precompiled chunk");
    }

    void ThrowTooShort()
    {
        Throw("truncate");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Read(Span<byte> dst)
    {
        if (Unread.Length < dst.Length)
        {
            ThrowTooShort();
        }

        Unread[..dst.Length].CopyTo(dst);

        Unread = Unread[dst.Length..];
    }

    byte ReadByte()
    {
        if (0 < Unread.Length)
        {
            var b = Unread[0];
            Unread = Unread[1..];
            return b;
        }

        ThrowTooShort();
        return 0;
    }

    bool ReadBool()
    {
        if (0 < Unread.Length)
        {
            var b = Unread[0];
            Unread = Unread[1..];
            return b != 0;
        }

        ThrowTooShort();

        return false;
    }

    int ReadInt()
    {
        var i = 0;
        Span<byte> span = new(&i, sizeof(int));
        Read(span);

        if (otherEndian)
        {
            i = BinaryPrimitives.ReverseEndianness(i);
        }

        return i;
    }

    long ReadLong()
    {
        long i = 0;
        Span<byte> span = new(&i, sizeof(long));
        Read(span);

        if (otherEndian)
        {
            i = BinaryPrimitives.ReverseEndianness(i);
        }

        return i;
    }

    double ReadDouble()
    {
        var i = ReadLong();

        return *(double*)&i;
    }

    public Prototype Undump()
    {
        Header h = default;
        Span<byte> span = new(&h, sizeof(Header));
        Read(span);

        h.Validate(name);
        otherEndian = BitConverter.IsLittleEndian ^ (h.Endianness == 1);
        pointerSize = h.PointerSize;
        format = h.Format;
        return UndumpFunction();
    }

    Prototype UndumpFunction()
    {
        var lineDefined = ReadInt(); // 4
        var lastLineDefined = ReadInt(); // 4
        var parameterCount = ReadByte(); // 1
        var maxStackSize = ReadByte(); // 1
        var isVarArg = ReadByte() == 1; // 1
        var codeLength = ReadInt();
        var code = new Instruction[codeLength];
        ReadInToIntSpan(MemoryMarshal.Cast<Instruction, int>((Span<Instruction>)code));
        var constants = ReadConstants();
        var prototypes = ReadPrototypes();
        var upValues = ReadUpValues();
        // Format 0 (a chunk written before Milestone 4) has no section here; it also cannot contain a
        // DupTable, so an empty pool is the correct reading of it either way.
        LuaTable[] templates = format >= 1 ? ReadTemplates() : [];

        // Debug
        var source = ReadString();
        var lineInfoLength = ReadInt();
        var lineInfo = new int[lineInfoLength];
        ReadInToIntSpan(lineInfo.AsSpan());
        var localVariables = ReadLocalVariables();
        var upValueCount = ReadInt();
        Debug.Assert(
            upValueCount == upValues.Length,
            $"upvalue count mismatch: {upValueCount} != {upValues.Length}"
        );
        foreach (ref var desc in upValues.AsSpan())
        {
            var name = ReadString();
            desc.Name = name;
        }

        return new(
            null, // name — the bytecode format does not carry function names (source-only)
            source,
            lineDefined,
            lastLineDefined,
            parameterCount,
            maxStackSize,
            isVarArg,
            constants,
            code,
            prototypes,
            lineInfo,
            localVariables,
            upValues,
            templates
        );
    }

    void ReadInToIntSpan(Span<int> toWrite)
    {
        for (var i = 0; i < toWrite.Length; i++)
        {
            toWrite[i] = ReadInt();
        }
    }

    string ReadString()
    {
        var len = pointerSize == 4 ? ReadInt() : (int)ReadLong();
        if (len == 0)
        {
            return "";
        }

        len--;
        var arrayPooled = ArrayPool<byte>.Shared.Rent(len);
        char[]? charArrayPooled = null;
        try
        {
            var span = arrayPooled.AsSpan(0, len);
            Read(span);

            var l = ReadByte();
            Debug.Assert(l == 0);
            var chars =
                len <= 128
                    ? stackalloc char[len * 2]
                    : (charArrayPooled = ArrayPool<char>.Shared.Rent(len * 2));
            var count = Encoding.UTF8.GetChars(span, chars);
            return internPool.Intern(chars[..count]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(arrayPooled);
            if (charArrayPooled != null)
            {
                ArrayPool<char>.Shared.Return(charArrayPooled);
            }
        }
    }

    LuaValue[] ReadConstants()
    {
        var count = ReadInt();
        var constants = new LuaValue[count];
        for (var i = 0; i < count; i++)
        {
            constants[i] = ReadConstant();
        }

        return constants;
    }

    LuaValue ReadConstant()
    {
        var type = (LuaValueType)ReadByte();
        switch (type)
        {
            case LuaValueType.Nil:
                return LuaValue.Nil;
            case LuaValueType.Boolean:
                return new(ReadByte() == 1);
            case LuaValueType.Number:
                return new(ReadDouble());
            case LuaValueType.Integer:
                return new(ReadLong());
            case LuaValueType.String:
                return new(ReadString());
            default:
                return LuaValue.Nil;
        }
    }

    /// <summary>
    /// Reads back what <see cref="DumpState.WriteTemplates"/> wrote. The template is rebuilt at the
    /// template's own capacities -- <c>new LuaTable(arrayLength, hashCount)</c> then
    /// <c>EnsureArrayCapacity</c>, the same two steps the compiler's own template construction takes
    /// -- and the hash entries are inserted before the array part is filled in, which is safe
    /// because a template's keys are strings: a string key never routes to the array part, so the
    /// two halves cannot overwrite each other.
    /// </summary>
    LuaTable[] ReadTemplates()
    {
        var count = ReadInt();
        var templates = count != 0 ? new LuaTable[count] : [];
        for (var i = 0; i < count; i++)
        {
            var arrayLength = ReadInt();
            var array = new LuaValue[arrayLength];
            for (var v = 0; v < arrayLength; v++)
            {
                array[v] = ReadConstant();
            }

            var hashCount = ReadInt();
            var template = new LuaTable(arrayLength, hashCount);
            template.EnsureArrayCapacity(arrayLength);
            for (var h = 0; h < hashCount; h++)
            {
                var key = ReadConstant();
                var value = ReadConstant();
                template[key] = value;
            }

            array.CopyTo(template.GetArraySpan());
            templates[i] = template;
        }

        return templates;
    }

    Prototype[] ReadPrototypes()
    {
        var count = ReadInt();
        var prototypes = count != 0 ? new Prototype[count] : [];
        for (var i = 0; i < count; i++)
        {
            prototypes[i] = UndumpFunction();
        }

        return prototypes;
    }

    LocalVariable[] ReadLocalVariables()
    {
        var count = ReadInt();
        var localVariables = new LocalVariable[count];
        for (var i = 0; i < count; i++)
        {
            var name = ReadString();
            var startPc = ReadInt();
            var endPc = ReadInt();
            localVariables[i] = new()
            {
                Name = name,
                StartPc = startPc,
                EndPc = endPc,
            };
        }

        return localVariables;
    }

    UpValueDesc[] ReadUpValues()
    {
        var count = ReadInt();
        Debug.Assert(count < 100, $" too many upvalues :{count}");
        var upValues = new UpValueDesc[count];
        for (var i = 0; i < count; i++)
        {
            var flags = ReadByte();
            var index = ReadByte();
            upValues[i] = new()
            {
                IsLocal = (flags & 1) != 0,
                ByValue = (flags & 2) != 0,
                Index = index,
            };
        }

        return upValues;
    }
}
