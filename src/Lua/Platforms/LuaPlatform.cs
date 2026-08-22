using Lua.IO;
using Lua.Loaders;

namespace Lua.Platforms;

/// <summary>
///  Platform abstraction for Lua.
/// </summary>
/// <param name="FileSystem"></param>
/// <param name="OsEnvironment"></param>
/// <param name="StandardIO"></param>
public record LuaPlatform(
    ILuaFileSystem FileSystem,
    ILuaOsEnvironment OsEnvironment,
    ILuaStandardIO StandardIO,
    TimeProvider TimeProvider
)
{
    /// <summary>
    /// Enables Luau-style "require by string" semantics: relative module paths
    /// (starting with "./" or "../") are resolved against the directory of the
    /// requiring file, with ".luau"/".lua" and "init.luau"/"init.lua" fallbacks.
    /// Disabled by default for backward compatibility.
    /// </summary>
    public bool RequireByString { get; init; } = false;

    /// <summary>
    /// Standard console platform implementation.
    /// Uses real file system, console I/O, and system operations.
    /// </summary>
    public static LuaPlatform Default { get; } =
        new(
            FileSystem: new FileSystem(),
            OsEnvironment: new SystemOsEnvironment(),
            StandardIO: new ConsoleStandardIO(),
            TimeProvider: TimeProvider.System
        );
}
