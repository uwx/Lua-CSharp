using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>
/// Global epoch used to invalidate per-state inline metamethod caches.
/// Bumped whenever a metamethod may have changed: setmetatable(), or any
/// "__"-prefixed string key written to a table (insert or overwrite).
/// The VM executes single-threaded per state, so a plain int is sufficient.
/// </summary>
static class MetamethodCache
{
    static int epoch;

    public static int Epoch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => epoch;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Invalidate()
    {
        unchecked
        {
            epoch++;
        }
    }

    /// <summary>
    /// True when <paramref name="key"/> is a metamethod name ("__"-prefixed string).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMetamethodKey(in LuaValue key)
    {
        if (key.Type != LuaValueType.String)
        {
            return false;
        }

        return IsMetamethodKey(key.ReadAsString());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMetamethodKey(string s)
    {
        return s.Length >= 2 && s[0] == '_' && s[1] == '_';
    }
}
