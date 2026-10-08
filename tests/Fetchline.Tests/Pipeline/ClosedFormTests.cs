using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// For straight-line code the length of a run is known without running it: N instructions take
/// N + 4 cycles, plus one for every load whose value the very next instruction uses. The
/// pipeline is held to that formula on random programs, with the count of load-use pairs worked
/// out here from the program text, not from anything the engine says.
/// </summary>
public class ClosedFormTests
{
    private static readonly string[] Registers = ["a0", "a1", "a2", "a3", "t0", "t1", "t2", "zero"];

    private static readonly string[] Slow = ["mul", "mulh", "mulhsu", "mulhu", "div", "divu", "rem", "remu"];

    /// <summary>One generated instruction: its text, and what it reads and writes.</summary>
    /// <param name="IsSlow">A multiply or a divide.</param>
    private sealed record Line(string Text, string? Writes, bool IsLoad, string[] Reads, bool IsSlow = false);

    private static Line Random(SeededRandom random)
    {
        string R() => random.Pick(Registers);
        var (rd, rs1, rs2) = (R(), R(), R());
        var offset = 4 * random.Next(0, 7);

        // s0 holds the address of the data and is never written, so every access is in bounds.
        return random.Next(0, 11) switch
        {
            < 3 => new Line($"lw {rd}, {offset}(s0)", rd, true, ["s0"]),
            3 => new Line($"lbu {rd}, {offset + random.Next(0, 3)}(s0)", rd, true, ["s0"]),
            < 6 => new Line($"sw {rs2}, {offset}(s0)", null, false, ["s0", rs2]),
            6 => new Line($"addi {rd}, {rs1}, {random.Next(-100, 100)}", rd, false, [rs1]),
            7 => new Line($"lui {rd}, {random.Next(0, 0xFFFFF)}", rd, false, []),
            8 => new Line($"slli {rd}, {rs1}, {random.Next(0, 31)}", rd, false, [rs1]),
            < 11 => new Line($"{random.Pick(Slow)} {rd}, {rs1}, {rs2}", rd, false, [rs1, rs2], IsSlow: true),
            _ => new Line($"add {rd}, {rs1}, {rs2}", rd, false, [rs1, rs2]),
        };
    }

    /// <summary>
    /// The load-use pairs of a program: a load into a real register followed at once by an
    /// instruction that reads that register.
    /// </summary>
    private static int PairsIn(List<Line> lines)
    {
        var pairs = 0;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i] is { IsLoad: true, Writes: not "zero" } load && lines[i + 1].Reads.Contains(load.Writes))
            {
                pairs++;
            }
        }

        return pairs;
    }

    private static string SourceOf(List<Line> lines) =>
        ".data\ncells: .word 1, 2, 3, 4, 5, 6, 7, 8\n.text\nlui s0, 0x10000\n" + string.Join('\n', lines.Select(line => line.Text));

    [Fact]
    public void StraightLineCodeTakesNPlusFourCyclesPlusOnePerLoadUsePair()
    {
        var random = new SeededRandom(0xF37C_5901);
        var totalPairs = 0;

        for (var round = 0; round < 1000; round++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 60)).Select(_ => Random(random)).ToList();
            var source = SourceOf(lines);

            // The first instruction (lui s0) is not a load, so it is in no pair.
            var pairs = PairsIn(lines);
            totalPairs += pairs;
            var lockstep = new Lockstep(AssemblerTesting.Assemble(source));
            var records = new List<CycleRecord>();
            while (!lockstep.Pipeline.IsFinished && lockstep.Divergence is null)
            {
                records.Add(lockstep.Step());
            }

            var instructions = lines.Count + 1;
            if (lockstep.Divergence is not null || records.Count != instructions + 4 + pairs)
            {
                Assert.Fail(
                    $"seed {random.Seed:X}, round {round}: {instructions} instructions with {pairs} load-use pairs took " +
                    $"{records.Count} cycles, not {instructions + 4 + pairs}. {lockstep.Divergence?.Describe()}\n{source}");
            }

            // The pipeline's own account agrees: that many stalls, each of them a load-use.
            var stats = PipelineStats.Of(records);
            Assert.Equal(pairs, stats.Stalls);
            Assert.Equal(pairs, stats.StallsBy(StallCause.LoadUse));
            Assert.Equal((ulong)instructions, stats.Instructions);
            Assert.Equal(0, stats.Flushes);
        }

        // Guard the test: the programs must really have contained the thing being counted.
        Assert.True(totalPairs > 500, $"only {totalPairs} load-use pairs in 1,000 programs");
    }

    [Fact]
    public void ASlowMultiplierAddsItsExtraCyclesForEachMultiplyAndDivide()
    {
        // The same formula with one more term: a multiply or divide that takes k cycles in EX
        // holds everything behind it for k - 1 of them. The two kinds of wait do not overlap: a
        // load-use pair is two instructions next to each other, and they wait together.
        var random = new SeededRandom(0xF37C_6502);
        var (totalPairs, totalSlow) = (0, 0);

        for (var round = 0; round < 600; round++)
        {
            var cycles = random.Next(2, 9);
            var lines = Enumerable.Range(0, random.Next(1, 60)).Select(_ => Random(random)).ToList();
            var source = SourceOf(lines);
            var pairs = PairsIn(lines);
            var slow = lines.Count(line => line.IsSlow);
            totalPairs += pairs;
            totalSlow += slow;

            var lockstep = new Lockstep(AssemblerTesting.Assemble(source), config: new PipelineConfig { MulDivCycles = cycles });
            var records = new List<CycleRecord>();
            while (!lockstep.Pipeline.IsFinished && lockstep.Divergence is null)
            {
                records.Add(lockstep.Step());
            }

            var instructions = lines.Count + 1;
            var expected = instructions + 4 + pairs + ((cycles - 1) * slow);
            if (lockstep.Divergence is not null || records.Count != expected)
            {
                Assert.Fail(
                    $"seed {random.Seed:X}, round {round}: {instructions} instructions with {pairs} load-use pairs and {slow} " +
                    $"multiplies and divides of {cycles} cycles took {records.Count} cycles, not {expected}. " +
                    $"{lockstep.Divergence?.Describe()}\n{source}");
            }

            var stats = PipelineStats.Of(records);
            Assert.Equal(pairs, stats.StallsBy(StallCause.LoadUse));
            Assert.Equal((cycles - 1) * slow, stats.StallsBy(StallCause.MultiCycle));
            Assert.Equal(pairs + ((cycles - 1) * slow), stats.Stalls);
            Assert.Equal(0, stats.Flushes);
        }

        Assert.True(totalPairs > 300 && totalSlow > 2000, $"only {totalPairs} load-use pairs and {totalSlow} multiplies in 600 programs");
    }

    [Fact]
    public void ARunNeverTakesFewerThanFourCyclesMoreThanItsInstructions()
    {
        // With branches there is no closed form, but there is a floor: nothing leaves WB sooner
        // than four cycles after it was fetched, and at most one instruction is fetched a cycle.
        foreach (var example in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var machine = new PipelineMachine(AssemblerTesting.Assemble(File.ReadAllText(example))) { Recording = false };
            machine.Run(3_000_000);

            Assert.True(machine.IsFinished);
            Assert.True(machine.Cycles >= machine.Hart.InstructionsRetired + 4, Path.GetFileName(example));
        }
    }
}
