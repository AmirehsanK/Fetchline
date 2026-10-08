using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;
using Fetchline.Viz.Explain;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// A multiply or a divide that takes several cycles in EX. While it works it keeps its stage,
/// what is behind it waits, and what is ahead of it drains away. It is the one stall in this
/// pipeline that no hazard causes.
/// </summary>
public class MultiCycleTests
{
    private static PipelineConfig Taking(int cycles) => new() { MulDivCycles = cycles };

    private static PipelineRun Go(string source, PipelineConfig config)
    {
        var lockstep = new Lockstep(AssemblerTesting.Assemble(source), new StringWriter(), config: config);
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished && records.Count < 200_000)
        {
            records.Add(lockstep.Step());
        }

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.True(lockstep.Pipeline.IsFinished);
        return new PipelineRun(lockstep.Pipeline, records, lockstep.Pipeline.Hart.Output.ToString()!);
    }

    private static List<(ulong Cycle, T Event)> Of<T>(PipelineRun run)
        where T : PipelineEvent =>
        [.. run.Records.SelectMany(record => record.Events.OfType<T>().Select(e => (record.Cycle, e)))];

    /// <summary>A stage name written out a number of times: "EX EX EX".</summary>
    private static string Times(string stage, int count) => string.Join(' ', Enumerable.Repeat(stage, count));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(32)]
    public void AMultiplyKeepsExecuteAndWhatIsBehindItWaits(int cycles)
    {
        var run = Go(
            """
            li   a0, 6                   # 1
            li   a1, 7                   # 2
            mul  a2, a0, a1              # 3
            addi a3, a2, 1               # 4: in ID while the multiply works
            xori a4, a3, 1               # 5: in IF
            nop                          # 6: not fetched until the multiply is done
            """,
            Taking(cycles));

        Assert.Equal($"IF ID {Times("EX", cycles)} MEM WB", run.Row(3));
        Assert.Equal($"IF {Times("ID", cycles)} EX MEM WB", run.Row(4));
        Assert.Equal($"{Times("IF", cycles)} ID EX MEM WB", run.Row(5));
        Assert.Equal("IF ID EX MEM WB", run.Row(6));

        // What is ahead of the multiply is not held up by it.
        Assert.Equal("IF ID EX MEM WB", run.Row(1));
        Assert.Equal("IF ID EX MEM WB", run.Row(2));

        Assert.Equal((42u, 43u, 42u), (run["a2"], run["a3"], run["a4"]));
        Assert.Equal(6 + 4 + (cycles - 1), run.Cycles);

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal(cycles - 1, stats.StallsBy(StallCause.MultiCycle));
        Assert.Equal(cycles - 1, stats.Stalls);
        Assert.Equal(0, stats.Flushes);
    }

    [Fact]
    public void EveryMultiplyAndDivideTakesTheCyclesAndNoOtherInstructionDoes()
    {
        const string before = ".data\nv: .word 5\n.text\nli a0, -100\nli a1, 7\nnop\nnop\n";

        // The list is the instruction table's own: whatever the M extension adds.
        var slow = InstructionSet.All.Where(row => row.Extension == Extension.M).Select(row => row.Mnemonic).ToList();
        Assert.Equal(["div", "divu", "mul", "mulh", "mulhsu", "mulhu", "rem", "remu"], slow.Order(StringComparer.Ordinal));

        foreach (var mnemonic in slow)
        {
            var run = Go($"{before}{mnemonic} a2, a0, a1\nend:", Taking(3));
            Assert.True("IF ID EX EX EX MEM WB" == run.Row(5), $"{mnemonic}: {run.Row(5)}");
        }

        string[] quick =
        [
            "add a2, a0, a1", "sll a2, a0, a1", "sltu a2, a0, a1", "srai a2, a0, 3", "lui a2, 7", "auipc a2, 7",
            "lw a2, 0(gp)", "sb a0, 0(gp)", "beq a0, a1, end", "bne a0, a1, end", "jal a2, end", "csrr a2, mscratch",
        ];
        foreach (var instruction in quick)
        {
            var run = Go($"{before}{instruction}\nend:", Taking(3));
            Assert.True("IF ID EX MEM WB" == run.Row(5), $"{instruction}: {run.Row(5)}");
        }
    }

    [Fact]
    public void TheOperandsAreTakenInTheFirstCycleAndKept()
    {
        var run = Go("li a0, 6\nli a1, 7\nmul a2, a0, a1", Taking(8));

        // Both operands are forwarded, once, in the multiply's first cycle in EX. Nothing could
        // be forwarded later: the two instructions they came from are gone long before it ends.
        Assert.Equal(
            [
                (5ul, new ForwardEvent(3, ForwardSource.MemWb, Operand.A, Register: 10, Value: 6, Producer: 1)),
                (5ul, new ForwardEvent(3, ForwardSource.ExMem, Operand.B, Register: 11, Value: 7, Producer: 2)),
            ],
            Of<ForwardEvent>(run));

        var lastCycleInExecute = run.Records.Last(record => record.Execute.Seq == 3).Cycle;
        Assert.Equal(12ul, lastCycleInExecute);
        Assert.Equal(6ul, run.CycleOf(2, Stage.WriteBack));
        Assert.Equal(42u, run["a2"]);
    }

    [Fact]
    public void TheStallIsSaidEachCycleByTheInstructionThatIsStillWorking()
    {
        var run = Go("li a0, 60\nli a1, 7\ndivu a2, a0, a1\nnop\nnop", Taking(4));

        // It waits for no register and no other instruction: only for itself.
        Assert.Equal(
            [
                (5ul, new StallEvent(3, StallCause.MultiCycle, Stage.Execute, Register: 0, Producer: 0, Remaining: 3)),
                (6ul, new StallEvent(3, StallCause.MultiCycle, Stage.Execute, Register: 0, Producer: 0, Remaining: 2)),
                (7ul, new StallEvent(3, StallCause.MultiCycle, Stage.Execute, Register: 0, Producer: 0, Remaining: 1)),
            ],
            Of<StallEvent>(run));

        foreach (var record in run.Records.Where(record => record.Cycle is >= 5 and <= 7))
        {
            Assert.Equal((Occupancy.Held, 3ul), (record.Execute.State, record.Execute.Seq));
            Assert.Equal((Occupancy.Held, 4ul), (record.Decode.State, record.Decode.Seq));
            Assert.Equal((Occupancy.Held, 5ul), (record.Fetch.State, record.Fetch.Seq));
        }

        // In its last cycle it is an instruction like any other, and so are the two behind it.
        var last = run.Records[7];
        Assert.Equal((Occupancy.Normal, 3ul), (last.Execute.State, last.Execute.Seq));
        Assert.Equal((Occupancy.Normal, 4ul), (last.Decode.State, last.Decode.Seq));
        Assert.Equal((Occupancy.Normal, 5ul), (last.Fetch.State, last.Fetch.Seq));
        Assert.Equal(8u, run["a2"]);
    }

    [Fact]
    public void BubblesFollowTheInstructionsAheadThroughMemoryAndWriteBack()
    {
        var run = Go("li a0, 6\nli a1, 7\nmul a2, a0, a1\nnop", Taking(4));

        // The multiply is in EX from cycle 5 to cycle 8. In the first of them the instruction
        // ahead is still in MEM; after that nothing comes out of EX until the multiply does.
        Assert.Equal(2ul, run.Records[4].Memory.Seq);
        Assert.All(run.Records.Skip(5).Take(3), record => Assert.False(record.Memory.HasInstruction));
        Assert.Equal(3ul, run.Records[8].Memory.Seq);

        Assert.Equal(2ul, run.Records[5].WriteBack.Seq);
        Assert.All(run.Records.Skip(6).Take(3), record => Assert.False(record.WriteBack.HasInstruction));
        Assert.Equal(3ul, run.Records[9].WriteBack.Seq);

        // Nothing commits in a cycle with a bubble in WB.
        Assert.All(run.Records.Skip(6).Take(3), record => Assert.Null(record.Commit));
    }

    [Fact]
    public void AMultiplyThrownAwayInItsFirstCycleStartsAgainFromTheBeginning()
    {
        var run = Go(
            """
            li   a0, 6                   # 1
            li   a1, 7                   # 2
            csrw mscratch, a0            # 3: a system instruction, which takes effect in MEM
            mul  a2, a0, a1              # 4: in EX then, and thrown away; fetched again as 7
            nop                          # 5
            nop                          # 6
            """,
            Taking(4));

        Assert.Equal("IF ID EX xx", run.Row(4));
        Assert.Equal("IF ID EX EX EX EX MEM WB", run.Row(7));
        Assert.Equal(42u, run["a2"]);

        // The cycle in which it was thrown away is a flush, not a stall: three stalls, not four.
        var stats = PipelineStats.Of(run.Records);
        Assert.Equal(3, stats.StallsBy(StallCause.MultiCycle));
        Assert.Equal(1, stats.FlushesBy(FlushCause.System));
        Assert.Equal(6 + 4 + 3 + 3, run.Cycles);
    }

    [Fact]
    public void AWaitForALoadAndAMultiplyAddUp()
    {
        var run = Go(
            """
            .data
            v: .word 6
            .text
                lui  s0, 0x10000         # 1
                lw   a0, 0(s0)           # 2
                mul  a1, a0, a0          # 3: waits a cycle for the load, then works for three
                addi a2, a1, 1           # 4
            """,
            Taking(3));

        Assert.Equal("IF ID ID EX EX EX MEM WB", run.Row(3));
        Assert.Equal("IF IF ID ID ID EX MEM WB", run.Row(4));
        Assert.Equal((36u, 37u), (run["a1"], run["a2"]));
        Assert.Equal(4 + 4 + 1 + 2, run.Cycles);

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal((1, 2, 3), (stats.StallsBy(StallCause.LoadUse), stats.StallsBy(StallCause.MultiCycle), stats.Stalls));

        // The value the load read was forwarded to both operands in the multiply's first cycle.
        Assert.Equal(
            [
                (4ul, new ForwardEvent(2, ForwardSource.ExMem, Operand.A, Register: 8, Value: 0x1000_0000, Producer: 1)),
                (6ul, new ForwardEvent(3, ForwardSource.MemWb, Operand.A, Register: 10, Value: 6, Producer: 2)),
                (6ul, new ForwardEvent(3, ForwardSource.MemWb, Operand.B, Register: 10, Value: 6, Producer: 2)),
                (9ul, new ForwardEvent(4, ForwardSource.ExMem, Operand.A, Register: 11, Value: 36, Producer: 3)),
            ],
            Of<ForwardEvent>(run));
    }

    [Fact]
    public void ABranchDecidedInDecodeWaitsBehindTheMultiplyAndThenForItsResult()
    {
        var config = new PipelineConfig { MulDivCycles = 3, Branches = BranchDecision.Decode };
        var run = Go(
            """
                li   a0, 3               # 1
                li   a1, 4               # 2
                mul  a2, a0, a1          # 3
                beqz a2, skip            # 4: held behind the multiply, then needs what it computed
                li   a3, 1               # 5
            skip:
                nop                      # 6
            """,
            config);

        // Two cycles held because EX is busy, one more because the product is only just being
        // computed, and then the comparator takes it from EX/MEM.
        Assert.Equal("IF ID ID ID ID EX MEM WB", run.Row(4));
        Assert.Equal(
            [
                (5ul, StallCause.MultiCycle, 3ul),
                (6ul, StallCause.MultiCycle, 3ul),
                (7ul, StallCause.BranchOperand, 4ul),
            ],
            Of<StallEvent>(run).Select(stall => (stall.Cycle, stall.Event.Cause, stall.Event.Seq)));
        Assert.Contains(
            (8ul, new ForwardEvent(4, ForwardSource.ExMem, Operand.A, Register: 12, Value: 12, Producer: 3, Stage.Decode)),
            Of<ForwardEvent>(run));
        Assert.Equal(1u, run["a3"]);
        Assert.Equal(6 + 4 + 2 + 1, run.Cycles);
    }

    [Fact]
    public void WithStallingOnlyAConsumerWaitsBehindTheMultiplyAndThenUntilItIsInWriteBack()
    {
        var config = new PipelineConfig { MulDivCycles = 3, Hazards = HazardHandling.StallOnly };
        var run = Go("li a0, 6\nnop\nnop\nnop\nmul a1, a0, a0\naddi a2, a1, 1", config);

        Assert.Equal("IF ID EX EX EX MEM WB", run.Row(5));
        Assert.Equal("IF ID ID ID ID ID EX MEM WB", run.Row(6));
        Assert.Equal(run.CycleOf(5, Stage.WriteBack), run.CycleOf(6, Stage.Execute) - 1);
        Assert.Equal(37u, run["a2"]);

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal((2, 2), (stats.StallsBy(StallCause.MultiCycle), stats.StallsBy(StallCause.DataHazard)));
        Assert.Equal(6 + 4 + 2 + 2, run.Cycles);
    }

    [Fact]
    public void WithStallingOnlyStraightLineCodeTakesWhatTheDistanceFormulaSays()
    {
        // The formula of the stall-only pipeline, with one more term. Let d(i) be the cycle
        // instruction i leaves ID and c(i) the cycles it spends in EX. It cannot leave before
        // the one ahead has left EX (d(i - 1) + c(i - 1)), nor before each of its producers is
        // in WB, which is c(p) + 2 cycles after that producer left ID. The run ends when the
        // last instruction is in WB, c + 2 cycles after it left ID.
        string[] registers = ["a0", "a1", "a2", "t0", "t1", "zero"];
        string[] slow = ["mul", "mulhu", "div", "remu"];
        var random = new SeededRandom(0xF37C_6501);

        for (var round = 0; round < 500; round++)
        {
            var cycles = random.Next(1, 6);
            var count = random.Next(1, 40);
            var lines = new List<(string Text, string? Writes, string[] Reads, int InExecute)> { ("lui s0, 0x10000", "s0", [], 1) };
            for (var i = 0; i < count; i++)
            {
                string R() => random.Pick(registers);
                var (rd, rs1, rs2, offset) = (R(), R(), R(), 4 * random.Next(0, 7));
                lines.Add(random.Next(0, 5) switch
                {
                    0 => ($"lw {rd}, {offset}(s0)", rd, ["s0"], 1),
                    1 => ($"sw {rs2}, {offset}(s0)", null, ["s0", rs2], 1),
                    2 => ($"addi {rd}, {rs1}, {random.Next(-9, 9)}", rd, [rs1], 1),
                    3 => ($"{random.Pick(slow)} {rd}, {rs1}, {rs2}", rd, [rs1, rs2], cycles),
                    _ => ($"add {rd}, {rs1}, {rs2}", rd, [rs1, rs2], 1),
                });
            }

            var leavesDecode = new long[lines.Count];
            var lastWriter = new Dictionary<string, int>();
            for (var i = 0; i < lines.Count; i++)
            {
                long earliest = i == 0 ? 2 : leavesDecode[i - 1] + lines[i - 1].InExecute;
                foreach (var register in lines[i].Reads.Where(r => r != "zero"))
                {
                    if (lastWriter.TryGetValue(register, out var producer))
                    {
                        earliest = Math.Max(earliest, leavesDecode[producer] + lines[producer].InExecute + 2);
                    }
                }

                leavesDecode[i] = earliest;
                if (lines[i].Writes is { } written and not "zero")
                {
                    lastWriter[written] = i;
                }
            }

            var expected = leavesDecode[^1] + lines[^1].InExecute + 2;
            var source = ".data\ncells: .word 1, 2, 3, 4, 5, 6, 7, 8\n.text\n" + string.Join('\n', lines.Select(l => l.Text));
            var run = Go(source, new PipelineConfig { MulDivCycles = cycles, Hazards = HazardHandling.StallOnly });

            if (run.Cycles != expected)
            {
                Assert.Fail(
                    $"seed {random.Seed:X}, round {round}, {cycles} cycles for a multiply: " +
                    $"{run.Cycles} cycles, the formula says {expected}\n{source}");
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(32)]
    public void EveryExampleTakesExactlyTheExtraCyclesOfItsMultipliesAndDivides(int cycles)
    {
        // With forwarding and branches decided in EX, a slow multiplier changes nothing but
        // itself: no other stall is hidden behind it and no guess comes out differently. So the
        // run is longer by the extra cycles of each multiply and divide that completes, and
        // those are counted on the reference machine, which knows nothing of cycles.
        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var name = Path.GetFileName(path);
            var program = AssemblerTesting.Assemble(File.ReadAllText(path));

            var reference = new ReferenceMachine(program, TextWriter.Null);
            ulong multiplies = 0;
            while (!reference.IsFinished)
            {
                var commit = reference.Step();
                multiplies += commit is { Trapped: false, Instruction.Control.IsMulDiv: true } ? 1ul : 0;
            }

            var quick = new PipelineMachine(program, new StringWriter()) { Recording = false };
            quick.Run(5_000_000);

            var slow = new Lockstep(program, new StringWriter(), config: Taking(cycles));
            slow.Pipeline.Recording = false;
            slow.Run(50_000_000);

            Assert.True(slow.Divergence is null, $"{name}: {slow.Divergence?.Describe()}");
            Assert.True(quick.IsFinished && slow.Pipeline.IsFinished, name);
            Assert.Equal(Cli.RunTests.ExpectedOutput(name), slow.Pipeline.Hart.Output.ToString());
            Assert.True(
                quick.Cycles + ((ulong)(cycles - 1) * multiplies) == slow.Pipeline.Cycles,
                $"{name}: {quick.Cycles} cycles with a one-cycle multiplier, {multiplies} multiplies and divides, " +
                $"{slow.Pipeline.Cycles} cycles with a {cycles}-cycle one");
        }
    }

    [Theory]
    [InlineData(HazardHandling.Forwarding, BranchDecision.Decode, Predictor.NotTaken)]
    [InlineData(HazardHandling.StallOnly, BranchDecision.Execute, Predictor.NotTaken)]
    [InlineData(HazardHandling.StallOnly, BranchDecision.Decode, Predictor.TwoBit)]
    [InlineData(HazardHandling.Forwarding, BranchDecision.Execute, Predictor.TwoBit)]
    [InlineData(HazardHandling.Forwarding, BranchDecision.Decode, Predictor.BackwardTaken)]
    public void BuiltAnyOtherWayTheExamplesStillRunInLockstepAndNoLongerThanTheSum(
        HazardHandling hazards, BranchDecision branches, Predictor predictor)
    {
        // Where instructions wait in ID for other reasons, a wait behind the multiplier can be
        // the same cycles as a wait for a value, so the extra cycles are a ceiling and not a sum.
        const int cycles = 4;
        var built = new PipelineConfig { Hazards = hazards, Branches = branches, Predictor = predictor };

        foreach (var path in Directory.GetFiles(Repo.PathOf("examples"), "*.s"))
        {
            var name = Path.GetFileName(path);
            var program = AssemblerTesting.Assemble(File.ReadAllText(path));

            var quick = new PipelineMachine(program, new StringWriter(), config: built) { Recording = false };
            quick.Run(5_000_000);

            var slow = new Lockstep(program, new StringWriter(), config: built with { MulDivCycles = cycles });
            ulong stalls = 0;
            while (!slow.Pipeline.IsFinished && slow.Divergence is null)
            {
                stalls += (ulong)slow.Step().Events.Count(e => e is StallEvent { Cause: StallCause.MultiCycle });
            }

            Assert.True(slow.Divergence is null, $"{name}: {slow.Divergence?.Describe()}");
            Assert.True(quick.IsFinished && slow.Pipeline.IsFinished, name);
            Assert.Equal(Cli.RunTests.ExpectedOutput(name), slow.Pipeline.Hart.Output.ToString());
            Assert.True(slow.Pipeline.Cycles >= quick.Cycles, name);
            Assert.True(slow.Pipeline.Cycles <= quick.Cycles + stalls, $"{name}: {quick.Cycles} + {stalls} < {slow.Pipeline.Cycles}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(65)]
    public void AMultiplierThatTakesNoTimeOrTooMuchIsRefused(int cycles)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new PipelineMachine(AssemblerTesting.Assemble("nop"), config: Taking(cycles)));

        Assert.Contains("from 1 to 64", error.Message);
        Assert.Equal(64, PipelineConfig.MaxMulDivCycles);
    }

    [Fact]
    public void TheTraceDrawsTheWaitAndSaysWhatItIs()
    {
        var trace = RunOk("trace", Example("multiply.s"), "--muldiv", "4");

        // The text of a row is 21 characters at its longest and the cycles start five further on;
        // the mul is fetched in cycle 3 and the addi in cycle 4, and a cycle is five wide.
        Golden.Check("multiply-4.trace.txt", trace);
        Assert.Contains(
            " 0x08 mul  a2, a0, a1" + new string(' ', 5 + 10) + "IF   ID   EX   EX   EX   EX   MEM  WB\n", trace);
        Assert.Contains(
            " 0x0c addi a3, a2, 1" + new string(' ', 6 + 15) + "IF   ID   ID   ID   ID   EX   MEM  WB\n", trace);
        Assert.Contains(
            " 0x10 div  a4, a2, a1" + new string(' ', 5 + 20) + "IF   IF   IF   IF   ID   EX   EX   EX   EX   MEM  WB\n", trace);
        Assert.Contains(" c5   stall    multi-cycle: mul (EX) needs 3 more cycles; what is behind it waits\n", trace);
        Assert.Contains(" c7   stall    multi-cycle: mul (EX) needs 1 more cycle; what is behind it waits\n", trace);
        Assert.Contains(" c12  stall    multi-cycle: div (EX) needs 1 more cycle; what is behind it waits\n", trace);
        Assert.EndsWith(" 6 instructions, 16 cycles, CPI 2.67, 6 stalls, 5 forwards\n", trace);

        // Drawn the textbook way the same program has no stall in it at all. It has one forward
        // more: the sub is in EX while the addi is still in WB, and with the slow divider between
        // them the addi's result had long been in the register file.
        Assert.EndsWith(" 6 instructions, 10 cycles, CPI 1.67, 0 stalls, 6 forwards\n", RunOk("trace", Example("multiply.s")));
    }

    [Fact]
    public void TheSentenceCountsItsCyclesInGoodEnglish()
    {
        var messages = EnglishMessages.Instance;

        Assert.Equal("multi-cycle: div (EX) needs 31 more cycles; what is behind it waits", messages.MultiCycle("div", "EX", 31));
        Assert.Equal("multi-cycle: mul (EX) needs 1 more cycle; what is behind it waits", messages.MultiCycle("mul", "EX", 1));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("many")]
    public void AnImpossibleNumberOfCyclesIsRefusedOnTheCommandLine(string value)
    {
        var (exitCode, _, error) = Run("trace", Example("multiply.s"), "--muldiv", value);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(value, error);
    }
}
