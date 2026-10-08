using Fetchline.Core.Asm;
using Fetchline.Core.Machine;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Machine;
using static Fetchline.Tests.Pipeline.PipelineTesting;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// MEM is the commit point. System instructions, traps and stops take effect there, and the
/// three instructions behind are thrown away and fetched again.
/// </summary>
public class CommitPointTests
{
    private static readonly AssemblerOptions BareBases = new() { TextBase = 0x8000_0000, DataBase = 0x8000_4000 };

    [Fact]
    public void ASystemCallRestartsTheThreeInstructionsBehindIt()
    {
        var run = RunToEnd("""
            li   a0, 7                   # 1
            li   a7, 1                   # 2
            ecall                        # 3: print int
            li   a1, 1                   # 4, and again as 7
            li   a2, 2                   # 5, and again as 8
            li   a3, 3                   # 6, and again as 9
            """);

        Assert.Equal("7", run.Output);
        Assert.Equal("IF ID EX MEM WB", run.Row(3));
        Assert.Equal("IF ID EX xx", run.Row(4));
        Assert.Equal("IF ID xx", run.Row(5));
        Assert.Equal("IF xx", run.Row(6));
        Assert.Equal("IF ID EX MEM WB", run.Row(7));
        Assert.Equal(0x0Cu, run.Records.First(r => r.Fetch.Seq == 7).Fetch.Pc);      // the same instruction as 4
        Assert.Equal(run.CycleOf(3, Stage.Memory) + 1, run.CycleOf(7, Stage.Fetch));

        Assert.Equal((1u, 2u, 3u), (run["a1"], run["a2"], run["a3"]));
        Assert.Equal(6 + 4 + 3, run.Cycles);
        Assert.Equal(6ul, run.Machine.Hart.InstructionsRetired);
    }

    [Fact]
    public void ExitEndsTheRunInTheCycleItLeavesWriteBack()
    {
        var run = Run("li a0, 3\nli a7, 93\necall\nli a0, 99\nli a1, 99");

        Assert.Equal((StopReason.Exit, 3), (run.Last.Stop, run.Last.Commit!.Value.ExitCode));
        Assert.Equal(run.CycleOf(3, Stage.WriteBack), (ulong)run.Cycles);
        Assert.Equal(3 + 4, run.Cycles);
        Assert.True(run.Machine.IsFinished);
        Assert.Equal((3u, 0u), (run["a0"], run["a1"]));

        // Once the exit has committed, nothing more is fetched.
        var afterCommit = run.Records.Skip((int)run.CycleOf(3, Stage.Memory));
        Assert.All(afterCommit, record => Assert.False(record.Fetch.HasInstruction));
    }

    [Fact]
    public void TheResultOfASystemCallReachesTheInstructionAfterIt()
    {
        var run = RunToEnd("""
            .data
            text: .ascii "hello"
            .text
                li   a7, 64
                li   a0, 1
                la   a1, text
                li   a2, 5
                ecall                    # write returns 5 in a0
                addi s0, a0, 100         # needs that 5 at once
            """);

        Assert.Equal("hello", run.Output);
        Assert.Equal(105u, run["s0"]);
    }

    [Fact]
    public void ACsrReadIsSeenByTheInstructionAfterIt()
    {
        var run = RunToEnd("li t0, 41\ncsrw mscratch, t0\ncsrr t1, mscratch\naddi a0, t1, 1\ncsrr a1, minstret");

        Assert.Equal(42u, run["a0"]);
        Assert.Equal(4u, run["a1"]);     // four instructions completed before it, whatever was squashed
    }

    [Fact]
    public void ASystemCallOnAWrongPathNeverRuns()
    {
        // The exit sits right behind a jump. It is fetched, and it is squashed in ID long
        // before it could reach the commit point.
        var run = RunToEnd("li a7, 93\nli a0, 5\nj over\necall\nli a1, 1\nover:\nli a2, 2");

        Assert.Equal(StopReason.EndOfProgram, run.Last.Stop);
        Assert.Equal((0u, 2u), (run["a1"], run["a2"]));
        Assert.Equal("IF ID xx", run.Row(4));
    }

    [Fact]
    public void AnEbreakPausesThePipelineAndItCanGoOn()
    {
        var machine = new PipelineMachine(AssemblerTesting.Assemble("li a0, 1\nebreak\nli a0, 2\nli a1, 3"));

        var paused = machine.Run()!;
        Assert.Equal(StopReason.Breakpoint, paused.Stop);
        Assert.Equal(StopReason.Breakpoint, machine.Stopped);
        Assert.False(machine.IsFinished);
        Assert.Equal(1u, machine.Hart.X[10]);

        var ended = machine.Run()!;
        Assert.Equal(StopReason.EndOfProgram, ended.Stop);
        Assert.Equal((2u, 3u), (machine.Hart.X[10], machine.Hart.X[11]));
        Assert.True(machine.IsFinished);
    }

    [Theory]
    [InlineData("li a0, 5\nnop\nnop\nsw a0, 0(zero)\nli a0, 9\nli a1, 9", "a store to 0x00000000, which is in 'text' and cannot be written, at pc 0x0000000c")]
    [InlineData("nop\n.word 0\nli a1, 9", "an illegal instruction (0x00000000), at pc 0x00000004")]
    [InlineData("li t0, 6\njr t0\nli a1, 9", "a jump to 0x00000006, which is not a multiple of 4, at pc 0x00000004")]
    [InlineData("li a7, 999\necall\nli a1, 9", "an ecall with a7 = 999, which is not a system call this machine has, at pc 0x00000004")]
    public void AFaultStopsThePipelineAndNothingBehindItTakesEffect(string source, string message)
    {
        var run = Run(source);

        Assert.Equal(StopReason.Fault, run.Last.Stop);
        Assert.Equal(message, run.Last.Commit!.Value.Message);
        Assert.Equal(0u, run["a1"]);
        Assert.NotEqual(9u, run["a0"]);
        Assert.True(run.Machine.IsFinished);
        Assert.Equal(MachineTesting.Run(source).Commits, run.Commits);
    }

    [Fact]
    public void AJumpToSomewhereThatIsNotCodeFaultsOnceEverythingBeforeItIsDone()
    {
        const string source = "li t0, 0x5000\nli a0, 4\njr t0\nli a1, 9";
        var run = Run(source);

        Assert.Equal(StopReason.Fault, run.Last.Stop);
        Assert.Equal("a jump to 0x00005000, which is outside the program, at pc 0x00005000", run.Last.End!.Value.Message);
        Assert.Equal((4u, 0u), (run["a0"], run["a1"]));
        Assert.Equal(MachineTesting.Run(source).Commits, run.Commits);
    }

    [Fact]
    public void OnBareMetalATrapGoesToTheHandlerAndMretComesBack()
    {
        const string source = """
            la    t0, handler
            csrw  mtvec, t0
            li    a0, 1
            ecall
            li    a0, 3
            j     done
            handler:
            li    a1, 2
            csrr  s0, mcause
            csrr  s1, mepc
            addi  s1, s1, 4
            csrw  mepc, s1
            mret
            done:
            la    t1, tohost
            li    t2, 1
            sw    t2, 0(t1)
            .data
            tohost: .word 0, 0
            """;
        var program = AssemblerTesting.Assemble(source, BareBases);
        var pipeline = new PipelineMachine(program);
        var reference = new ReferenceMachine(program);

        var commits = new List<Commit>();
        while (!pipeline.IsFinished)
        {
            var record = pipeline.Step();
            commits.AddRange(new[] { record.Commit, record.End }.OfType<Commit>());
            Assert.True(record.Cycle < 1000);
        }

        Assert.Equal((StopReason.Tohost, 1), (pipeline.Stopped, commits[^1].ExitCode));
        Assert.Equal((3u, 2u, 11u), (pipeline.Hart.X[10], pipeline.Hart.X[11], pipeline.Hart.X[8]));
        Assert.Single(commits, c => c.Trapped);

        var expected = new List<Commit>();
        while (!reference.IsFinished)
        {
            expected.Add(reference.Step());
        }

        Assert.Equal(expected, commits);
    }

    [Fact]
    public void AMisalignedJumpIsFollowedThenTrappedAndItsWrongPathIsThrownAway()
    {
        const string source = """
            la    t0, handler
            csrw  mtvec, t0
            la    t1, target + 2
            li    ra, 0x55
            jalr  ra, 0(t1)
            target:
            li    a7, 99
            li    a6, 99
            handler:
            csrr  s0, mcause
            la    t1, tohost
            li    t2, 1
            sw    t2, 0(t1)
            .data
            tohost: .word 0, 0
            """;
        var pipeline = new PipelineMachine(AssemblerTesting.Assemble(source, BareBases));

        pipeline.Run(1000);

        Assert.Equal(StopReason.Tohost, pipeline.Stopped);
        Assert.Equal((0u, 0x55u, 0u, 0u), (pipeline.Hart.X[8], pipeline.Hart.X[1], pipeline.Hart.X[17], pipeline.Hart.X[16]));
    }

    public static TheoryData<string> Examples() => Cli.AsmAndDisTests.Examples();

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryExampleLeavesTheSameRecordsAndOutputOnBothMachines(string name)
    {
        var source = File.ReadAllText(Repo.PathOf("examples", name));

        var reference = MachineTesting.Run(source, maxInstructions: 1_000_000);
        var pipeline = Run(source, maxCycles: 3_000_000);

        Assert.True(pipeline.Machine.IsFinished);
        Assert.Equal(reference.Output, pipeline.Output);
        Assert.Equal(reference.Commits.Count, pipeline.Commits.Count);
        Assert.True(reference.Commits.SequenceEqual(pipeline.Commits), "the records differ");
        Assert.Equal(reference.Hart.X, pipeline.Machine.Hart.X);
    }
}
