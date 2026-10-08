using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Machine;
using static Fetchline.Tests.Pipeline.PipelineTesting;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// The pipeline on code that has no hazards: instructions that do not depend on each other. This
/// is the kernel alone: five stages, four latches, one instruction entering per cycle.
/// </summary>
public class KernelTests
{
    private const string Independent = """
        li   a0, 1
        li   a1, 2
        li   a2, 3
        li   a3, 4
        li   a4, 5
        li   a5, 6
        """;

    [Fact]
    public void EachInstructionSpendsOneCycleInEachStage()
    {
        var run = RunToEnd(Independent);

        for (ulong seq = 1; seq <= 6; seq++)
        {
            Assert.Equal("IF ID EX MEM WB", run.Row(seq));
            Assert.Equal(seq, run.CycleOf(seq, Stage.Fetch));           // one enters per cycle
            Assert.Equal(seq + 4, run.CycleOf(seq, Stage.WriteBack));   // and leaves four cycles later
        }
    }

    [Fact]
    public void ARunOfNInstructionsTakesNPlusFourCycles()
    {
        var run = RunToEnd(Independent);

        Assert.Equal(6 + 4, run.Cycles);
        Assert.Equal(10ul, run.Machine.Cycles);
        Assert.Equal(6ul, run.Machine.Hart.InstructionsRetired);
        Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u], new[] { "a0", "a1", "a2", "a3", "a4", "a5" }.Select(r => run[r]));
    }

    [Fact]
    public void TheStagesOfOneCycleHoldFiveDifferentInstructions()
    {
        var run = RunToEnd(Independent);
        var fifth = run.Records[4];     // cycle 5: the first cycle with every stage full

        Assert.Equal(5ul, fifth.Cycle);
        Assert.Equal(
            [5ul, 4ul, 3ul, 2ul, 1ul],
            Enum.GetValues<Stage>().Select(stage => fifth[stage].Seq));
        Assert.Equal(
            [16u, 12u, 8u, 4u, 0u],
            Enum.GetValues<Stage>().Select(stage => fifth[stage].Pc));
        Assert.All(Enum.GetValues<Stage>(), stage => Assert.Equal(Occupancy.Normal, fifth[stage].State));

        // In the first cycle only fetch has anything; in the last, only write-back.
        Assert.Equal(
            [true, false, false, false, false],
            Enum.GetValues<Stage>().Select(stage => run.Records[0][stage].HasInstruction));
        Assert.Equal(
            [false, false, false, false, true],
            Enum.GetValues<Stage>().Select(stage => run.Last[stage].HasInstruction));
    }

    [Fact]
    public void AnInstructionCommitsInTheCycleItLeavesWriteBack()
    {
        var run = RunToEnd(Independent);

        // Nothing has reached WB in the first four cycles.
        Assert.All(run.Records.Take(4), record => Assert.Null(record.Commit));
        for (var i = 0; i < 6; i++)
        {
            var commit = run.Records[4 + i].Commit;
            Assert.NotNull(commit);
            Assert.Equal(((uint)(4 * i), 10 + i, (uint)(i + 1)), (commit.Value.Pc, commit.Value.Register, commit.Value.Value));
        }
    }

    [Fact]
    public void TheRunEndsInTheCycleTheLastInstructionLeavesWriteBack()
    {
        var run = RunToEnd(Independent);

        Assert.All(run.Records.SkipLast(1), record => Assert.Equal(StopReason.None, record.Stop));
        Assert.NotNull(run.Last.Commit);                        // the last instruction, in WB
        Assert.Equal(StopReason.EndOfProgram, run.Last.End!.Value.Stop);
        Assert.Equal(24u, run.Last.End.Value.Pc);
        Assert.True(run.Machine.IsFinished);
        Assert.Throws<InvalidOperationException>(() => run.Machine.Step());
    }

    [Fact]
    public void TheRecordsAreTheOnesTheReferenceMachineGives()
    {
        const string source = """
            .data
            cell: .word 0
            .text
                lui  s0, 0x10000
                li   t0, 42
                li   t1, -7
                li   t2, 3
                li   t3, 9
                sw   t0, 0(s0)
                mul  a0, t1, t2
                sub  a1, t3, t2
                sltu a2, t2, t3
                lw   a3, 0(s0)
            """;

        // Every source register here was written at least three instructions earlier, so the
        // value is already in the register file when it is read.
        Assert.Equal(MachineTesting.RunToEnd(source).Commits, RunToEnd(source).Commits);
    }

    [Fact]
    public void AValueWrittenInWriteBackCanBeReadInDecodeInTheSameCycle()
    {
        // The consumer is three behind its producer: it is in ID exactly when the producer is in
        // WB. The register file writes before it reads, so that is early enough.
        var run = RunToEnd("li a0, 5\nnop\nnop\naddi a1, a0, 1");

        Assert.Equal(run.CycleOf(1, Stage.WriteBack), run.CycleOf(4, Stage.Decode));
        Assert.Equal(6u, run["a1"]);
    }

    [Fact]
    public void WithNoHazardHandlingACloserConsumerReadsAStaleValue()
    {
        // Two behind, the consumer is in ID one cycle before the producer writes: it reads the
        // old value. This is the data hazard; forwarding is what will fix it.
        var run = RunToEnd("li a0, 5\nnop\naddi a1, a0, 1");

        Assert.Equal(run.CycleOf(1, Stage.WriteBack), run.CycleOf(3, Stage.Decode) + 1);
        Assert.Equal(1u, run["a1"]);
    }

    [Fact]
    public void AnEmptyProgramEndsInItsFirstCycle()
    {
        var run = RunToEnd(string.Empty);

        Assert.Equal(1, run.Cycles);
        Assert.Null(run.Last.Commit);
        Assert.Equal(StopReason.EndOfProgram, run.Last.Stop);
        Assert.All(Enum.GetValues<Stage>(), stage => Assert.False(run.Last[stage].HasInstruction));
    }

    [Fact]
    public void RunStopsAtItsBudget()
    {
        var machine = new PipelineMachine(Asm.AssemblerTesting.Assemble(Independent));

        var record = machine.Run(maxCycles: 3);

        Assert.Equal(3ul, record!.Cycle);
        Assert.False(machine.IsFinished);
        Assert.Equal(StopReason.EndOfProgram, machine.Run()!.Stop);
        Assert.Equal(10ul, machine.Cycles);
    }
}
