using BenchmarkDotNet.Attributes;
using Lua;

/// <summary>
/// Micro-benchmarks for <see cref="LuaTable"/>'s hash part. The hash part is split by
/// key kind, so the cases are split too:
/// <list type="bullet">
/// <item>string keys land in the embedded <c>LuaStringDictionary</c> (the common,
/// record-shaped case);</item>
/// <item>sparse integer keys land in the lazily created generic (LuaValue-keyed)
/// dictionary;</item>
/// <item>dense integer keys land in the array part and only touch the hash part when
/// the array grows and promotes them out of it.</item>
/// </list>
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class LuaTableBenchmark
{
    const int N = 4096;

    // i * 8192 + 7 is well beyond LuaTable's MaxDistance, so every sparse key goes to
    // the generic hash part instead of the array part.
    const int SparseStride = 8192;

    static readonly string[] StringKeys = new string[N];
    static readonly string[] StringKeyMisses = new string[N];
    static readonly LuaValue[] SparseKeys = new LuaValue[N];
    static readonly LuaValue[] SparseKeyMisses = new LuaValue[N];

    static LuaTableBenchmark()
    {
        for (var i = 0; i < N; i++)
        {
            StringKeys[i] = "key_" + i;
            StringKeyMisses[i] = "miss_" + i;
            SparseKeys[i] = (double)((i * SparseStride) + 7);
            SparseKeyMisses[i] = (double)((i * SparseStride) + 3);
        }
    }

    LuaTable stringTable = null!;
    LuaTable sparseTable = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        stringTable = new LuaTable(0, N);
        for (var i = 0; i < N; i++)
        {
            stringTable[StringKeys[i]] = i;
        }

        sparseTable = new LuaTable(0, 0);
        for (var i = 0; i < N; i++)
        {
            sparseTable[SparseKeys[i]] = i;
        }
    }

    [Benchmark(Description = "insert 4096 string keys (sized)")]
    public int Insert_StringKeys()
    {
        var table = new LuaTable(0, N);
        for (var i = 0; i < N; i++)
        {
            table[StringKeys[i]] = i;
        }

        return table.HashMapCount;
    }

    [Benchmark(Description = "insert 4096 string keys (unsized)")]
    public int Insert_StringKeys_Unsized()
    {
        var table = new LuaTable();
        for (var i = 0; i < N; i++)
        {
            table[StringKeys[i]] = i;
        }

        return table.HashMapCount;
    }

    [Benchmark(Description = "insert 4096 sparse int keys")]
    public int Insert_SparseIntKeys()
    {
        var table = new LuaTable(0, 0);
        for (var i = 0; i < N; i++)
        {
            table[SparseKeys[i]] = i;
        }

        return table.HashMapCount;
    }

    [Benchmark(Description = "insert 4096 dense int keys (array part)")]
    public int Insert_DenseIntKeys()
    {
        var table = new LuaTable(0, 0);
        for (var i = 0; i < N; i++)
        {
            table[i + 1] = i;
        }

        return table.ArrayLength;
    }

    [Benchmark(Description = "update 4096 existing string keys")]
    public int Update_StringKeys()
    {
        for (var i = 0; i < N; i++)
        {
            stringTable[StringKeys[i]] = i;
        }

        return stringTable.HashMapCount;
    }

    [Benchmark(Description = "get hit x4096 string keys")]
    public int Get_StringKeys()
    {
        var hits = 0;
        for (var i = 0; i < N; i++)
        {
            if (stringTable.TryGetValue(StringKeys[i], out _))
            {
                hits++;
            }
        }

        return hits;
    }

    [Benchmark(Description = "get miss x4096 string keys")]
    public int GetMissing_StringKeys()
    {
        var misses = 0;
        for (var i = 0; i < N; i++)
        {
            if (!stringTable.TryGetValue(StringKeyMisses[i], out _))
            {
                misses++;
            }
        }

        return misses;
    }

    [Benchmark(Description = "get hit x4096 sparse int keys")]
    public int Get_SparseIntKeys()
    {
        var hits = 0;
        for (var i = 0; i < N; i++)
        {
            if (sparseTable.TryGetValue(SparseKeys[i], out _))
            {
                hits++;
            }
        }

        return hits;
    }

    [Benchmark(Description = "get miss x4096 sparse int keys")]
    public int GetMissing_SparseIntKeys()
    {
        var misses = 0;
        for (var i = 0; i < N; i++)
        {
            if (!sparseTable.TryGetValue(SparseKeyMisses[i], out _))
            {
                misses++;
            }
        }

        return misses;
    }

    [Benchmark(Description = "pairs-iterate 4096 string keys")]
    public int Iterate_StringKeys()
    {
        var n = 0;
        foreach (var _ in stringTable)
        {
            n++;
        }

        return n;
    }

    [Benchmark(Description = "pairs-iterate 4096 sparse int keys")]
    public int Iterate_SparseIntKeys()
    {
        var n = 0;
        foreach (var _ in sparseTable)
        {
            n++;
        }

        return n;
    }

    [Benchmark(Description = "clear + refill 4096 string keys")]
    public int Clear_StringKeys()
    {
        stringTable.Clear();
        for (var i = 0; i < N; i++)
        {
            stringTable[StringKeys[i]] = i;
        }

        return stringTable.HashMapCount;
    }

    [Benchmark(Description = "promote sparse int keys into array part")]
    public int Promote_SparseToArray()
    {
        var table = new LuaTable(0, 0);
        table[32] = 10; // hash part
        for (var i = 1; i <= 31; i++)
        {
            table[i] = 10; // grows the array and promotes key 32 out of the hash part
        }

        return table.HashMapCount;
    }
}
