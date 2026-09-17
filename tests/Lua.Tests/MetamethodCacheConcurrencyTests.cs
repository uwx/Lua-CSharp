using Lua.Internal;

namespace Lua.Tests;

/// <summary>
/// <see cref="MetamethodCache"/>'s epoch is process-global (see its doc comment for why), so it
/// can be incremented and read from more than one thread at once whenever more than one
/// independent Lua VM is in use concurrently in the same process -- exactly what this suite's own
/// parallel test fixtures do. This targets the counter directly rather than through Lua script
/// behavior, because a lost update is a statistical event: a behavioral test exercising
/// setmetatable() from multiple threads could pass by chance on a build with the bug (the exact
/// situation that made the earlier `unchecked epoch++`/plain-read version an open, unconfirmed
/// suspicion rather than a caught bug -- see the compiler-rewrite plan's "known suite flake"
/// section). Asserting the exact post-count instead makes a lost update a hard failure, not a
/// chance one.
/// </summary>
public class MetamethodCacheConcurrencyTests
{
    [Test]
    public void Invalidate_ConcurrentIncrements_LoseNoUpdates()
    {
        const int threadCount = 8;
        const int incrementsPerThread = 20_000;

        var before = MetamethodCache.Epoch;
        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait(); // start every thread at the same instant
                for (var i = 0; i < incrementsPerThread; i++)
                {
                    MetamethodCache.Invalidate();
                }
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var after = MetamethodCache.Epoch;

        // unchecked(int) wraparound: compare the delta, not the raw values, so this test is
        // correct even on a run where the counter wraps past int.MaxValue mid-test.
        var delta = unchecked(after - before);
        Assert.That(
            delta,
            Is.EqualTo(threadCount * incrementsPerThread),
            "a lost update means Invalidate()/Epoch are not safe to call from concurrent " +
                "independent Lua VMs, which this process does (parallel test fixtures)"
        );
    }
}
