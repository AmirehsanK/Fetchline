using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;
using Fetchline.Tests.Asm;

namespace Fetchline.Tests.Pipeline;

public class LockstepTests
{
    public static TheoryData<string> Examples() => Cli.AsmAndDisTests.Examples();

    private static Lockstep Start(string source, TextWriter? output = null) =>
        new(AssemblerTesting.Assemble(source), output);

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryExampleRunsInLockstep(string name)
    {
        var output = new StringWriter();
        var lockstep = Start(File.ReadAllText(Repo.PathOf("examples", name)), output);

        lockstep.Run(3_000_000);

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.True(lockstep.Pipeline.IsFinished);
        Assert.True(lockstep.Reference.IsFinished);
        Assert.True(lockstep.Compared > 0);

        // Each machine ran the system calls, but the output appears once.
        Assert.Equal(Cli.RunTests.ExpectedOutput(name), output.ToString());
    }

    [Fact]
    public void EveryCommitIsComparedIncludingTheEndOfTheProgram()
    {
        var lockstep = Start("li a0, 1\nli a1, 2\nli a2, 3");

        var last = lockstep.Run()!;

        Assert.Equal(StopReason.EndOfProgram, last.Stop);
        Assert.Equal(4ul, lockstep.Compared);
        Assert.Null(lockstep.Divergence);
    }

    [Fact]
    public void AWrongValueIsCaughtAtTheFirstInstructionThatUsesIt()
    {
        // Corrupt a register in the pipeline's register file after the instruction that wrote it
        // has left WB. The two machines stay in step until something reads the bad value.
        var lockstep = Start("li t0, 5\nnop\nnop\nnop\nnop\nli a1, 1\nadd a0, t0, t0\nli a2, 2");
        for (var i = 0; i < 6; i++)
        {
            lockstep.Step();
        }

        lockstep.Pipeline.Hart.X[5] = 7;
        lockstep.Run();

        var divergence = lockstep.Divergence;
        Assert.NotNull(divergence);
        Assert.Equal(7ul, divergence.Index);
        Assert.Equal(
            "instruction 7, in cycle 11, 'add a0, t0, t0' at 0x00000018: " +
            "the pipeline wrote a0 = 0x0000000e, the reference machine wrote a0 = 0x0000000a",
            divergence.Describe());

        // Nothing is compared after the first disagreement.
        Assert.Equal(7ul, lockstep.Compared);
    }

    [Fact]
    public void ADivergenceIsDescribedByWhatDiffered()
    {
        var add = new Commit { Pc = 8, Instruction = Fetchline.Core.Isa.Decoder.Decode(0x00B50533), Register = 10, Value = 5, NextPc = 12 };
        var store = new Commit { Pc = 8, Instruction = Fetchline.Core.Isa.Decoder.Decode(0x00A2A023), StoreBytes = 4, StoreAddress = 0x100, StoreValue = 9, NextPc = 12 };
        var branch = new Commit { Pc = 8, Instruction = Fetchline.Core.Isa.Decoder.Decode(0x00050463), NextPc = 16 };

        Assert.Equal(
            "instruction 3, in cycle 9, 'add a0, a0, a1' at 0x00000008: the pipeline wrote a0 = 0x00000005, the reference machine wrote no register",
            new Divergence(3, 9, add, add with { Register = 0, Value = 0 }).Describe());
        Assert.Equal(
            "instruction 3, in cycle 9, 'sw a0, 0(t0)' at 0x00000008: the pipeline stored 0x00000009 to 0x00000100, the reference machine stored 0x00000008 to 0x00000100",
            new Divergence(3, 9, store, store with { StoreValue = 8 }).Describe());
        Assert.Equal(
            "instruction 3, in cycle 9, 'beqz a0, 0x10' at 0x00000008: the pipeline went on to 0x00000010, the reference machine to 0x0000000c",
            new Divergence(3, 9, branch, branch with { NextPc = 12 }).Describe());
        Assert.Equal(
            "instruction 3, in cycle 9: the pipeline committed 'add a0, a0, a1' at 0x00000008 where the reference machine ran the end of the program",
            new Divergence(3, 9, add, new Commit { Pc = 12, NextPc = 12, Stop = StopReason.EndOfProgram }).Describe());
    }

    [Fact]
    public void TheCycleCounterMayDifferAndTheProgramStaysInStep()
    {
        // The program reads the cycle counter and branches on it. The pipeline has taken more
        // cycles than the reference machine has taken steps, so the readings differ; the
        // pipeline's reading is the one both carry on with.
        var lockstep = Start("""
            nop
            nop
            rdcycle  a0
            csrr     a1, mcycle
            rdcycleh a2
            li       t0, 5
            bltu     a0, t0, few         # taken on the reference machine's count, not on the pipeline's
            li       a3, 1
            few:
            li       a4, 2
            """);

        lockstep.Run();

        Assert.True(lockstep.Divergence is null, lockstep.Divergence?.Describe());
        Assert.Equal(lockstep.Pipeline.Hart.X, lockstep.Reference.Hart.X);
        Assert.Equal(1u, lockstep.Pipeline.Hart.X[13]);
        Assert.True(lockstep.Pipeline.Hart.X[10] >= 5);
    }

    [Fact]
    public void TheInstructionCounterMustAgreeExactly()
    {
        // Unlike cycles, instructions are the same thing on both machines, flushes or no flushes.
        var lockstep = Start("li t0, 3\nloop: addi t0, t0, -1\nbnez t0, loop\nrdinstret a0\ncsrr a1, minstret");

        lockstep.Run();

        Assert.Null(lockstep.Divergence);
        Assert.Equal((7u, 8u), (lockstep.Pipeline.Hart.X[10], lockstep.Pipeline.Hart.X[11]));
    }

    [Fact]
    public void RunStopsAtItsBudgetAndCanGoOn()
    {
        var lockstep = Start("li t0, 100\nloop: addi t0, t0, -1\nbnez t0, loop");

        var record = lockstep.Run(maxCycles: 50);

        Assert.Equal(50ul, record!.Cycle);
        Assert.False(lockstep.Pipeline.IsFinished);
        Assert.Equal(StopReason.EndOfProgram, lockstep.Run()!.Stop);
        Assert.Null(lockstep.Divergence);
    }
}
