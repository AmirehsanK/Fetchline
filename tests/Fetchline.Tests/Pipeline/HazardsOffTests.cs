using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Cli.CommandLine;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// The pipeline with its data-hazard handling switched off. It computes a wrong answer, and the
/// lockstep check says which instruction first went wrong and how. That is the whole point of
/// the mode: it shows what forwarding and stalling are for.
/// </summary>
public class HazardsOffTests
{
    private static readonly PipelineConfig Off = new() { Hazards = HazardHandling.Off };

    private static (Lockstep Lockstep, List<CycleRecord> Records) RunOff(string source, int maxCycles = 5000)
    {
        var lockstep = new Lockstep(AssemblerTesting.Assemble(source), new StringWriter(), config: Off);
        var records = new List<CycleRecord>();
        while (!lockstep.Pipeline.IsFinished && records.Count < maxCycles)
        {
            records.Add(lockstep.Step());
        }

        return (lockstep, records);
    }

    [Fact]
    public void OnlyThisConfigurationIsNotCorrect()
    {
        Assert.False(Off.IsCorrect);
        Assert.True(PipelineConfig.Default.IsCorrect);
        Assert.True(new PipelineConfig { Hazards = HazardHandling.StallOnly }.IsCorrect);
    }

    [Theory]
    [InlineData("li a0, 5\naddi a1, a0, 1", 1u, 2ul)]              // one behind: reads the old a0
    [InlineData("li a0, 5\nnop\naddi a1, a0, 1", 1u, 3ul)]         // two behind: still too early
    public void ACloseConsumerReadsAStaleValue(string source, uint wrong, ulong instruction)
    {
        var (lockstep, _) = RunOff(source);

        Assert.Equal(wrong, lockstep.Pipeline.Hart.X[11]);
        Assert.Equal(6u, lockstep.Reference.Hart.X[11]);
        Assert.NotNull(lockstep.Divergence);
        Assert.Equal(instruction, lockstep.Divergence.Index);
        Assert.Equal((11, 1u), (lockstep.Divergence.Pipeline.Register, lockstep.Divergence.Pipeline.Value));
        Assert.Equal((11, 6u), (lockstep.Divergence.Reference.Register, lockstep.Divergence.Reference.Value));
    }

    [Fact]
    public void AConsumerThreeBehindIsStillRight()
    {
        // The register file writes before it reads, and that is not something that can be
        // switched off: three behind, the value is simply there.
        var (lockstep, _) = RunOff("li a0, 5\nnop\nnop\naddi a1, a0, 1");

        Assert.Null(lockstep.Divergence);
        Assert.Equal(6u, lockstep.Pipeline.Hart.X[11]);
    }

    [Fact]
    public void ALoadedValueIsMissedToo()
    {
        var (lockstep, _) = RunOff(".data\nv: .word 9\n.text\nlui s0, 0x10000\nnop\nnop\nnop\nlw a0, 0(s0)\naddi a1, a0, 1");

        Assert.Equal(1u, lockstep.Pipeline.Hart.X[11]);
        Assert.Equal(
            "instruction 6, in cycle 10, 'addi a1, a0, 1' at 0x00000014: " +
            "the pipeline wrote a1 = 0x00000001, the reference machine wrote a1 = 0x0000000a",
            lockstep.Divergence!.Describe());
    }

    [Fact]
    public void NothingStallsAndNothingIsForwarded()
    {
        var (lockstep, records) = RunOff("""
            .data
            v: .word 9
            .text
                lui  s0, 0x10000
                lw   a0, 0(s0)
                add  a1, a0, a0
                sub  a2, a1, a0
                sw   a2, 4(s0)
            """);

        var stats = PipelineStats.Of(records);
        Assert.Equal((0, 0), (stats.Stalls, stats.Forwards));
        Assert.Equal(5 + 4, records.Count);
        Assert.DoesNotContain(records, record => record.Decode.State == Occupancy.Held);
        Assert.NotNull(lockstep.Divergence);
    }

    [Fact]
    public void ControlHazardsAreStillHandled()
    {
        // Only data hazards are switched off. A taken branch still throws away what was fetched
        // behind it, and a system call still restarts what is behind it; with every value far
        // enough from its use, the program is right.
        var (lockstep, records) = RunOff("""
                li   t0, 1
                li   a7, 1
                li   a0, 7
                nop
                nop
                bnez t0, over
                li   a0, 9
                li   a0, 9
            over:
                ecall
                li   a1, 3
            """);

        Assert.Null(lockstep.Divergence);
        Assert.Equal("7", lockstep.Pipeline.Hart.Output.ToString());
        Assert.Equal((7u, 3u), (lockstep.Pipeline.Hart.X[10], lockstep.Pipeline.Hart.X[11]));
        Assert.Equal(1, PipelineStats.Of(records).FlushesBy(FlushCause.Branch));
    }

    [Fact]
    public void AfterTheFirstWrongValueNothingMoreIsCompared()
    {
        var (lockstep, _) = RunOff("li a0, 5\naddi a1, a0, 1\naddi a2, a1, 1\naddi a3, a2, 1");

        Assert.Equal(2ul, lockstep.Divergence!.Index);
        Assert.Equal(2ul, lockstep.Compared);
        Assert.True(lockstep.Pipeline.IsFinished);     // the pipeline itself ran to the end
    }

    [Fact]
    public void AWholeProgramComputesTheWrongAnswer()
    {
        // The sum of 1 to 10 is 55. With nobody watching the hazards, two things go wrong, and
        // both can be worked out by hand. The first "add" reads the counter before "li t0, 1"
        // has written it, so it adds 0 where it should add 1. And the branch at the foot of
        // the loop reads the counter before the "addi" just ahead of it has landed, so it is
        // always one behind and the loop goes round an eleventh time, adding 11.
        // 55 - 1 + 11 = 65, in three more instructions than the right answer takes.
        var (lockstep, _) = RunOff(File.ReadAllText(Repo.PathOf("examples", "sum.s")));
        var printed = lockstep.Pipeline.Hart.Output.ToString();

        Assert.NotNull(lockstep.Divergence);
        Assert.True(lockstep.Pipeline.IsFinished);
        Assert.Equal("65\n", printed);
        Assert.Equal(43ul, lockstep.Pipeline.Hart.InstructionsRetired);
        Assert.Equal(
            "instruction 4, in cycle 8, 'add a0, a0, t0' at 0x0000000c: " +
            "the pipeline wrote a0 = 0x00000000, the reference machine wrote a0 = 0x00000001",
            lockstep.Divergence.Describe());

        var reference = new Fetchline.Core.Machine.ReferenceMachine(lockstep.Pipeline.Hart.Program, new StringWriter());
        reference.Run();
        Assert.Equal("55\n", reference.Hart.Output.ToString());
        Assert.Equal(40ul, reference.Hart.InstructionsRetired);
    }

    [Fact]
    public void RandomProgramsAlmostAlwaysGoWrongAndTheFirstDifferenceIsAlwaysARealOne()
    {
        // Whatever the divergence report says must be true: the two records it shows are really
        // different, and every instruction before it really matched.
        string[] registers = ["a0", "a1", "a2", "t0", "t1"];
        var random = new SeededRandom(0xF37C_6201);
        var wrong = 0;

        for (var round = 0; round < 300; round++)
        {
            var lines = Enumerable.Range(0, 30).Select(_ => random.Chance(40)
                ? $"addi {random.Pick(registers)}, {random.Pick(registers)}, {random.Next(1, 9)}"
                : $"add {random.Pick(registers)}, {random.Pick(registers)}, {random.Pick(registers)}");
            var (lockstep, _) = RunOff(string.Join('\n', lines));

            if (lockstep.Divergence is { } divergence)
            {
                wrong++;
                Assert.NotEqual(divergence.Pipeline, divergence.Reference);
                Assert.Equal(divergence.Index, lockstep.Compared);
                Assert.Equal(divergence.Pipeline.Pc, divergence.Reference.Pc);
            }
            else
            {
                Assert.Equal(lockstep.Reference.Hart.X, lockstep.Pipeline.Hart.X);
            }
        }

        Assert.True(wrong > 250, $"only {wrong} of 300 random programs went wrong with hazard handling off");
    }

    [Fact]
    public void TheTraceNamesTheFirstWrongValue()
    {
        var trace = RunOk("trace", Example("forwarding.s"), "--hazards", "off");

        Golden.Check("forwarding-off.trace.txt", trace);
        Assert.EndsWith(
            " first wrong value: instruction 3, 'or x13, x6, x2', in cycle 7: " +
            "the pipeline wrote x13 = 0x7ffffff0, the reference machine wrote x13 = 0x0fffffec\n",
            trace);
    }

    [Fact]
    public void TheTraceCanBeDrawnWithStallsOnly()
    {
        var trace = RunOk("trace", Example("forwarding.s"), "--hazards", "stall");

        Golden.Check("forwarding-stall.trace.txt", trace);
        Assert.Contains("no forwarding: and (ID) waits for x2 until sub (EX) reaches WB", trace);
        Assert.DoesNotContain("first wrong value", trace);
    }

    [Fact]
    public void AnUnknownWayOfHandlingHazardsIsRefused()
    {
        var (exitCode, _, error) = Run("trace", Example("forwarding.s"), "--hazards", "magic");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("magic", error);
    }

    [Fact]
    public void TheDescriptionCoversEveryKindOfDifference()
    {
        // Through the visualizer's own words, with the program's own names for things.
        var program = AssemblerTesting.Assemble("start:\n  add x10, x10, x11\n  sw x10, 0(x5)\n  beqz x10, start\nend:");
        var layout = Fetchline.Viz.Staircase.StaircaseLayout.Build([]);
        var explainer = new Fetchline.Viz.Explain.Explainer(
            layout, new Fetchline.Viz.InstructionLabels(program), Fetchline.Viz.Explain.EnglishMessages.Instance);

        var add = new Commit { Pc = 0, Instruction = Fetchline.Core.Isa.Decoder.Decode(0x00B50533), Register = 10, Value = 5, NextPc = 4 };
        var store = new Commit { Pc = 4, Instruction = Fetchline.Core.Isa.Decoder.Decode(0x00A2A023), StoreBytes = 4, StoreAddress = 0x100, StoreValue = 9, NextPc = 8 };
        var branch = new Commit { Pc = 8, Instruction = Fetchline.Core.Isa.Decoder.Decode(0xFE050CE3), NextPc = 0 };
        var end = new Commit { Pc = 12, NextPc = 12, Stop = StopReason.EndOfProgram };

        Assert.Equal(
            "first wrong value: instruction 1, 'add x10, x10, x11', in cycle 5: the pipeline wrote x10 = 0x00000005, the reference machine wrote x10 = 0x00000007",
            explainer.Describe(new Divergence(1, 5, add, add with { Value = 7 })));
        Assert.Equal(
            "first wrong value: instruction 1, 'add x10, x10, x11', in cycle 5: the pipeline wrote no register, the reference machine wrote x10 = 0x00000005",
            explainer.Describe(new Divergence(1, 5, add with { Register = 0, Value = 0 }, add)));
        Assert.Equal(
            "first wrong value: instruction 2, 'sw x10, 0(x5)', in cycle 6: the pipeline stored 0x00000009 -> 0x00000100, the reference machine stored 0x00000008 -> 0x00000100",
            explainer.Describe(new Divergence(2, 6, store, store with { StoreValue = 8 })));
        Assert.Equal(
            "first wrong value: instruction 3, 'beqz x10, start', in cycle 7: the pipeline went on to start, the reference machine to end",
            explainer.Describe(new Divergence(3, 7, branch, branch with { NextPc = 12 })));
        Assert.Equal(
            "first wrong value: instruction 4, the end of the program, in cycle 9: the pipeline ran 'add x10, x10, x11' where the reference machine ran the end of the program",
            explainer.Describe(new Divergence(4, 9, add, end)));
    }
}
