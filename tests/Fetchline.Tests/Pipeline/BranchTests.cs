using Fetchline.Core.Pipeline;
using Fetchline.Tests.Machine;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Pipeline.PipelineTesting;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// Branches and jumps decided in EX. Fetch carries on in a straight line behind them, so when
/// one is taken the two instructions fetched since are thrown away: a two-cycle penalty.
/// </summary>
public class BranchTests
{
    [Fact]
    public void ATakenBranchThrowsAwayTheTwoInstructionsFetchedBehindIt()
    {
        var run = RunToEnd("""
                beq  x0, x0, target      # 1: always taken
                li   a0, 1               # 2: in ID when the branch is decided
                li   a1, 2               # 3: in IF
                li   a2, 3               #    never fetched
            target:
                li   a3, 4               # 4
            """);

        Assert.Equal("IF ID EX MEM WB", run.Row(1));
        Assert.Equal("IF ID xx", run.Row(2));
        Assert.Equal("IF xx", run.Row(3));
        Assert.Equal("IF ID EX MEM WB", run.Row(4));
        Assert.Equal(run.CycleOf(1, Stage.Execute) + 1, run.CycleOf(4, Stage.Fetch));

        // The wrong path left no trace, and the run is two cycles longer than its two instructions.
        Assert.Equal((0u, 0u, 0u, 4u), (run["a0"], run["a1"], run["a2"], run["a3"]));
        Assert.Equal(2 + 4 + 2, run.Cycles);
        Assert.Equal(2ul, run.Machine.Hart.InstructionsRetired);
    }

    [Fact]
    public void ABranchThatIsNotTakenCostsNothing()
    {
        var run = RunToEnd("li t0, 1\nbeqz t0, away\nli a0, 1\nli a1, 2\naway:\nli a2, 3");

        Assert.Equal(5 + 4, run.Cycles);
        Assert.Equal((1u, 2u, 3u), (run["a0"], run["a1"], run["a2"]));
        Assert.DoesNotContain(run.Records, record => record.Decode.State == Occupancy.Squashed);
    }

    [Fact]
    public void AJumpAlwaysPaysThePenaltyAndItsLinkIsForwarded()
    {
        var run = RunToEnd("""
                jal  ra, callee          # 0x00
                li   a5, 9               # 0x04: skipped
            callee:
                addi a0, ra, 0           # 0x08: ra comes straight from the jump, by forwarding
                addi a1, ra, 100
            """);

        Assert.Equal((4u, 104u, 0u), (run["a0"], run["a1"], run["a5"]));
        Assert.Equal(3 + 4 + 2, run.Cycles);
    }

    [Fact]
    public void AnIndirectJumpTakesItsTargetFromAForwardedRegister()
    {
        var run = RunToEnd("""
                la   t0, there           # two instructions; the jump needs t0 at once
                jalr a0, 0(t0)
                li   a5, 9
                li   a6, 9
            there:
                li   a1, 7
            """);

        Assert.Equal((12u, 7u, 0u, 0u), (run["a0"], run["a1"], run["a5"], run["a6"]));
    }

    [Fact]
    public void ALoopPaysThePenaltyEachTimeItGoesRound()
    {
        var run = RunToEnd("""
                li   t0, 3
            loop:
                addi a0, a0, 10
                addi t0, t0, -1
                bnez t0, loop            # taken twice, then falls through
            """);

        // 1 + 3 * 3 instructions, and two taken branches at two cycles each.
        Assert.Equal(30u, run["a0"]);
        Assert.Equal(10ul, run.Machine.Hart.InstructionsRetired);
        Assert.Equal(10 + 4 + (2 * 2), run.Cycles);
    }

    [Fact]
    public void ABranchOnALoadedValueWaitsForTheLoadAndThenDecides()
    {
        var run = RunToEnd("""
            .data
            flag: .word 1
            .text
                lui  s0, 0x10000
                nop
                nop
                lw   t0, 0(s0)           # 4
                bnez t0, yes             # 5: load-use stall, then taken
                li   a0, 1
            yes:
                li   a1, 2
            """);

        Assert.Equal("IF ID ID EX MEM WB", run.Row(5));
        Assert.Equal((0u, 2u), (run["a0"], run["a1"]));
        Assert.Equal(6 + 4 + 1 + 2, run.Cycles);
    }

    [Fact]
    public void NothingOnTheWrongPathHappens()
    {
        // The two instructions behind the jump are a store and a register write. Neither may
        // take effect, and the third is never even fetched.
        var run = RunToEnd("""
            .data
            cell: .word 5
            .text
                lui  s0, 0x10000
                li   t0, 99
                nop
                j    over
                sw   t0, 0(s0)
                li   a0, 1
                li   a1, 1
            over:
                lw   a2, 0(s0)
            """);

        Assert.Equal((0u, 0u, 5u), (run["a0"], run["a1"], run["a2"]));
        Assert.DoesNotContain(run.Commits, commit => commit.WritesMemory);
    }

    [Fact]
    public void ARedirectOutranksAStallBehindIt()
    {
        // When the jump is in EX, the instruction in ID is a use of a load... but the load is
        // the jump's own delay: here the pair behind the jump is "lw, use", on the wrong path.
        var run = RunToEnd("""
            .data
            cell: .word 5
            .text
                lui  s0, 0x10000
                nop
                nop
                lw   t0, 0(s0)           # 4
                beq  t0, t0, over        # 5: stalled for the load, then taken
                lw   t1, 0(s0)           # 6: wrong path
                add  a0, t1, t1          # 7: wrong path; would stall behind 6
            over:
                li   a1, 3               # 8
            """);

        Assert.Equal("IF IF ID xx", run.Row(6));
        Assert.Equal("IF xx", run.Row(7));
        Assert.Equal((0u, 3u), (run["a0"], run["a1"]));
        Assert.Equal(MachineTesting.RunToEnd("""
            .data
            cell: .word 5
            .text
                lui  s0, 0x10000
                nop
                nop
                lw   t0, 0(s0)
                beq  t0, t0, over
                lw   t1, 0(s0)
                add  a0, t1, t1
            over:
                li   a1, 3
            """).Commits, run.Commits);
    }

    [Fact]
    public void TheEndOfTheCodeOnTheWrongPathDoesNotEndTheRun()
    {
        // The loop's branch is the last instruction: behind it fetch runs off the end of the
        // code. That is the wrong path while the loop is still going round.
        var run = RunToEnd("li t0, 4\nloop:\naddi a0, a0, 1\naddi t0, t0, -1\nbnez t0, loop");

        Assert.Equal(4u, run["a0"]);
        Assert.Equal(13 + 4 + (3 * 2), run.Cycles);
    }

    [Fact]
    public void ABackwardJumpToItselfSpinsUntilTheBudgetRunsOut()
    {
        var run = Run("spin: j spin", maxCycles: 300);

        Assert.Equal(300, run.Cycles);
        Assert.False(run.Machine.IsFinished);
        Assert.Equal(99ul, run.Machine.Hart.InstructionsRetired);    // one every three cycles
    }

    [Fact]
    public void RandomProgramsWithForwardBranchesMatchTheReferenceMachine()
    {
        // Forward branches and jumps only, so every program ends. Each branch skips zero to
        // three instructions; whether it is taken depends on the data.
        string[] registers = ["a0", "a1", "a2", "t0", "t1", "zero"];
        string[] conditions = ["beq", "bne", "blt", "bge", "bltu", "bgeu"];
        var random = new SeededRandom(0xF37C_5401);

        for (var round = 0; round < 400; round++)
        {
            var lines = new List<string> { ".data", "cells: .word 3, -1, 0, 7", ".text", "lui s0, 0x10000" };
            const int count = 50;
            for (var i = 0; i < count; i++)
            {
                lines.Add($"L{i}:");
                var skipTo = $"L{Math.Min(count, i + 1 + random.Next(0, 3))}";
                lines.Add(random.Next(0, 9) switch
                {
                    < 2 => $"{random.Pick(conditions)} {random.Pick(registers)}, {random.Pick(registers)}, {skipTo}",
                    2 => $"jal {random.Pick(registers)}, {skipTo}",
                    3 => $"lw {random.Pick(registers)}, {4 * random.Next(0, 3)}(s0)",
                    4 => $"sw {random.Pick(registers)}, {4 * random.Next(0, 3)}(s0)",
                    5 => $"addi {random.Pick(registers)}, {random.Pick(registers)}, {random.Next(-4, 4)}",
                    _ => $"sub {random.Pick(registers)}, {random.Pick(registers)}, {random.Pick(registers)}",
                });
            }

            lines.Add($"L{count}:");
            var source = string.Join('\n', lines);
            var (reference, pipeline) = (MachineTesting.RunToEnd(source), RunToEnd(source));

            if (!reference.Commits.SequenceEqual(pipeline.Commits))
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: the records differ for\n{source}");
            }
        }
    }
}
