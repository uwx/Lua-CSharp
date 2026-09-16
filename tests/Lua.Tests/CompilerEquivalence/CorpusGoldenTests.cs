using Lua.Runtime;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Milestone 0 of the compiler-rewrite plan (single-pass streaming -> two-pass AST + codegen):
/// captures a golden, byte-for-byte bytecode dump (see <see cref="BytecodeDump"/>) of every real
/// script in NFM-World's actual UI/reactive corpus, compiled with today's compiler. Once the
/// rewrite begins, this test is the objective "did the new compiler emit identical bytecode"
/// gate for Milestones 1-2 -- the rest of the suite is purely behavioral and cannot catch a
/// shape-preserving-but-different-bytecode regression on its own.
///
/// The corpus lives in the sibling NFMWorld.Library project (outside this submodule), so these
/// tests skip gracefully -- rather than fail -- when that directory isn't present, keeping
/// Lua-CSharp testable standalone.
/// </summary>
public class CorpusGoldenTests
{
    static readonly string CorpusRoot = FileHelper.GetAbsolutePath(
        "../../../../NFMWorld.Library/data"
    );

    static readonly string GoldenRoot = FileHelper.GetAbsolutePath("Golden");

    public static IEnumerable<string> CorpusFiles()
    {
        if (!Directory.Exists(CorpusRoot))
        {
            yield break;
        }

        // `.d.luau` files are Luau type-declaration syntax, not executable Lua, and don't
        // parse as a chunk. `preact-luau` is unrelated vendored code -- NFM-World's actual UI
        // runtime is the Sx signals runtime (see sx/GUIDE.md), not preact-luau.
        foreach (
            var file in Directory.EnumerateFiles(CorpusRoot, "*.luau", SearchOption.AllDirectories)
        )
        {
            if (file.EndsWith(".d.luau", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/');
            if (relative.StartsWith("library/preact-luau/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    [Test]
    public void CorpusDirectoryIsReachable()
    {
        // If this fails, the golden dumps below are stale/were never regenerated against the
        // real corpus -- CorpusFiles() silently yields nothing when the directory is missing,
        // so this exists to make that condition loud instead of a silent no-op test run.
        Assert.That(
            Directory.Exists(CorpusRoot),
            $"Expected to find the NFMWorld.Library corpus at {CorpusRoot}. If this submodule " +
                "is being tested standalone (outside the NFM-World monorepo layout), the " +
                "corpus-comparison tests below are inert (they skip via CorpusFiles() yielding " +
                "nothing) -- that's fine, but this assertion exists so a genuinely broken path " +
                "inside the monorepo doesn't silently pass as green."
        );
    }

    [TestCaseSource(nameof(CorpusFiles))]
    public void CompilesToGoldenBytecode(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath);
        var relativeName = Path.GetRelativePath(CorpusRoot, sourcePath).Replace('\\', '/');
        var goldenPath = Path.Combine(GoldenRoot, relativeName.Replace('/', '_') + ".dump");

        var state = LuaState.Create();
        state.OpenStandardLibraries();
        var closure = state.Load(source, relativeName);
        var actual = BytecodeDump.Dump(closure.Proto);

        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(GoldenRoot);
            File.WriteAllText(goldenPath, actual);
            Assert.Inconclusive(
                $"No golden dump existed yet for {relativeName} -- wrote one to {goldenPath}. " +
                    "Re-run to verify, and commit the new golden file."
            );
            return;
        }

        var expected = File.ReadAllText(goldenPath);
        Assert.That(
            actual,
            Is.EqualTo(expected),
            $"Bytecode for {relativeName} no longer matches the golden dump at {goldenPath}. " +
                "If this change is intentional (e.g. landing Milestone 3/4 of the compiler-" +
                "rewrite plan), regenerate the golden file and review the diff; if not, this is " +
                "exactly the class of regression this test exists to catch."
        );
    }
}
