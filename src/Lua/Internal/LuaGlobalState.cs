using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lua.Internal;
using Lua.Platforms;
using Lua.Runtime;
using Lua.Standard;

namespace Lua;

sealed class LuaGlobalState
{
    // states
    readonly LuaState mainState;
    FastStackCore<LuaState> stateStack;
    readonly LuaTable environment;
    readonly LuaTable registry = new();
    readonly UpValue envUpValue;

    FastStackCore<LuaDebug.LuaDebugBuffer> debugBufferPool;

    public LuaPlatform Platform { get; set; }
    public ILuaModuleLoader? ModuleLoader { get; set; }

    internal UpValue EnvUpValue => envUpValue;
    internal ref FastStackCore<LuaState> ThreadStack => ref stateStack;
    internal ref FastStackCore<LuaDebug.LuaDebugBuffer> DebugBufferPool => ref debugBufferPool;

    public LuaTable Environment => environment;
    public LuaTable Registry => registry;
    public LuaTable LoadedModules => registry[ModuleLibrary.LoadedKeyForRegistry].Read<LuaTable>();
    public LuaTable PreloadModules =>
        registry[ModuleLibrary.PreloadKeyForRegistry].Read<LuaTable>();
    public LuaState MainThread => mainState;

    // metatables
    LuaTable? nilMetatable;
    LuaTable? numberMetatable;
    LuaTable? stringMetatable;
    LuaTable? booleanMetatable;
    LuaTable? functionMetatable;
    LuaTable? stateMetatable;
    LuaTable? fixed64Metatable;
    LuaTable? fixed64Vector3Metatable;
    LuaTable? fixed64AngleMetatable;
    LuaTable? fixed64EulerMetatable;

    // Inline cache for metamethod resolution, keyed by (metatable identity, name).
    // Entries are invalidated via MetamethodCache.Epoch (bumped on setmetatable or any
    // "__"-prefixed table write). Metatables are stable in practice, so the hit rate is
    // very high (e.g. an enum's __eq is resolved once per metatable, not per comparison).
    const int MetamethodCacheSize = 128;
    readonly MetamethodCacheEntry[] metamethodCache = new MetamethodCacheEntry[MetamethodCacheSize];

    struct MetamethodCacheEntry
    {
        public LuaTable? Metatable;
        public string? Name;
        public LuaValue Value;
        public int Epoch;
        public bool Found;
    }

    public static LuaGlobalState Create(LuaPlatform? platform = null)
    {
        LuaGlobalState globalState = new(platform ?? LuaPlatform.Default);
        return globalState;
    }

    LuaGlobalState(LuaPlatform platform)
    {
        mainState = new(this);
        environment = new();
        envUpValue = UpValue.Closed(environment);
        registry[ModuleLibrary.LoadedKeyForRegistry] = new LuaTable(0, 8);
        registry[ModuleLibrary.PreloadKeyForRegistry] = new LuaTable(0, 8);
        Platform = platform;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetMetatable(LuaValue value, [NotNullWhen(true)] out LuaTable? result)
    {
        result = value.Type switch
        {
            LuaValueType.Nil => nilMetatable,
            LuaValueType.Boolean => booleanMetatable,
            LuaValueType.String => stringMetatable,
            LuaValueType.Number or LuaValueType.Integer => numberMetatable,
            LuaValueType.Function => functionMetatable,
            LuaValueType.Thread => stateMetatable,
            LuaValueType.Fixed64 => fixed64Metatable,
            LuaValueType.Fixed64Vector3 => fixed64Vector3Metatable,
            LuaValueType.Fixed64Angle => fixed64AngleMetatable,
            LuaValueType.Fixed64Euler => fixed64EulerMetatable,
            LuaValueType.UserData => value.UnsafeRead<ILuaUserData>().Metatable,
            // UnsafeRead here just gets you down to the object inside the UserDataObject, which is not what we want
            LuaValueType.UserData2 => Unsafe.As<object, UserDataObject>(ref Unsafe.AsRef(in value.referenceValue)!).Metatable,
            LuaValueType.Table => value.UnsafeRead<LuaTable>().Metatable,
            _ => null,
        };

        return result != null;
    }

    /// <summary>
    /// Resolves a metamethod for <paramref name="metatable"/>, caching the result so
    /// repeated lookups of the same (metatable, name) pair avoid a dictionary probe.
    /// The cache is direct-mapped and identity-verified; a collision is just a miss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetCachedMetamethod(
        LuaTable metatable,
        string methodName,
        out LuaValue result
    )
    {
        var cache = metamethodCache;
        var key = unchecked(RuntimeHelpers.GetHashCode(metatable) ^ methodName.GetHashCode());
        ref var entry = ref cache[key & (cache.Length - 1)];
        if (
            entry.Metatable == metatable
            && entry.Name == methodName
            && entry.Epoch == MetamethodCache.Epoch
        )
        {
            result = entry.Value;
            return entry.Found;
        }

        var found = metatable.TryGetValue(methodName, out result);
        entry.Metatable = metatable;
        entry.Name = methodName;
        entry.Value = result;
        entry.Found = found;
        entry.Epoch = MetamethodCache.Epoch;
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetMetatable(LuaValue value, LuaTable metatable)
    {
        MetamethodCache.Invalidate();
        switch (value.Type)
        {
            case LuaValueType.Nil:
                nilMetatable = metatable;
                break;
            case LuaValueType.Boolean:
                booleanMetatable = metatable;
                break;
            case LuaValueType.String:
                stringMetatable = metatable;
                break;
            case LuaValueType.Number:
            case LuaValueType.Integer:
                // Lua 5.3: integers and floats share a single number metatable.
                numberMetatable = metatable;
                break;
            case LuaValueType.Function:
                functionMetatable = metatable;
                break;
            case LuaValueType.Thread:
                stateMetatable = metatable;
                break;
            case LuaValueType.Fixed64:
                fixed64Metatable = metatable;
                break;
            case LuaValueType.Fixed64Vector3:
                fixed64Vector3Metatable = metatable;
                break;
            case LuaValueType.Fixed64Angle:
                fixed64AngleMetatable = metatable;
                break;
            case LuaValueType.Fixed64Euler:
                fixed64EulerMetatable = metatable;
                break;
            case LuaValueType.UserData:
                value.UnsafeRead<ILuaUserData>().Metatable = metatable;
                break;
            case LuaValueType.Table:
                value.UnsafeRead<LuaTable>().Metatable = metatable;
                break;
            case LuaValueType.UserData2:
                value.UnsafeRead<UserDataObject>().Metatable = metatable;
                break;
        }
    }
}
