using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;
using static Fetchline.Tests.Pipeline.PipelineTesting;

namespace Fetchline.Tests.Pipeline;

/// <summary>What the pipeline says about itself, cycle by cycle.</summary>
public class EventTests
{
    private const string LoadUse = """
            lw   x4, 0(x2)
            add  x5, x4, x6
            sub  x7, x5, x4
        """;

    private static List<(ulong Cycle, T Event)> Of<T>(PipelineRun run)
        where T : PipelineEvent =>
        [.. run.Records.SelectMany(record => record.Events.OfType<T>().Select(e => (record.Cycle, e)))];

    [Fact]
    public void TheTextbookSequenceSaysWhatItStalledAndForwarded()
    {
        var run = RunToEnd(LoadUse);

        // Cycle 3: the add is in ID, the lw is in EX and will not have x4 until after MEM.
        Assert.Equal(
            [(3ul, new StallEvent(Seq: 2, StallCause.LoadUse, Stage.Decode, Register: 4, Producer: 1))],
            Of<StallEvent>(run));

        // Cycle 5: the add is in EX, the lw is in WB. Cycle 6: the sub is in EX, the add in MEM.
        // The sub's other operand, x4, is not forwarded: the lw wrote it in cycle 5, when the sub
        // was in ID, and the register file writes before it reads.
        Assert.Equal(
            [
                (5ul, new ForwardEvent(Seq: 2, ForwardSource.MemWb, Operand.A, Register: 4, Value: 0, Producer: 1)),
                (6ul, new ForwardEvent(Seq: 3, ForwardSource.ExMem, Operand.A, Register: 5, Value: 0, Producer: 2)),
            ],
            Of<ForwardEvent>(run));

        Assert.Equal(8, run.Cycles);
        var stats = PipelineStats.Of(run.Records);
        Assert.Equal((8ul, 3ul, 1, 2), (stats.Cycles, stats.Instructions, stats.Stalls, stats.Forwards));
        Assert.Equal(2.67, stats.Cpi, precision: 2);
        Assert.Equal((1, 1), (stats.ForwardsFrom(ForwardSource.ExMem), stats.ForwardsFrom(ForwardSource.MemWb)));
    }

    [Fact]
    public void ADependencyCountsOnlyIfTheInstructionUsesTheRegister()
    {
        // Each second instruction has the first one's register number somewhere in its bits,
        // but does not read it: a shift amount of 5, an immediate of 5, a CSR constant of 5.
        var run = RunToEnd("""
            li    x5, 1
            slli  x6, x7, 5
            li    x5, 2
            addi  x6, x7, 5
            li    x5, 3
            csrrwi x0, mscratch, 5
            li    x5, 4
            lui   x6, 5
            li    x5, 5
            jal   x6, next
            next:
            """);

        Assert.Empty(Of<ForwardEvent>(run));
        Assert.Empty(Of<StallEvent>(run));
    }

    [Fact]
    public void BothOperandsCanBeForwardedAtOnce()
    {
        var run = RunToEnd("li t0, 3\nli t1, 4\nadd a0, t0, t1");

        Assert.Equal(
            [
                (5ul, new ForwardEvent(3, ForwardSource.MemWb, Operand.A, 5, 3, Producer: 1)),
                (5ul, new ForwardEvent(3, ForwardSource.ExMem, Operand.B, 6, 4, Producer: 2)),
            ],
            Of<ForwardEvent>(run));
    }

    [Fact]
    public void ABranchSaysWhetherItWasTakenAndWhatItCost()
    {
        var run = RunToEnd("""
                li   t0, 1
                beqz t0, skip            # 2: not taken
                bnez t0, skip            # 3: taken
                li   a0, 1               # 4: squashed in ID
                li   a1, 1               # 5: squashed in IF
            skip:
                li   a2, 1
            """);

        Assert.Equal(
            [
                (4ul, new BranchEvent(2, Taken: false, Target: 0x14, Mispredicted: false, Stage.Execute)),
                (5ul, new BranchEvent(3, Taken: true, Target: 0x14, Mispredicted: true, Stage.Execute)),
            ],
            Of<BranchEvent>(run));
        Assert.Equal(
            [
                (5ul, new FlushEvent(4, FlushCause.Branch, Stage.Decode, By: 3)),
                (5ul, new FlushEvent(5, FlushCause.Branch, Stage.Fetch, By: 3)),
            ],
            Of<FlushEvent>(run));

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal((2, 1, 1), (stats.Branches, stats.BranchesTaken, stats.Mispredictions));
        Assert.Equal((1, 2), (stats.Flushes, stats.Squashed));
        Assert.Equal(1, stats.FlushesBy(FlushCause.Branch));
    }

    [Fact]
    public void ASystemInstructionFlushesTheThreeBehindItAndSaysWhy()
    {
        var run = RunToEnd("li a7, 1\necall\nli a1, 1\nli a2, 2\nli a3, 3");

        Assert.Equal(
            [
                (5ul, new FlushEvent(3, FlushCause.System, Stage.Execute, By: 2)),
                (5ul, new FlushEvent(4, FlushCause.System, Stage.Decode, By: 2)),
                (5ul, new FlushEvent(5, FlushCause.System, Stage.Fetch, By: 2)),
            ],
            Of<FlushEvent>(run));
        Assert.Equal(1, PipelineStats.Of(run.Records).FlushesBy(FlushCause.System));
    }

    [Fact]
    public void AStopFlushesWhatIsBehindItAsAStop()
    {
        var run = Run("li a7, 10\necall\nli a1, 1");

        var flush = Assert.Single(Of<FlushEvent>(run));
        Assert.Equal(new FlushEvent(3, FlushCause.Stop, Stage.Execute, By: 2), flush.Event);
    }

    [Fact]
    public void ATrapSaysItsCauseAndWhereItWent()
    {
        var bases = new Fetchline.Core.Asm.AssemblerOptions { TextBase = 0x8000_0000, DataBase = 0x8000_4000 };
        var program = AssemblerTesting.Assemble("""
            la    t0, handler
            csrw  mtvec, t0
            ecall
            nop
            handler:
            la    t1, tohost
            li    t2, 1
            sw    t2, 0(t1)
            .data
            tohost: .word 0, 0
            """, bases);
        var machine = new PipelineMachine(program);
        var records = new List<CycleRecord>();
        while (!machine.IsFinished)
        {
            records.Add(machine.Step());
        }

        var trap = Assert.Single(records.SelectMany(r => r.Events).OfType<TrapEvent>());
        Assert.Equal((11u, 0u, 0x8000_0014u), (trap.Cause, trap.Value, trap.Handler));
        Assert.Contains(records.SelectMany(r => r.Events).OfType<FlushEvent>(), f => f.Cause == FlushCause.Trap && f.By == trap.Seq);

        var stats = PipelineStats.Of(records);
        Assert.Equal(1, stats.Traps);
        Assert.Equal(machine.Hart.InstructionsRetired, stats.Instructions);     // the ecall trapped: not counted
    }

    [Fact]
    public void RegisterAndMemoryActivityIsReportedWhereItHappens()
    {
        var run = RunToEnd("""
            lui  s0, 0x10000             # 1
            li   t0, -2                  # 2
            sh   t0, 4(s0)               # 3
            nop                          # 4
            lh   a0, 4(s0)               # 5
            lhu  a1, 4(s0)               # 6
            """);

        Assert.Equal(
            [(6ul, new MemWriteEvent(3, 0x1000_0004, 2, 0xFFFE))],
            Of<MemWriteEvent>(run));
        Assert.Equal(
            [
                (8ul, new MemReadEvent(5, 0x1000_0004, 2, 0xFFFF_FFFE)),
                (9ul, new MemReadEvent(6, 0x1000_0004, 2, 0x0000_FFFE)),
            ],
            Of<MemReadEvent>(run));

        // Registers are written in WB, one cycle after MEM. The store and the nop write none.
        Assert.Equal(
            [(5ul, 1ul, 8, 0x1000_0000u), (6ul, 2ul, 5, 0xFFFF_FFFEu), (9ul, 5ul, 10, 0xFFFF_FFFEu), (10ul, 6ul, 11, 0xFFFEu)],
            Of<RegWriteEvent>(run).Select(e => (e.Cycle, e.Event.Seq, (int)e.Event.Register, e.Event.Value)));
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul, 6ul], Of<CommitEvent>(run).Select(e => e.Event.Seq));

        var stats = PipelineStats.Of(run.Records);
        Assert.Equal((2, 1), (stats.Loads, stats.Stores));
    }

    [Fact]
    public void WithRecordingOffTheMachineComputesTheSameAndSaysNothing()
    {
        var program = AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", "bubble-sort.s")));
        var loud = new PipelineMachine(program);
        var quiet = new PipelineMachine(program) { Recording = false };

        while (!loud.IsFinished)
        {
            var (a, b) = (loud.Step(), quiet.Step());
            Assert.Equal(a.Commit, b.Commit);
            Assert.Equal(a.End, b.End);
            Assert.Equal(a.Fetch, b.Fetch);
            Assert.Empty(b.Events);
        }

        Assert.True(quiet.IsFinished);
        Assert.Equal(loud.Hart.X, quiet.Hart.X);
        Assert.Equal(loud.Hart.Output.ToString(), quiet.Hart.Output.ToString());
    }

    [Fact]
    public void TheSameProgramGivesTheSameRecordStreamEveryTime()
    {
        static ulong HashOf(string example, int cycles = int.MaxValue)
        {
            var machine = new PipelineMachine(AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", example))));
            var hash = default(TraceHash);
            for (var i = 0; i < cycles && !machine.IsFinished; i++)
            {
                machine.Step().AddTo(ref hash);
            }

            return hash.Value;
        }

        Assert.Equal(HashOf("bubble-sort.s"), HashOf("bubble-sort.s"));
        Assert.NotEqual(HashOf("bubble-sort.s"), HashOf("fib.s"));
        Assert.NotEqual(HashOf("fib.s", 100), HashOf("fib.s", 101));

        // The number itself is pinned, so a change in what the pipeline does or reports cannot
        // slip by: when this fails after a deliberate change, look at the new trace, then update it.
        // It last changed when a stall began to say how many more cycles it needs, which is zero
        // for every stall in this program; the records themselves were the same before and after.
        Assert.Equal(0xBCD3_D3DA_731E_052Dul, HashOf("load-use.s"));
    }

    [Fact]
    public void ReplayingToACycleGivesTheStateThatWasThereTheFirstTime()
    {
        // Stepping back in the playground is a replay from reset, so a replay must be exact.
        var program = AssemblerTesting.Assemble(File.ReadAllText(Repo.PathOf("examples", "factorial.s")));
        var first = new PipelineMachine(program);
        var snapshots = new List<(uint[] X, ulong Retired)>();
        var records = new List<CycleRecord>();
        while (!first.IsFinished)
        {
            records.Add(first.Step());
            snapshots.Add(((uint[])first.Hart.X.Clone(), first.Hart.InstructionsRetired));
        }

        foreach (var k in new[] { 1, 7, 50, 123, records.Count })
        {
            var replay = new PipelineMachine(program);
            CycleRecord? last = null;
            for (var i = 0; i < k; i++)
            {
                last = replay.Step();
            }

            Assert.Equal(snapshots[k - 1].X, replay.Hart.X);
            Assert.Equal(snapshots[k - 1].Retired, replay.Hart.InstructionsRetired);
            Assert.Equal(records[k - 1].Events, last!.Events);
            Assert.Equal(records[k - 1].Commit, last.Commit);
        }
    }

    [Fact]
    public void TheHashIsFnv1aAndDependsOnEveryField()
    {
        var empty = default(TraceHash);
        Assert.Equal(14695981039346656037ul, empty.Value);

        // FNV-1a of the single byte 0x61 ("a"), a published test vector.
        var a = default(TraceHash);
        a.Add((byte)0x61);
        Assert.Equal(0xAF63DC4C8601EC8Cul, a.Value);

        static ulong Of(Commit commit)
        {
            var hash = default(TraceHash);
            hash.Add(commit);
            return hash.Value;
        }

        var commit = new Commit { Pc = 4, Register = 10, Value = 7, NextPc = 8 };
        Assert.Equal(Of(commit), Of(commit));
        Assert.NotEqual(Of(commit), Of(commit with { Value = 8 }));
        Assert.NotEqual(Of(commit), Of(commit with { NextPc = 12 }));
        Assert.NotEqual(Of(commit), Of(commit with { Message = "x" }));
        Assert.NotEqual(Of(commit), Of(commit with { Stop = StopReason.Exit }));
    }
}
