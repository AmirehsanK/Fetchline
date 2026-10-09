using System.Collections.Concurrent;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// Caches in the pipeline. A cache changes how long a run takes and nothing else, so there are
/// two things to hold it to: the cycles, worked out by hand and by formula, and the answers,
/// which must be the reference machine's whatever shape the caches are.
/// </summary>
public class CachePipelineTests
{
    private const ulong Seed = 0xCAC4_E100;
    private const int Programs = 300;
    private const ulong Budget = 1_000_000;

    private static PipelineRun Run(string source, PipelineConfig config)
    {
        var output = new StringWriter();
        var machine = new PipelineMachine(AssemblerTesting.Assemble(source), output, config: config);
        var records = new List<CycleRecord>();
        while (!machine.IsFinished && records.Count < 200_000)
        {
            records.Add(machine.Step());
        }

        Assert.True(machine.IsFinished, "the run did not end");
        return new PipelineRun(machine, records, output.ToString());
    }

    private static PipelineConfig WithData(int sets, int ways, int block, int penalty) =>
        new() { DataCache = new CacheConfig(sets, ways, block, penalty) };

    private static PipelineConfig WithInstructions(int sets, int ways, int block, int penalty) =>
        new() { InstructionCache = new CacheConfig(sets, ways, block, penalty) };

    private static int Misses(PipelineRun run, CacheKind kind) =>
        run.Records.SelectMany(record => record.Events).OfType<CacheEvent>().Count(access => access.Kind == kind && !access.Hit);

    [Fact]
    public void ACacheThatCannotBeBuiltIsRefusedWithThePipeline()
    {
        var config = new PipelineConfig { DataCache = new CacheConfig(3, 1, 4, 1) };

        Assert.Throws<ArgumentException>(config.Validate);
        Assert.Throws<ArgumentException>(() => new PipelineMachine(AssemblerTesting.Assemble("nop"), config: config));
        Assert.Null(PipelineConfig.Default.DataCache);
        Assert.Null(PipelineConfig.Default.InstructionCache);
    }

    [Fact]
    public void ALoadThatMissesWaitsInMemAndEverythingBehindItWaitsToo()
    {
        // The second load reads the word beside the first, in the same block of eight bytes.
        const string Source = "lw t0, 0(sp)\nlw t1, 4(sp)\naddi t2, t1, 1\nlw t3, -64(sp)\n";
        var run = Run(Source, WithData(sets: 4, ways: 1, block: 8, penalty: 3));

        // A miss costs three cycles in MEM. The instructions behind it keep their stages for
        // those three cycles; the second load hits and costs nothing.
        Assert.Equal("IF ID EX MEM MEM MEM MEM WB", run.Row(1));
        Assert.Equal("IF ID EX EX EX EX MEM WB", run.Row(2));
        Assert.Equal("IF ID ID ID ID ID EX MEM WB", run.Row(3));      // and one more for load-use
        Assert.Equal("IF IF IF IF IF ID EX MEM MEM MEM MEM WB", run.Row(4));

        // Three cycles of waiting are three stalls, each about the load, counting down.
        var stalls = run.Records.SelectMany(record => record.Events).OfType<StallEvent>()
            .Where(stall => stall.Cause == StallCause.DataCacheMiss).ToList();
        Assert.Equal([(1ul, 2), (1ul, 1), (1ul, 0), (4ul, 2), (4ul, 1), (4ul, 0)], stalls.Select(stall => (stall.Seq, (int)stall.Remaining)));
        Assert.All(stalls, stall => Assert.Equal(Stage.Memory, stall.Stage));

        // One access for each load, said once, in the cycle it reached MEM.
        var accesses = run.Records.SelectMany(record => record.Events).OfType<CacheEvent>().ToList();
        Assert.Equal([(1ul, false), (2ul, true), (4ul, false)], accesses.Select(access => (access.Seq, access.Hit)));
        Assert.All(accesses, access => Assert.Equal(CacheKind.Data, access.Kind));
        Assert.Equal(0x7FFF_FFF0u, accesses[0].Address);

        // Without the cache the same program takes the six cycles fewer that the two misses cost.
        Assert.Equal(PipelineTesting.RunToEnd(Source).Cycles + 6, run.Cycles);
    }

    [Fact]
    public void AStoreThatMissesBringsItsBlockInAndTheLoadAfterItHits()
    {
        var run = Run("li t0, 7\nsw t0, 0(sp)\nlw t1, 0(sp)\n", WithData(sets: 2, ways: 2, block: 16, penalty: 2));

        Assert.Equal("IF ID EX MEM MEM MEM WB", run.Row(2));
        Assert.Equal(7u, run["t1"]);
        Assert.Equal(1, Misses(run, CacheKind.Data));

        // The store took effect once, at the end of its wait, and not in the cycles before.
        var written = run.Records.Where(record => record.Events.OfType<MemWriteEvent>().Any()).Select(record => record.Cycle).ToList();
        Assert.Equal([7ul], written);
    }

    [Fact]
    public void AnInstructionHeldInExTakesItsForwardedOperandWhenTheWaitBegins()
    {
        // The store misses and waits in MEM. The addi behind it is in EX and needs t0 from the
        // li ahead of the store, which is in WB for exactly one cycle: cycle 5, the first of the wait.
        // If the addi took its operand only when it was let go, the li would be long gone.
        const string Source = "li t0, 5\nsw zero, 0(sp)\naddi t1, t0, 1\nadd t2, t1, t0\n";
        var run = Run(Source, WithData(sets: 1, ways: 1, block: 4, penalty: 5));

        Assert.Equal((6u, 11u), (run["t1"], run["t2"]));
        Assert.Equal("IF ID EX EX EX EX EX EX MEM WB", run.Row(3));

        var forward = run.Records.SelectMany(record => record.Events.OfType<ForwardEvent>().Select(item => (record.Cycle, item)))
            .First(pair => pair.item.Seq == 3);
        Assert.Equal((5ul, ForwardSource.MemWb, 5u, 1ul), (forward.Cycle, forward.item.From, forward.item.Value, forward.item.Producer));

        // And it is right built the other ways too, where there is nothing to forward.
        foreach (var hazards in new[] { HazardHandling.Forwarding, HazardHandling.StallOnly })
        {
            foreach (var branches in Enum.GetValues<BranchDecision>())
            {
                var config = new PipelineConfig { Hazards = hazards, Branches = branches, DataCache = new CacheConfig(1, 1, 4, 5), MulDivCycles = 3 };
                var lockstep = new Lockstep(AssemblerTesting.Assemble(Source + "mul t3, t2, t1\nsw t3, 8(sp)\nlw t4, 8(sp)\naddi t4, t4, 1\n"), TextWriter.Null, config: config);
                lockstep.Run(10_000);
                Assert.Null(lockstep.Divergence);
                Assert.Equal(67u, lockstep.Pipeline.Hart.X[29]);
            }
        }
    }

    [Fact]
    public void AnInstructionThatMissesWaitsInIfAndNothingElseDoes()
    {
        // Blocks of sixteen bytes are four instructions: the first and the fifth miss.
        const string Source = "li t0, 1\nli t1, 2\nli t2, 3\nli t3, 4\nli t4, 5\nli t5, 6\n";
        var run = Run(Source, WithInstructions(sets: 2, ways: 1, block: 16, penalty: 2));

        Assert.Equal("IF IF IF ID EX MEM WB", run.Row(1));
        Assert.Equal("IF ID EX MEM WB", run.Row(2));
        Assert.Equal("IF IF IF ID EX MEM WB", run.Row(5));
        Assert.Equal(2, Misses(run, CacheKind.Instruction));
        Assert.Equal(PipelineTesting.RunToEnd(Source).Cycles + 4, run.Cycles);

        // While the fifth waits, the ones ahead of it go on: the fourth is never held.
        Assert.Equal("IF ID EX MEM WB", run.Row(4));
        var stalls = run.Records.SelectMany(record => record.Events).OfType<StallEvent>().ToList();
        Assert.Equal([(1ul, 1), (1ul, 0), (5ul, 1), (5ul, 0)], stalls.Select(stall => (stall.Seq, (int)stall.Remaining)));
        Assert.All(stalls, stall => Assert.Equal((StallCause.InstructionCacheMiss, Stage.Fetch), (stall.Cause, stall.Stage)));
    }

    [Fact]
    public void ALoopHitsEveryTimeRoundAfterTheFirst()
    {
        const string Source = "li t0, 20\nloop: addi t0, t0, -1\nbnez t0, loop\n";
        var run = Run(Source, WithInstructions(sets: 4, ways: 1, block: 4, penalty: 4));

        // Three instructions, three blocks, three misses, however many times the loop goes round.
        Assert.Equal(3, Misses(run, CacheKind.Instruction));
        Assert.True(run.Records.SelectMany(record => record.Events).OfType<CacheEvent>().Count(access => access.Hit) > 30);
        Assert.Equal(0u, run["t0"]);
    }

    [Fact]
    public void AnInstructionThrownAwayWhileItWaitsLeavesItsBlockBehind()
    {
        // The branch is taken in EX while the instruction two behind it is still waiting for
        // its block in IF. It is squashed there; when the program comes back to that address
        // later, the block is in the cache.
        const string Source = "li t0, 2\nj over\nback: addi t0, t0, -1\nover: bnez t0, back\n";
        var run = Run(Source, WithInstructions(sets: 8, ways: 1, block: 4, penalty: 6));

        var accesses = run.Records.SelectMany(record => record.Events).OfType<CacheEvent>().ToList();
        var back = AssemblerTesting.Assemble(Source).AddressOf("back");
        var first = accesses.First(access => access.Address == back);
        Assert.False(first.Hit);

        // It was squashed in IF before its wait was over, and never reached ID that time.
        Assert.Contains(run.Records.SelectMany(record => record.Events).OfType<FlushEvent>(),
            flush => flush.Seq == first.Seq && flush.Stage == Stage.Fetch);
        Assert.DoesNotContain(run.Records, record => record.Decode.Seq == first.Seq);

        // Every later fetch of it hits.
        Assert.All(accesses.Where(access => access.Address == back).Skip(1), access => Assert.True(access.Hit));
        Assert.Equal(0u, run["t0"]);
    }

    [Fact]
    public void TheSameProgramAndCachesGiveTheSameRecordStream()
    {
        static ulong HashOf(PipelineConfig config)
        {
            var machine = new PipelineMachine(
                AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", "bubble-sort.s"))), config: config);
            var hash = default(TraceHash);
            while (!machine.IsFinished)
            {
                machine.Step().AddTo(ref hash);
            }

            return hash.Value;
        }

        var cached = new PipelineConfig { InstructionCache = new CacheConfig(4, 2, 16, 3), DataCache = new CacheConfig(2, 1, 8, 5) };
        Assert.Equal(HashOf(cached), HashOf(cached));
        Assert.NotEqual(HashOf(PipelineConfig.Default), HashOf(cached));
        Assert.NotEqual(HashOf(cached), HashOf(cached with { DataCache = new CacheConfig(2, 1, 8, 6) }));
    }

    [Fact]
    public void TheSameTableTakesLongerDownItsColumnsThanAlongItsRows()
    {
        // The example that teaches it: 64 words, read once each, in two orders.
        static (int Misses, int Cycles, string Printed) Walk(string example, CacheConfig? cache)
        {
            var run = Run(File.ReadAllText(Repo.PathOf("examples", example)), new PipelineConfig { DataCache = cache });
            Assert.Equal(64, run.Records.SelectMany(record => record.Events).OfType<MemReadEvent>().Count());
            return (Misses(run, CacheKind.Data), run.Cycles, run.Output);
        }

        var small = new CacheConfig(4, 1, 16, 10);
        var twoWays = new CacheConfig(2, 2, 16, 10);
        var big = new CacheConfig(16, 1, 16, 10);

        // With no cache the two orders are the same program for all the pipeline can tell.
        Assert.Equal((0, 749, "2080\n"), Walk("cache-rows.s", null));
        Assert.Equal((0, 749, "2080\n"), Walk("cache-columns.s", null));

        // Along the rows a block of four words is waited for once and then read three times
        // more. Down the columns the next word is two blocks on, in a set that the word after
        // it will want again: every load misses. 48 more misses at ten cycles each is 480.
        Assert.Equal((16, 909, "2080\n"), Walk("cache-rows.s", small));
        Assert.Equal((64, 1389, "2080\n"), Walk("cache-columns.s", small));

        // Two ways do not help: a column is eight blocks, and they all want the same set.
        Assert.Equal((64, 1389, "2080\n"), Walk("cache-columns.s", twoWays));

        // A cache the whole table fits in makes the order not matter again.
        Assert.Equal((16, 909, "2080\n"), Walk("cache-rows.s", big));
        Assert.Equal((16, 909, "2080\n"), Walk("cache-columns.s", big));
    }

    /// <summary>The shapes of cache the random programs are run with, and what each is there for.</summary>
    private static readonly (CacheConfig? Instructions, CacheConfig? Data)[] Shapes =
    [
        (null, new CacheConfig(1, 1, 4, 1)),                                // one word: nearly every access misses
        (null, new CacheConfig(4, 2, 16, 7)),
        (new CacheConfig(1, 1, 4, 1), null),                                // one instruction: every fetch misses
        (new CacheConfig(8, 1, 16, 3), null),
        (new CacheConfig(2, 2, 8, 2), new CacheConfig(2, 1, 8, 5)),         // both, small, different penalties
        (new CacheConfig(64, 4, 32, 9), new CacheConfig(64, 4, 32, 9)),     // both, big enough to miss once
        (new CacheConfig(4, 1, 16, 10), new CacheConfig(2, 2, 16, 10)),     // the ones the playground offers
        (new CacheConfig(16, 1, 16, 10), new CacheConfig(4, 1, 16, 10)),
        (null, new CacheConfig(16, 1, 16, 10)),
    ];

    [Fact]
    public void RandomProgramsRunInLockstepWithCachesOfEveryShapeBuiltEveryCorrectWay()
    {
        var failures = new ConcurrentQueue<string>();
        long misses = 0;

        Parallel.For(0, Programs, index =>
        {
            var seed = Seed + (ulong)index;
            var source = ProgramGenerator.Generate(new SeededRandom(seed));
            var program = AssemblerTesting.Assemble(source);
            var (instructions, data) = Shapes[index % Shapes.Length];

            foreach (var plain in Configurations.Correct)
            {
                var config = plain with { InstructionCache = instructions, DataCache = data };
                var where = $"program {index} (seed {seed:X}), {Configurations.Name(plain)}, icache {instructions}, dcache {data}";

                var cached = new Lockstep(program, TextWriter.Null, config: config);
                var stats = new PipelineStats();
                while (!cached.Pipeline.IsFinished && cached.Pipeline.Cycles < 4 * Budget)
                {
                    stats.Add(cached.Step());
                }

                if (cached.Divergence is { } divergence || !cached.Pipeline.IsFinished)
                {
                    failures.Enqueue($"{where}: {cached.Divergence?.Describe() ?? "the pipeline did not finish"}\n{source}");
                    return;
                }

                // The time a miss in MEM costs is exactly its penalty, because everything
                // stands still for it. A miss in IF costs at most its penalty: a wait that
                // coincides with another, or with a stall, is served at the same time.
                var bare = new PipelineMachine(program, config: plain) { Recording = false };
                bare.Run(4 * Budget);
                var most = bare.Cycles
                    + (ulong)(stats.CacheMisses(CacheKind.Data) * (data?.MissPenalty ?? 0))
                    + (ulong)(stats.CacheMisses(CacheKind.Instruction) * (instructions?.MissPenalty ?? 0));
                // A program that reads the cycle counter can do something else when it reads
                // another number, so its two runs are not the same run at two speeds.
                var timed = !source.Contains("rdcycle", StringComparison.Ordinal);
                var exact = instructions is null;
                if (timed && (cached.Pipeline.Cycles > most || cached.Pipeline.Cycles < bare.Cycles || (exact && cached.Pipeline.Cycles != most)))
                {
                    failures.Enqueue($"{where}: {cached.Pipeline.Cycles} cycles, against {bare.Cycles} without caches and {most} at most\n{source}");
                    return;
                }

                Interlocked.Add(ref misses, stats.CacheMisses(CacheKind.Data) + stats.CacheMisses(CacheKind.Instruction));
            }
        });

        Assert.True(failures.IsEmpty, failures.FirstOrDefault());
        Assert.True(misses > 100_000, $"only {misses} misses: the caches were hardly tried");
    }

    [Fact]
    public void EveryPairOfCachesThePlaygroundOffersRunsInLockstepBuiltEveryCorrectWay()
    {
        // What can be switched to on the page is what is held to the reference machine here:
        // every instruction cache on offer with every data cache on offer, on every correct
        // configuration.
        var pairs = Fetchline.Viz.SwitchBoard.InstructionCaches
            .SelectMany(instructions => Fetchline.Viz.SwitchBoard.DataCaches.Select(data => (instructions, data))).ToList();
        var failures = new ConcurrentQueue<string>();
        long runs = 0;

        Parallel.For(0, 40, index =>
        {
            var seed = Seed + 0x2000 + (ulong)index;
            var source = ProgramGenerator.Generate(new SeededRandom(seed));
            var program = AssemblerTesting.Assemble(source);
            foreach (var (instructions, data) in pairs)
            {
                foreach (var plain in Configurations.Correct)
                {
                    var lockstep = new Lockstep(program, TextWriter.Null, config: plain with { InstructionCache = instructions, DataCache = data });
                    lockstep.Pipeline.Recording = false;
                    lockstep.Run(4 * Budget);
                    Interlocked.Increment(ref runs);
                    if (lockstep.Divergence is not null || !lockstep.Pipeline.IsFinished)
                    {
                        failures.Enqueue(
                            $"program {index} (seed {seed:X}), {Configurations.Name(plain)}, icache {instructions}, dcache {data}: "
                            + (lockstep.Divergence?.Describe() ?? "the pipeline did not finish") + "\n" + source);
                        return;
                    }
                }
            }
        });

        Assert.True(failures.IsEmpty, failures.FirstOrDefault());
        Assert.Equal(40 * 12 * 64, runs);
    }

    [Fact]
    public void WithHazardHandlingOffACachedPipelineIsWrongButNeverBroken()
    {
        for (var index = 0; index < 120; index++)
        {
            var source = ProgramGenerator.Generate(new SeededRandom(Seed + 0x1000 + (ulong)index));
            var (instructions, data) = Shapes[index % Shapes.Length];
            var config = new PipelineConfig { Hazards = HazardHandling.Off, InstructionCache = instructions, DataCache = data };

            // It may compute anything, loop for ever or fault; what it may not do is throw.
            var lockstep = new Lockstep(AssemblerTesting.Assemble(source), TextWriter.Null, config: config);
            lockstep.Run(200_000);
        }
    }
}
