using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// A pipeline with no forwarding paths. The only way to a value is the register file, so an
/// instruction waits in ID until the one producing its value is in WB.
/// </summary>
public class StallOnlyTests
{
    private static readonly PipelineConfig StallOnly = new() { Hazards = HazardHandling.StallOnly };

    private static PipelineRun Run(string source)
    {
        var lockstep = new Lockstep(AssemblerTesting.Assemble(source), new StringWriter(), config: StallOnly);
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished && records.Count < 200_000)
        {
            records.Add(lockstep.Step());
        }

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.True(lockstep.Pipeline.IsFinished);
        return new PipelineRun(lockstep.Pipeline, records, lockstep.Pipeline.Hart.Output.ToString()!);
    }

    [Theory]
    [InlineData("li a0, 5\naddi a1, a0, 1", "IF ID ID ID EX MEM WB", 2)]               // one behind: two cycles
    [InlineData("li a0, 5\nnop\naddi a1, a0, 1", "IF ID ID EX MEM WB", 1)]             // two behind: one cycle
    [InlineData("li a0, 5\nnop\nnop\naddi a1, a0, 1", "IF ID EX MEM WB", 0)]           // three behind: none
    public void AConsumerWaitsUntilItsProducerIsInWriteBack(string source, string row, int stalls)
    {
        var run = Run(source);
        var consumer = (ulong)source.Split('\n').Length;

        Assert.Equal(row, run.Row(consumer));
        Assert.Equal(6u, run["a1"]);
        Assert.Equal(stalls, PipelineStats.Of(run.Records).StallsBy(StallCause.DataHazard));

        // The consumer leaves ID in the very cycle the producer is in WB: a write is read at once.
        Assert.Equal(run.CycleOf(1, Stage.WriteBack), run.CycleOf(consumer, Stage.Execute) - 1);
    }

    [Fact]
    public void TheHazardUnitSaysWhatItIsWaitingForAndWhereThatIs()
    {
        var run = Run("li a0, 5\naddi a1, a0, 1");
        var stalls = run.Records.SelectMany(r => r.Events.OfType<StallEvent>().Select(e => (r.Cycle, Event: e))).ToList();

        Assert.Equal(
            [
                (3ul, new StallEvent(2, StallCause.DataHazard, Stage.Decode, Register: 10, Producer: 1, Stage.Execute)),
                (4ul, new StallEvent(2, StallCause.DataHazard, Stage.Decode, Register: 10, Producer: 1, Stage.Memory)),
            ],
            stalls);
    }

    [Fact]
    public void NothingIsEverForwarded()
    {
        var run = Run(File.ReadAllText(Repo.PathOf("examples", "bubble-sort.s")));

        Assert.DoesNotContain(run.Records.SelectMany(r => r.Events), e => e is ForwardEvent);
        Assert.Equal(0, PipelineStats.Of(run.Records).Forwards);
        Assert.Equal("1 2 3 5 7 8 9 \n", run.Output);
    }

    [Fact]
    public void TheNearerOfTwoProducersIsTheOneWaitedFor()
    {
        // Both instructions ahead write a0. The value wanted is the newer one, from the nearer.
        var run = Run("li a0, 1\nli a0, 2\naddi a1, a0, 0");

        Assert.Equal(2u, run["a1"]);
        Assert.All(
            run.Records.SelectMany(r => r.Events.OfType<StallEvent>()),
            stall => Assert.Equal(2ul, stall.Producer));
    }

    [Fact]
    public void ALoadIsWaitedForLikeAnythingElse()
    {
        var run = Run(".data\nv: .word 9\n.text\nlui s0, 0x10000\nnop\nnop\nnop\nlw a0, 0(s0)\naddi a1, a0, 1");

        Assert.Equal("IF ID ID ID EX MEM WB", run.Row(6));
        Assert.Equal(10u, run["a1"]);
        Assert.Equal(0, PipelineStats.Of(run.Records).StallsBy(StallCause.LoadUse));
    }

    [Fact]
    public void AFieldThatIsNotARegisterIsNotWaitedFor()
    {
        var run = Run("li x5, 1\nslli x6, x7, 5\nli x5, 2\naddi x6, x7, 5\nli x5, 3\nlui x6, 5");

        Assert.Equal(0, PipelineStats.Of(run.Records).Stalls);
        Assert.Equal(6 + 4, run.Cycles);
    }

    [Fact]
    public void StraightLineCodeTakesWhatTheDistanceFormulaSays()
    {
        // Without forwarding there is still a closed form. Let d(i) be the cycle instruction i
        // leaves ID. It cannot leave before the one ahead has (d(i - 1) + 1), nor before each of
        // its producers is in WB, three cycles after that producer left ID (d(p) + 3). The run
        // ends three cycles after the last instruction leaves ID.
        string[] registers = ["a0", "a1", "a2", "t0", "t1", "zero"];
        var random = new SeededRandom(0xF37C_6101);

        for (var round = 0; round < 500; round++)
        {
            var count = random.Next(1, 50);
            var lines = new List<(string Text, string? Writes, string[] Reads)> { ("lui s0, 0x10000", "s0", []) };
            for (var i = 0; i < count; i++)
            {
                string R() => random.Pick(registers);
                var (rd, rs1, rs2, offset) = (R(), R(), R(), 4 * random.Next(0, 7));
                lines.Add(random.Next(0, 5) switch
                {
                    0 => ($"lw {rd}, {offset}(s0)", rd, ["s0"]),
                    1 => ($"sw {rs2}, {offset}(s0)", null, ["s0", rs2]),
                    2 => ($"addi {rd}, {rs1}, {random.Next(-9, 9)}", rd, [rs1]),
                    3 => ($"lui {rd}, {random.Next(0, 99)}", rd, []),
                    _ => ($"add {rd}, {rs1}, {rs2}", rd, [rs1, rs2]),
                });
            }

            var leavesDecode = new long[lines.Count];
            var lastWriter = new Dictionary<string, int>();
            for (var i = 0; i < lines.Count; i++)
            {
                long earliest = i == 0 ? 2 : leavesDecode[i - 1] + 1;
                foreach (var register in lines[i].Reads.Where(r => r != "zero"))
                {
                    if (lastWriter.TryGetValue(register, out var producer))
                    {
                        earliest = Math.Max(earliest, leavesDecode[producer] + 3);
                    }
                }

                leavesDecode[i] = earliest;
                if (lines[i].Writes is { } written and not "zero")
                {
                    lastWriter[written] = i;
                }
            }

            var source = ".data\ncells: .word 1, 2, 3, 4, 5, 6, 7, 8\n.text\n" + string.Join('\n', lines.Select(l => l.Text));
            var run = Run(source);

            if (run.Cycles != leavesDecode[^1] + 3)
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: {run.Cycles} cycles, the formula says {leavesDecode[^1] + 3}\n{source}");
            }
        }
    }

    [Fact]
    public void EveryExampleGivesTheSameAnswerMoreSlowly()
    {
        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var name = Path.GetFileName(path);
            var program = AssemblerTesting.Assemble(File.ReadAllText(path));

            // The sieve runs for hundreds of thousands of cycles, so nothing is kept but the end.
            var fast = new PipelineMachine(program, new StringWriter()) { Recording = false };
            fast.Run(5_000_000);

            var slow = new Lockstep(program, new StringWriter(), config: StallOnly);
            slow.Pipeline.Recording = false;
            slow.Run(5_000_000);

            Assert.True(slow.Divergence is null, $"{name}: {slow.Divergence?.Describe()}");
            Assert.True(fast.IsFinished && slow.Pipeline.IsFinished, name);
            Assert.Equal(fast.Hart.Output.ToString(), slow.Pipeline.Hart.Output.ToString());
            Assert.Equal(fast.Hart.X, slow.Pipeline.Hart.X);
            Assert.True(slow.Pipeline.Cycles >= fast.Cycles, name);
        }
    }

    [Fact]
    public void TheLogExplainsTheWaitInWords()
    {
        var program = AssemblerTesting.Assemble("sub x2, x3, x1\nand x12, x2, x5");
        var machine = new PipelineMachine(program, config: StallOnly);
        var records = new List<CycleRecord>();
        while (!machine.IsFinished)
        {
            records.Add(machine.Step());
        }

        var trace = Fetchline.Viz.Export.AsciiTrace.Write(program, records, Fetchline.Viz.Explain.EnglishMessages.Instance);

        Assert.Equal(
            new string(' ', 27) + "1    2    3    4    5    6    7    8\n" +
            " 0x00 sub  x2, x3, x1" + new string(' ', 6) + "IF   ID   EX   MEM  WB\n" +
            " 0x04 and  x12, x2, x5" + new string(' ', 10) + "IF   ID   ID   ID   EX   MEM  WB\n" +
            "\n" +
            " c3  stall    no forwarding: and (ID) waits for x2 until sub (EX) reaches WB\n" +
            " c4  stall    no forwarding: and (ID) waits for x2 until sub (MEM) reaches WB\n" +
            " 2 instructions, 8 cycles, CPI 4.00, 2 stalls, 0 forwards\n",
            trace);
    }
}
