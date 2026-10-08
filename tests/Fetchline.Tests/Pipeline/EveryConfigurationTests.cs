using System.Collections.Concurrent;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// What the what-if switches promise: built any correct way, the pipeline does what the
/// reference machine does, and only the number of cycles differs. Random programs with
/// everything in them are run on every correct configuration, each in lockstep.
/// </summary>
public class EveryConfigurationTests
{
    private const ulong Seed = 0xF37C_6700;
    private const int Programs = 2000;

    // The longest of these programs is a few thousand instructions; this is far beyond any of them.
    private const ulong Budget = 1_000_000;

    /// <summary>Program number <paramref name="index"/>: always the same one, whatever ran before it.</summary>
    private static string Source(int index) => ProgramGenerator.Generate(new SeededRandom(Seed + (ulong)index));

    [Fact]
    public void ThereAreSixtyFourCorrectWaysToBuildIt()
    {
        var all = Configurations.Correct;

        Assert.Equal(64, all.Count);
        Assert.Equal(64, all.Distinct().Count());
        Assert.All(all, config => Assert.True(config.IsCorrect));
        Assert.Contains(PipelineConfig.Default, all);

        // Every value of every switch is in there somewhere.
        Assert.Equal(2, all.Select(config => config.Hazards).Distinct().Count());
        Assert.Equal(2, all.Select(config => config.Branches).Distinct().Count());
        Assert.Equal(4, all.Select(config => config.Predictor).Distinct().Count());
        Assert.Equal([16, 64, 256], all.Select(config => config.BtbEntries).Distinct().Order());
        Assert.Equal([1, 3], all.Select(config => config.MulDivCycles).Distinct().Order());
        Assert.Equal(
            "--hazards stall --branch id --predictor 2-bit --btb 256 --muldiv 3",
            Configurations.Name(all[^1]));
    }

    [Fact]
    public void TheSameSeedGivesTheSameProgram()
    {
        Assert.Equal(Source(7), Source(7));
        Assert.NotEqual(Source(7), Source(8));

        // The programs take the address of their data as a constant, so it had better be the one.
        Assert.Equal(ProgramGenerator.DataBase, AssemblerTesting.Assemble(Source(7)).AddressOf("cells"));
    }

    [Fact]
    public void TwoThousandRandomProgramsRunInLockstepBuiltEveryCorrectWay()
    {
        var failures = new ConcurrentQueue<string>();
        long runs = 0, instructions = 0;

        // Each program is its own seed, so the order they run in does not matter.
        Parallel.For(0, Programs, index =>
        {
            var source = Source(index);
            var program = AssemblerTesting.Assemble(source);

            // What the program does, from the machine that knows nothing of pipelines.
            var printed = new StringWriter();
            var reference = new ReferenceMachine(program, printed);
            var end = reference.Run(Budget);
            if (!reference.IsFinished)
            {
                failures.Enqueue($"program {index} (seed {Seed + (ulong)index:X}) did not end on the reference machine\n{source}");
                return;
            }

            Interlocked.Add(ref instructions, (long)reference.Hart.InstructionsRetired);

            // A program that reads the cycle counter may print it, and the reference machine
            // alone counts differently: for those the two outputs are not compared.
            var readsCycles = source.Contains("rdcycle", StringComparison.Ordinal);

            foreach (var config in Configurations.Correct)
            {
                var output = new StringWriter();
                var lockstep = new Lockstep(program, output, config: config);
                lockstep.Pipeline.Recording = false;
                var last = lockstep.Run(4 * Budget);
                Interlocked.Increment(ref runs);

                var problem =
                    lockstep.Divergence is { } divergence ? divergence.Describe()
                    : !lockstep.Pipeline.IsFinished ? "the pipeline did not finish"
                    : !lockstep.Pipeline.Hart.X.AsSpan().SequenceEqual(lockstep.Reference.Hart.X) ? "the registers differ at the end"
                    : (last!.Stop, (last.End ?? last.Commit)!.Value.ExitCode) != (end.Stop, end.ExitCode) ? "it ended another way"
                    : !readsCycles && output.ToString() != printed.ToString() ? $"it printed '{output}', not '{printed}'"
                    : null;
                if (problem is not null)
                {
                    failures.Enqueue(
                        $"program {index} (seed {Seed + (ulong)index:X}), {Configurations.Name(config)}: {problem}\n{source}");
                    return;
                }
            }
        });

        Assert.True(failures.IsEmpty, failures.FirstOrDefault());
        Assert.Equal(Programs * Configurations.Correct.Count, runs);

        // Guard the test: the programs must be long enough to be worth running.
        Assert.True(instructions > 80 * Programs, $"only {instructions} instructions in {Programs} programs");
    }

    [Fact]
    public void TheProgramsContainWhatTheyAreMeantToExercise()
    {
        // The sweep above proves nothing about a mechanism its programs never use. The first
        // three hundred are run with recording on, the textbook way and the opposite way, and
        // everything the hazard logic does has to have happened a good number of times.
        var stats = new[] { new PipelineStats(), new PipelineStats() };
        PipelineConfig[] configs =
        [
            new() { MulDivCycles = 3 },
            new() { Hazards = HazardHandling.StallOnly, Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit, BtbEntries = 16 },
        ];
        var (calls, indirect, withLoops, ended, exited) = (0, 0, 0, 0, 0);

        for (var index = 0; index < 300; index++)
        {
            var source = Source(index);
            var program = AssemblerTesting.Assemble(source);
            withLoops += source.Contains("\nT", StringComparison.Ordinal) ? 1 : 0;
            calls += source.Contains("call f", StringComparison.Ordinal) || source.Contains("jal f", StringComparison.Ordinal) ? 1 : 0;
            indirect += source.Contains("jalr", StringComparison.Ordinal) ? 1 : 0;

            for (var which = 0; which < configs.Length; which++)
            {
                var lockstep = new Lockstep(program, new StringWriter(), config: configs[which]);
                while (!lockstep.Pipeline.IsFinished)
                {
                    stats[which].Add(lockstep.Step());
                }

                Assert.Null(lockstep.Divergence);
                if (which == 0)
                {
                    ended += lockstep.Pipeline.Stopped == StopReason.EndOfProgram ? 1 : 0;
                    exited += lockstep.Pipeline.Stopped == StopReason.Exit ? 1 : 0;
                }
            }
        }

        // Every figure goes into the message, so that a failure shows the whole picture at once.
        var (textbook, opposite) = (stats[0], stats[1]);
        var found =
            $"{textbook.Instructions} instructions; {textbook.StallsBy(StallCause.LoadUse)} load-use stalls, " +
            $"{textbook.StallsBy(StallCause.MultiCycle)} multiply stalls; {textbook.ForwardsFrom(ForwardSource.ExMem)} forwards from EX/MEM, " +
            $"{textbook.ForwardsFrom(ForwardSource.MemWb)} from MEM/WB; {textbook.Loads} loads, {textbook.Stores} stores; " +
            $"{textbook.Branches} branches, {textbook.BranchesTaken} taken, {textbook.Mispredictions} guessed wrong; " +
            $"flushes: {textbook.FlushesBy(FlushCause.Branch)} branch, {textbook.FlushesBy(FlushCause.System)} system, " +
            $"{textbook.FlushesBy(FlushCause.Stop)} stop. Stalling only, in ID, two bits: " +
            $"{opposite.StallsBy(StallCause.DataHazard)} waits for a value, {opposite.Mispredictions} guessed wrong. " +
            $"Programs: {withLoops} with loops, {calls} with calls, {indirect} with indirect calls, " +
            $"{ended} ran off the end, {exited} exited";

        // Each floor is about two thirds of what the generator gives now.
        Assert.True(textbook.Instructions > 25_000, found);
        Assert.True(textbook.StallsBy(StallCause.LoadUse) > 300, found);
        Assert.True(textbook.StallsBy(StallCause.MultiCycle) > 2000, found);
        Assert.True(textbook.ForwardsFrom(ForwardSource.ExMem) > 6000, found);
        Assert.True(textbook.ForwardsFrom(ForwardSource.MemWb) > 800, found);
        Assert.True(textbook.Loads > 1500 && textbook.Stores > 1500, found);
        Assert.True(textbook.Branches > 6000, found);
        Assert.True(textbook.BranchesTaken > 4000, found);
        Assert.True(textbook.Branches - textbook.BranchesTaken > 1500, found);
        Assert.True(textbook.FlushesBy(FlushCause.Branch) > 3000, found);
        Assert.True(textbook.FlushesBy(FlushCause.System) > 2000, found);
        Assert.True(textbook.FlushesBy(FlushCause.Stop) > 100, found);

        Assert.Equal(textbook.Instructions, opposite.Instructions);
        Assert.Equal(0, opposite.Forwards);
        Assert.True(opposite.StallsBy(StallCause.DataHazard) > 15_000, found);
        Assert.True(opposite.Mispredictions > 2000, found);
        Assert.True(opposite.Mispredictions < textbook.Mispredictions, found);

        Assert.True(withLoops > 200 && calls > 120 && indirect > 120, found);
        Assert.True(ended > 60 && exited > 120, found);
    }

    [Fact]
    public void OddSizesOfBufferAndMultiplierAreNoDifferent()
    {
        // Outside what the playground offers: a buffer so small that every branch pushes
        // another out, and multipliers of awkward lengths, the longest allowed among them.
        PipelineConfig[] odd =
        [
            new() { Predictor = Predictor.TwoBit, BtbEntries = 1, MulDivCycles = 2 },
            new() { Predictor = Predictor.OneBit, BtbEntries = 2, Branches = BranchDecision.Decode, MulDivCycles = 7 },
            new() { Predictor = Predictor.TwoBit, BtbEntries = 4, Hazards = HazardHandling.StallOnly, MulDivCycles = 5 },
            new() { Predictor = Predictor.OneBit, BtbEntries = 65536, Branches = BranchDecision.Decode, Hazards = HazardHandling.StallOnly },
            new() { Predictor = Predictor.BackwardTaken, MulDivCycles = PipelineConfig.MaxMulDivCycles },
        ];
        var failures = new ConcurrentQueue<string>();

        Parallel.For(0, 300, index =>
        {
            var source = Source(index);
            var program = AssemblerTesting.Assemble(source);
            foreach (var config in odd)
            {
                var lockstep = new Lockstep(program, new StringWriter(), config: config);
                lockstep.Pipeline.Recording = false;
                lockstep.Run(40 * Budget);
                if (lockstep.Divergence is not null || !lockstep.Pipeline.IsFinished)
                {
                    failures.Enqueue(
                        $"program {index} (seed {Seed + (ulong)index:X}), {Configurations.Name(config)}: " +
                        $"{lockstep.Divergence?.Describe() ?? "did not finish"}\n{source}");
                    return;
                }
            }
        });

        Assert.True(failures.IsEmpty, failures.FirstOrDefault());
    }

    [Fact]
    public void WithHazardsOffTheSameProgramsNeverBreakTheMachineAndEveryReportIsTrue()
    {
        // With nothing handled a program can do anything: jump through a register that was not
        // ready, store where it should not, never end. Whatever it does, the pipeline must stop
        // or carry on as a machine would, and what the checker says must be so: the two records
        // it shows really differ, every instruction before them really matched, and a run with
        // no report really did end as the reference machine did.
        PipelineConfig[] off =
        [
            new() { Hazards = HazardHandling.Off },
            new() { Hazards = HazardHandling.Off, Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit, BtbEntries = 16, MulDivCycles = 3 },
        ];
        var failures = new ConcurrentQueue<string>();
        int wrong = 0, right = 0, endless = 0, faulted = 0;
        long cycles = 0;

        Parallel.For(0, 1000, index =>
        {
            var source = Source(index);
            var program = AssemblerTesting.Assemble(source);
            foreach (var config in off)
            {
                var lockstep = new Lockstep(program, new StringWriter(), config: config);
                lockstep.Pipeline.Recording = false;
                try
                {
                    for (var cycle = 0; cycle < 20_000 && !lockstep.Pipeline.IsFinished; cycle++)
                    {
                        lockstep.Step();
                        Interlocked.Increment(ref cycles);
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue($"program {index} (seed {Seed + (ulong)index:X}), {Configurations.Name(config)}: {exception}\n{source}");
                    return;
                }

                Interlocked.Add(ref endless, lockstep.Pipeline.IsFinished ? 0 : 1);
                Interlocked.Add(ref faulted, lockstep.Pipeline.Stopped == StopReason.Fault ? 1 : 0);
                string? problem = null;
                if (lockstep.Divergence is { } divergence)
                {
                    Interlocked.Increment(ref wrong);
                    problem = divergence.Pipeline == divergence.Reference ? "the two records reported are the same"
                        : divergence.Index != lockstep.Compared ? "something was compared after the first difference"
                        : null;
                }
                else if (lockstep.Pipeline.IsFinished)
                {
                    Interlocked.Increment(ref right);
                    problem = lockstep.Pipeline.Hart.X.AsSpan().SequenceEqual(lockstep.Reference.Hart.X)
                        ? null
                        : "nothing was reported, but the registers differ";
                }

                if (problem is not null)
                {
                    failures.Enqueue($"program {index} (seed {Seed + (ulong)index:X}), {Configurations.Name(config)}: {problem}\n{source}");
                    return;
                }
            }
        });

        Assert.True(failures.IsEmpty, failures.FirstOrDefault());

        // Nearly all of them go wrong, as they should, and they go on for a while after they do:
        // some to a fault, some for ever, most to an end of their own.
        var found = $"{wrong} wrong, {right} right, {endless} cut off, {faulted} faulted, {cycles} cycles in 2000 runs";
        Assert.True(wrong > 1900, found);
        Assert.True(wrong + right + endless >= 2000, found);
        Assert.True(cycles > 2000 * 150, found);
        Assert.True(endless > 0 && faulted > 200 && faulted < 1800, found);
    }
}
