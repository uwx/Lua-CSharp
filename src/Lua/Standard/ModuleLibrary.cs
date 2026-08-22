using System.Runtime.CompilerServices;
using Lua.Internal.CompilerServices;
using Lua.Runtime;

namespace Lua.Standard;

public sealed class ModuleLibrary
{
    public static readonly ModuleLibrary Instance = new();
    internal const string LoadedKeyForRegistry = "_LOADED";
    internal const string PreloadKeyForRegistry = "_PRELOAD";

    public ModuleLibrary()
    {
        RequireFunction = new("require", Require);
        SearchPathFunction = new("package.searchpath", SearchPath);
    }

    public readonly LuaFunction RequireFunction;
    public readonly LuaFunction SearchPathFunction;

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<int> Require(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var arg0 = context.GetArgument<string>(0);
        var loaded = context.GlobalState.LoadedModules;
        LuaValue loadedTable;

        // Luau-style "require by string": relative paths ("./" or "../") are
        // resolved against the directory of the requiring file, with
        // ".luau"/".lua" and "init.luau"/"init.lua" resolution. Optional;
        // enabled via LuaPlatform.RequireByString (off by default).
        if (
            context.GlobalState.Platform.RequireByString
            && (
                arg0.StartsWith("./", StringComparison.Ordinal)
                || arg0.StartsWith("../", StringComparison.Ordinal)
            )
        )
        {
            var callerSource = GetCallerSource(context.State);
            var resolvedPath = ResolveRequireByString(context.State, arg0, callerSource);
            if (resolvedPath == null)
            {
                throw new LuaRuntimeException(context.State, $"Module '{arg0}' not found");
            }

            if (!loaded.TryGetValue(resolvedPath, out loadedTable))
            {
                var loader = await context.State.LoadFileAsync(
                    resolvedPath,
                    "bt",
                    null,
                    cancellationToken
                );
                await context.State.RunAsync(loader, 0, context.ReturnFrameBase, cancellationToken);
                loadedTable = context.State.Stack.Get(context.ReturnFrameBase);
                loaded[resolvedPath] = loadedTable;
            }

            return context.Return(loadedTable);
        }

        if (!loaded.TryGetValue(arg0, out loadedTable))
        {
            LuaFunction loader;
            var moduleLoader = context.GlobalState.ModuleLoader;
            if (moduleLoader != null && moduleLoader.Exists(arg0))
            {
                var module = await moduleLoader.LoadAsync(arg0, cancellationToken);
                loader =
                    module.Type == LuaModuleType.Bytes
                        ? context.State.Load(module.ReadBytes(), module.Name)
                        : context.State.Load(module.ReadText(), module.Name);
            }
            else
            {
                loader = await FindLoader(context.State, arg0, cancellationToken);
            }

            await context.State.RunAsync(loader, 0, context.ReturnFrameBase, cancellationToken);
            loadedTable = context.State.Stack.Get(context.ReturnFrameBase);
            loaded[arg0] = loadedTable;
        }

        return context.Return(loadedTable);
    }

    static string? GetCallerSource(LuaState state)
    {
        var frames = state.GetCallStackFrames();
        if (frames.Length < 1)
        {
            return null;
        }

        // A tail-called require (e.g. `return require("./x")`) reuses the
        // caller's frame: the tail caller's function is preserved in
        // LuaState.LastCallerFunction rather than in the call stack.
        LuaFunction? caller;
        if (frames[^1].IsTailCall)
        {
            caller = state.LastCallerFunction;
        }
        else
        {
            if (frames.Length < 2)
            {
                return null;
            }

            caller = frames[^2].Function;
        }

        return caller is LuaClosure closure ? closure.Proto.ChunkName : null;
    }

    static string? ResolveRequireByString(LuaState state, string name, string? callerSource)
    {
        // File-backed chunks carry "@path" as their chunk name (see
        // LuaStateExtensions.LoadFileAsync); DoString/load chunks carry the
        // name verbatim. Strip the '@' marker before deriving the directory.
        if (string.IsNullOrEmpty(callerSource))
        {
            throw new LuaRuntimeException(
                state,
                $"cannot use relative require '{name}' from a chunk without a file"
            );
        }

        var source = callerSource.TrimStart('@');
        if (
            source.Length == 0
            || source[0] == '='
            || (source[0] == '[' && source.EndsWith(']'))
        )
        {
            throw new LuaRuntimeException(
                state,
                $"cannot use relative require '{name}' from a chunk without a file ('{source}')"
            );
        }

        // Normalize to the canonical '/' separator used by the interpreter/VFS.
        source = source.Replace('\\', '/');
        var lastSlash = source.LastIndexOf('/');
        var baseDir = lastSlash < 0 ? "" : source[..lastSlash];

        var combined = CombineRelativePath(baseDir, name);
        if (combined == null)
        {
            return null;
        }

        var fileSystem = state.GlobalState.Platform.FileSystem;

        // Luau's updated resolution rule: implicit extension order is not
        // supported — if more than one candidate matches, require is ambiguous.
        var candidates = new List<string>(5);

        if (fileSystem.IsReadable(combined))
        {
            candidates.Add(combined);
        }

        foreach (var extension in Extensions)
        {
            var candidate = combined + extension;
            if (fileSystem.IsReadable(candidate))
            {
                candidates.Add(candidate);
            }
        }

        if (fileSystem.DirectoryExists(combined))
        {
            foreach (var initFile in InitFiles)
            {
                var candidate = combined + "/" + initFile;
                if (fileSystem.IsReadable(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        switch (candidates.Count)
        {
            case 0:
                return null;
            case > 1:
                throw new LuaRuntimeException(
                    state,
                    $"ambiguous require '{name}': multiple files match ({string.Join(", ", candidates)})"
                );
            default:
                return candidates[0];
        }
    }

    static string? CombineRelativePath(string baseDir, string relative)
    {
        var segments = new List<string>();

        if (baseDir.Length > 0)
        {
            segments.AddRange(baseDir.Split('/'));
        }

        foreach (var part in relative.Split('/'))
        {
            switch (part)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                    break;
                default:
                    segments.Add(part);
                    break;
            }
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    static readonly string[] Extensions = [".luau", ".lua"];
    static readonly string[] InitFiles = ["init.luau", "init.lua"];

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<string?> FindFile(
        LuaState state,
        string name,
        string pName,
        string dirSeparator
    )
    {
        var globalState = state.GlobalState;
        var package = globalState.Environment["package"];
        var p = await state.GetTableAsync(package, pName);
        if (!p.TryReadString(out var path))
        {
            throw new LuaRuntimeException(state, $"package.{pName} must be a string");
        }

        return SearchPath(state, name, path, ".", dirSeparator);
    }

    public ValueTask<int> SearchPath(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var name = context.GetArgument<string>(0);
        var path = context.GetArgument<string>(1);
        var separator = context.GetArgument<string>(2);
        var dirSeparator = context.GetArgument<string>(3);
        var fileName = SearchPath(context.State, name, path, separator, dirSeparator);
        return new(context.Return(fileName ?? LuaValue.Nil));
    }

    internal static string? SearchPath(
        LuaState state,
        string name,
        string path,
        string separator,
        string dirSeparator
    )
    {
        if (separator != "")
        {
            name = name.Replace(separator, dirSeparator);
        }

        var pathSpan = path.AsSpan();
        var nextIndex = pathSpan.IndexOf(';');
        if (nextIndex == -1)
        {
            nextIndex = pathSpan.Length;
        }

        do
        {
            path = pathSpan[..nextIndex].ToString();
            var fileName = path.Replace("?", name);
            if (state.GlobalState.Platform.FileSystem.IsReadable(fileName))
            {
                return fileName;
            }

            if (pathSpan.Length <= nextIndex)
            {
                break;
            }

            pathSpan = pathSpan[(nextIndex + 1)..];
            nextIndex = pathSpan.IndexOf(';');
            if (nextIndex == -1)
            {
                nextIndex = pathSpan.Length;
            }
        } while (nextIndex != -1);

        return null;
    }

    [AsyncMethodBuilder(typeof(LightAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<LuaFunction> FindLoader(
        LuaState state,
        string name,
        CancellationToken cancellationToken
    )
    {
        var package = state.GlobalState.Environment["package"].Read<LuaTable>();
        var searchers = package["searchers"].Read<LuaTable>();
        for (var i = 0; i < searchers.GetArraySpan().Length; i++)
        {
            var searcher = searchers.GetArraySpan()[i];
            if (searcher.Type == LuaValueType.Nil)
            {
                continue;
            }

            var loader = searcher;
            var top = state.Stack.Count;
            state.Stack.Push(loader);
            state.Stack.Push(name);
            var resultCount = await state.CallAsync(top, top, cancellationToken);
            if (0 < resultCount)
            {
                var result = state.Stack.Get(top);
                if (result.Type == LuaValueType.Function)
                {
                    state.Stack.SetTop(top);
                    return result.Read<LuaFunction>();
                }
            }

            state.Stack.SetTop(top);
        }

        throw new LuaRuntimeException(state, $"Module '{name}' not found");
    }

    public ValueTask<int> SearcherPreload(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var name = context.GetArgument<string>(0);
        var preload = context.GlobalState.PreloadModules[name];
        if (preload == LuaValue.Nil)
        {
            return new(context.Return());
        }

        return new(context.Return(preload));
    }

    public async ValueTask<int> SearcherLua(
        LuaFunctionExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        var name = context.GetArgument<string>(0);
        var fileName = await FindFile(
            context.State,
            name,
            "path",
            context.GlobalState.Platform.FileSystem.DirectorySeparator
        );
        if (fileName == null)
        {
            return context.Return(LuaValue.Nil);
        }

        return context.Return(
            await context.State.LoadFileAsync(fileName, "bt", null, cancellationToken)
        );
    }
}
