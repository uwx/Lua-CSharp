using Lua.IO;
using Lua.Platforms;
using Lua.Standard;
using Lua.Tests.Helpers;

namespace Lua.Tests;

public sealed class RequireByStringTests
{
    sealed class MemoryFileSystem(
        Dictionary<string, string> files,
        HashSet<string>? directories = null
    ) : NotImplementedExceptionFileSystemBase
    {
        public override bool IsReadable(string path)
        {
            return files.ContainsKey(path);
        }

        public override bool DirectoryExists(string path)
        {
            return directories?.Contains(path) ?? false;
        }

        public override string DirectorySeparator => "/";

        public override ValueTask<ILuaStream> Open(
            string path,
            LuaFileOpenMode mode,
            CancellationToken cancellationToken
        )
        {
            if (!files.TryGetValue(path, out var content))
            {
                throw new FileNotFoundException($"File '{path}' not found");
            }

            return new(ILuaStream.CreateFromMemory(content.AsMemory()));
        }
    }

    static LuaState CreateState(MemoryFileSystem fs, bool requireByString = true)
    {
        var state = LuaState.Create(
            new LuaPlatform(
                FileSystem: fs,
                OsEnvironment: null!,
                StandardIO: new ConsoleStandardIO(),
                TimeProvider: TimeProvider.System
            )
            {
                RequireByString = requireByString
            }
        );
        state.OpenStandardLibraries();
        return state;
    }

    [Test]
    public void SiblingRelativeRequire_ResolvesAgainstRequiringFile()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["lib/main.lua"] = "assert(require('./dep') == 42, 'expected 42')",
                ["lib/dep.lua"] = "return 42",
            }
        );
        var state = CreateState(fs);
        state.DoFile("lib/main.lua");
    }

    [Test]
    public void ParentRelativeRequire_ResolvesParentDirectory()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["lib/sub/main.lua"] = "assert(require('../shared') == 'ok', 'expected ok')",
                ["lib/shared.lua"] = "return 'ok'",
            }
        );
        var state = CreateState(fs);
        state.DoFile("lib/sub/main.lua");
    }

    [Test]
    public void ExtensionResolution_PrefersLuau()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["dir/main.lua"] = "assert(require('./mod') == 'luau', 'expected luau')",
                ["dir/mod.luau"] = "return 'luau'",
            }
        );
        var state = CreateState(fs);
        state.DoFile("dir/main.lua");
    }

    [Test]
    public void ExtensionResolution_FallsBackToLua()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["dir/main.lua"] = "assert(require('./mod') == 'lua', 'expected lua')",
                ["dir/mod.lua"] = "return 'lua'",
            }
        );
        var state = CreateState(fs);
        state.DoFile("dir/main.lua");
    }

    [Test]
    public void AmbiguousExtension_Throws()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["dir/main.lua"] = "return require('./mod')",
                ["dir/mod.luau"] = "return 1",
                ["dir/mod.lua"] = "return 2",
            }
        );
        var state = CreateState(fs);
        Assert.Throws<LuaRuntimeException>(() => state.DoFile("dir/main.lua"));
    }

    [Test]
    public void DirectoryInit_ResolvesInitLua()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["dir/main.lua"] = "assert(require('./pkg') == 7, 'expected 7')",
                ["dir/pkg/init.lua"] = "return 7",
            },
            directories: ["dir/pkg"]
        );
        var state = CreateState(fs);
        state.DoFile("dir/main.lua");
    }

    [Test]
    public void NestedRelativeRequire_Chains()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["a/main.lua"] = "assert(require('./b') == 99, 'expected 99')",
                ["a/b.lua"] = "return require('./c')",
                ["a/c.lua"] = "return 99",
            }
        );
        var state = CreateState(fs);
        state.DoFile("a/main.lua");
    }

    [Test]
    public void NonFileChunk_Throws()
    {
        var fs = new MemoryFileSystem(new());
        var state = CreateState(fs);
        Assert.Throws<LuaRuntimeException>(
            () => state.DoString("return require('./x')", chunkName: "=stdin")
        );
    }

    [Test]
    public void FlagOff_KeepsLegacyResolution()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["dir/main.lua"] = "return require('./dep')",
                ["dir/dep.lua"] = "return 1",
            }
        );
        var state = CreateState(fs, requireByString: false);
        Assert.Throws<LuaRuntimeException>(() => state.DoFile("dir/main.lua"));
    }

    [Test]
    public void NonRelativeRequire_StillWorksWithFlagOn()
    {
        var fs = new MemoryFileSystem(
            new()
            {
                ["main.lua"] = "assert(require('dep') == 5, 'expected 5')",
                ["dep.lua"] = "return 5",
            }
        );
        var state = CreateState(fs);
        state.DoFile("main.lua");
    }
}
