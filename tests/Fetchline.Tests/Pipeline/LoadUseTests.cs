using Fetchline.Core.Pipeline;
using Fetchline.Tests.Machine;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Pipeline.PipelineTesting;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// The load-use stall: the one data hazard forwarding cannot remove. A loaded value exists only
/// after MEM, so an instruction that needs it in EX straight afterwards has to wait one cycle.
/// </summary>
public class LoadUseTests
{
    // Five instructions set things up, far enough ahead not to matter: a value in memory, and
    // registers the sequences below read.
    private const string Setup = """
        .data
        value: .word 21, 0x10000004
        .text
            lui  x2, 0x10000
            li   x6, 4
            nop
            nop
            nop

        """;

    [Fact]
    public void TheTextbookSequenceGivesTheTextbookDiagram()
    {
        var run = RunToEnd(Setup + """
                lw   x4, 0(x2)
                add  x5, x4, x6
                sub  x7, x5, x4
            """);

        // Instructions 6, 7 and 8: the add waits in ID, and the sub behind it waits in IF.
        Assert.Equal("IF ID EX MEM WB", run.Row(6));
        Assert.Equal("IF ID ID EX MEM WB", run.Row(7));
        Assert.Equal("IF IF ID EX MEM WB", run.Row(8));

        Assert.Equal((21u, 25u, 4u), (run["x4"], run["x5"], run["x7"]));
        Assert.Equal(8 + 4 + 1, run.Cycles);
    }

    [Fact]
    public void WhileAnInstructionWaitsABubbleGoesAheadOfIt()
    {
        var run = RunToEnd(Setup + "lw x4, 0(x2)\nadd x5, x4, x6");

        var stalled = run.Records.Single(record => record.Decode.State == Occupancy.Held);
        Assert.Equal(7ul, stalled.Decode.Seq);
        Assert.Equal(6ul, stalled.Execute.Seq);                       // the load is in EX

        var after = run.Records[(int)stalled.Cycle];                  // the next cycle
        Assert.False(after.Execute.HasInstruction);                   // the bubble
        Assert.Equal((6ul, Occupancy.Normal), (after.Memory.Seq, after.Memory.State));
        Assert.Equal((7ul, Occupancy.Normal), (after.Decode.Seq, after.Decode.State));
    }

    [Theory]
    [InlineData("lw x4, 0(x2)\nadd x5, x6, x4", 1)]                   // the use is the second operand
    [InlineData("lw x4, 0(x2)\nadd x5, x4, x4", 1)]                   // both operands: still one cycle
    [InlineData("lw x4, 0(x2)\nsw x4, 8(x2)", 1)]                     // a store's data counts as a use
    [InlineData("lw x4, 4(x2)\nlw x5, 0(x4)", 1)]                     // following a pointer
    [InlineData("lw x4, 0(x2)\nlw x5, 0(x2)\nadd x7, x4, x5", 1)]     // only the nearer load is too late
    [InlineData("lw x4, 0(x2)\nadd x5, x4, x6\nlw x7, 0(x2)\nadd x8, x7, x6", 2)]
    [InlineData("lw x4, 0(x2)\nnop\nadd x5, x4, x6", 0)]              // one instruction between is enough
    [InlineData("lw x4, 0(x2)\nadd x5, x6, x6", 0)]                   // the next instruction does not use it
    [InlineData("lw x0, 0(x2)\nadd x5, x0, x6", 0)]                   // a load into x0 produces nothing
    [InlineData("lw x4, 0(x2)\naddi x5, x6, 4", 0)]                   // 4 is an immediate, not register x4
    [InlineData("lw x5, 0(x2)\ncsrrwi x0, mscratch, 5", 0)]           // 5 is a constant, not register x5
    [InlineData("lw x4, 0(x2)\nlui x5, 0x4", 0)]
    [InlineData("add x4, x6, x6\nadd x5, x4, x6", 0)]                 // not a load: forwarding is enough
    public void OnlyAUseOfALoadedValueInTheVeryNextInstructionStalls(string sequence, int stalls)
    {
        var run = RunToEnd(Setup + sequence);
        var instructions = 5 + sequence.Split('\n').Length;

        Assert.Equal(instructions + 4 + stalls, run.Cycles);
        Assert.Equal(stalls, run.Records.Count(record => record.Decode.State == Occupancy.Held));
        Assert.Equal(MachineTesting.RunToEnd(Setup + sequence).Commits, run.Commits);
    }

    [Fact]
    public void TheInstructionBehindWaitsInFetchWithoutBeingFetchedTwice()
    {
        var run = RunToEnd(Setup + "lw x4, 0(x2)\nadd x5, x4, x6\nsub x7, x5, x4\nxor x8, x7, x6");

        // One sequence number per instruction: the wait does not make a second copy.
        Assert.Equal(9ul, run.Records.SelectMany(r => new[] { r.Fetch, r.Decode }).Max(view => view.Seq));
        var held = run.Records.Single(record => record.Fetch.State == Occupancy.Held);
        Assert.Equal((8ul, 0x1Cu), (held.Fetch.Seq, held.Fetch.Pc));
        Assert.Equal("IF IF ID EX MEM WB", run.Row(8));
        Assert.Equal("IF ID EX MEM WB", run.Row(9));              // fetched a cycle late, then no delay
    }

    [Fact]
    public void RandomStraightLineCodeWithLoadsAndStoresMatchesTheReferenceMachine()
    {
        string[] registers = ["a0", "a1", "a2", "a3", "t0", "t1", "zero"];
        var random = new SeededRandom(0xF37C_5301);

        for (var round = 0; round < 300; round++)
        {
            var lines = new List<string> { ".data", "cells: .word 1, 2, 3, 4, 5, 6, 7, 8", ".text", "lui s0, 0x10000" };
            for (var i = 0; i < 40; i++)
            {
                var offset = 4 * random.Next(0, 7);
                lines.Add(random.Next(0, 9) switch
                {
                    < 3 => $"lw {random.Pick(registers)}, {offset}(s0)",
                    < 5 => $"sw {random.Pick(registers)}, {offset}(s0)",
                    5 => $"lbu {random.Pick(registers)}, {offset + random.Next(0, 3)}(s0)",
                    6 => $"addi {random.Pick(registers)}, {random.Pick(registers)}, {random.Next(-64, 64)}",
                    _ => $"add {random.Pick(registers)}, {random.Pick(registers)}, {random.Pick(registers)}",
                });
            }

            var source = string.Join('\n', lines);
            var (reference, pipeline) = (MachineTesting.RunToEnd(source), RunToEnd(source));

            if (!reference.Commits.SequenceEqual(pipeline.Commits))
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: the records differ for\n{source}");
            }
        }
    }
}
