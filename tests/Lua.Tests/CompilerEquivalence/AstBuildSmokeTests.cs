using Lua.CodeAnalysis.Compilation.Ast;
using Lua.Standard;

namespace Lua.Tests.CompilerEquivalence;

/// <summary>
/// Milestone 1 of the compiler-rewrite plan: <see cref="AstParser"/> builds a retained AST
/// covering the whole grammar (Increments A and B both landed). This test runs AST-building
/// over the whole real corpus (the same one <see cref="CorpusGoldenTests"/> uses) and asserts
/// every file builds without a genuine crash -- a whole-corpus smoke test that's meaningfully
/// stronger than any hand-written snippet, since it exercises whatever real Luau idioms the
/// actual UI/reactive code happens to use.
/// </summary>
public class AstBuildSmokeTests
{
    // All 35 real corpus files build cleanly as of Increment B. If a future grammar change
    // (e.g. a new Luau syntax addition) needs a temporary carve-out, lower this and note why --
    // any other drop is a real regression.
    const int MinimumSupportedFileCount = 35;

    [Test]
    public void AstBuildingCoversAtLeastTheExpectedSubsetOfTheCorpus()
    {
        var files = CorpusGoldenTests.CorpusFiles().ToList();
        if (files.Count == 0)
        {
            Assert.Inconclusive("Real corpus not reachable from this checkout -- see CorpusGoldenTests.");
            return;
        }

        var supported = new List<string>();
        var deferred = new List<(string File, string Reason)>();
        var genuineFailures = new List<(string File, Exception Error)>();

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var state = LuaState.Create();
            state.OpenStandardLibraries();

            try
            {
                AstParser.ParseToAst(state, source, Path.GetFileName(file));
                supported.Add(file);
            }
            catch (NotSupportedException e)
            {
                deferred.Add((file, e.Message));
            }
            catch (Exception e)
            {
                genuineFailures.Add((file, e));
            }
        }

        if (genuineFailures.Count > 0)
        {
            var details = string.Join(
                "\n",
                genuineFailures.Select(f => $"  {f.File}: {f.Error}")
            );
            Assert.Fail(
                $"{genuineFailures.Count} corpus file(s) failed AST building with something " +
                    $"other than a known-deferred NotSupportedException:\n{details}"
            );
        }

        TestContext.Out.WriteLine(
            $"AST build: {supported.Count} supported, {deferred.Count} deferred, " +
                $"{files.Count} total."
        );
        foreach (var (file, reason) in deferred)
        {
            TestContext.Out.WriteLine($"  deferred: {Path.GetFileName(file)}: {reason}");
        }

        Assert.That(
            supported.Count,
            Is.GreaterThanOrEqualTo(MinimumSupportedFileCount),
            $"Only {supported.Count} corpus files built successfully (expected at least " +
                $"{MinimumSupportedFileCount}). Deferred files hit: " +
                string.Join(", ", deferred.Select(d => Path.GetFileName(d.File)).Distinct())
        );
    }
}
