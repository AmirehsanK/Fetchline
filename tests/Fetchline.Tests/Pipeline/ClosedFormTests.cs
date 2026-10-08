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

    /// <summary>One generated instruction: its text, and what it reads and writes.</summary>
    private sealed record Line(string Text, string? Writes, bool IsLoad, string[] Reads);

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
            9 => new Line($"mul {rd}, {rs1}, {rs2}", rd, false, [rs1, rs2]),
            _ => new Line($"add {rd}, {rs1}, {rs2}", rd, false, [rs1, rs2]),
        };
    }

    [Fact]
    public void StraightLineCodeTakesNPlusFourCyclesPlusOnePerLoadUsePair()
    {
        var random = new SeededRandom(0xF37C_5901);
        var totalPairs = 0;

        for (var round = 0; round < 1000; round++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 60)).Select(_ => Random(random)).ToList();
            var source = ".data\ncells: .word 1, 2, 3, 4, 5, 6, 7, 8\n.text\nlui s0, 0x10000\n"
                + string.Join('\n', lines.Select(line => line.Text));

            // A pair is a load into a real register followed at once by an instruction that
            // reads that register. The first instruction (lui s0) is not a load.
            var pairs = 0;
            for (var i = 0; i + 1 < lines.Count; i++)
            {
                if (lines[i] is { IsLoad: true, Writes: not "zero" } load && lines[i + 1].Reads.Contains(load.Writes))
                {
                    pairs++;
                }
            }

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
