using System.Runtime.CompilerServices;

namespace Lua.Internal;

/// <summary>
/// Global epoch used to invalidate per-state inline metamethod caches.
/// Bumped whenever a metamethod may have changed: setmetatable(), or any
/// "__"-prefixed string key written to a table (insert or overwrite).
/// <para>
/// This counter is process-global, not per-<see cref="LuaGlobalState"/> -- deliberately, since
/// it exists to invalidate <em>every</em> state's cache on any change anywhere, not just the
/// state that made it. That means it can be incremented and read from more than one thread at
/// once whenever more than one independent Lua VM is in use concurrently in the same process
/// (e.g. NUnit's parallel test fixtures each create their own <c>LuaState</c>, or a host embeds
/// several unrelated Lua environments) -- a situation this type has to be correct under even
/// though each individual state's own execution, and its own <c>metamethodCache</c> array, is
/// still only ever touched by the one thread running that state. <see cref="Invalidate"/> and
/// <see cref="Epoch"/> are therefore atomic: a plain <c>int++</c> can lose an update under
/// concurrent increments, and a plain field read is not guaranteed to observe another thread's
/// write promptly on every platform's memory model.
/// </para>
/// </summary>
static class MetamethodCache
{
    static int epoch;

    public static int Epoch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref epoch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Invalidate()
    {
        // Interlocked.Increment wraps silently on overflow, same as the `unchecked` increment
        // this replaced -- an eventual wraparound back to a value some stale cache entry still
        // carries is an accepted, pre-existing risk (see the type doc), not one this change
        // introduces or changes the odds of.
        Interlocked.Increment(ref epoch);
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
