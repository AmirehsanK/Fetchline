using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Asm;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// The values a cycle's record says were on the wires. They are what the datapath view shows,
/// so they are held to what the rest of the record says happened.
/// </summary>
public class WiresTests
{
    private const ulong Seed = 0x0D47_A9A7;
    private const int Programs = 60;

    private static List<CycleRecord> Run(string source, PipelineConfig? config = null)
    {
        var machine = new PipelineMachine(AssemblerTesting.Assemble(source), config: config);
        var records = new List<CycleRecord>();
        while (!machine.IsFinished && records.Count < 200_000)
        {
            records.Add(machine.Step());
        }

        Assert.True(machine.IsFinished);
        return records;
    }

    [Fact]
    public void TheAluIsGivenWhatTheInstructionAsksForAndItsResultIsWhatIsWritten()
    {
        // li, then an add of the register just written to itself: both operands are forwarded.
        var records = Run("li t0, 5\nadd t1, t0, t0\nauipc t2, 1\n");
        var text = records[0].Fetch.Pc;

        // Cycle 3: li (addi t0, zero, 5) is in EX. Its operands are x0 and the immediate.
        Assert.Equal((0u, 5u, 5u), (records[2].Wires.AluA, records[2].Wires.AluB, records[2].Wires.AluOut));

        // Cycle 4: the add is in EX. What ID read for t0 was stale; the wires say what EX used.
        var add = records[3].Wires;
        Assert.Equal((5u, 5u, 5u, 5u, 10u), (add.ExecuteRs1, add.ExecuteRs2, add.AluA, add.AluB, add.AluOut));
        Assert.Equal((0u, 0u), (records[2].Wires.DecodeRs1, records[2].Wires.DecodeRs2));

        // Cycle 5: auipc adds its immediate to its own address; the add's result is in MEM.
        Assert.Equal((text + 8, 0x1000u, text + 8 + 0x1000), (records[4].Wires.AluA, records[4].Wires.AluB, records[4].Wires.AluOut));
        Assert.Equal(10u, records[4].Wires.MemoryAlu);
    }

    [Fact]
    public void TheNextProgramCounterSaysWhereItCameFrom()
    {
        var records = Run("lw t0, 0(sp)\naddi t1, t0, 1\nbeqz zero, on\nnop\nnop\non: ebreak\n");
        var text = records[0].Fetch.Pc;
        var from = records.Select(record => record.Wires.NextPcFrom).ToList();

        // Two fetches straight on, then the load-use stall holds the counter for a cycle.
        Assert.Equal(
            [NextPcFrom.Sequential, NextPcFrom.Sequential, NextPcFrom.Held, NextPcFrom.Sequential],
            from.Take(4));
        Assert.Equal(records[1].Wires.NextPc, records[2].Wires.NextPc);

        // The branch is decided in EX and goes where fetch had not; the ebreak stops at MEM.
        var branch = records.Single(record => record.Wires.NextPcFrom == NextPcFrom.Execute);
        Assert.Equal(text + 20, branch.Wires.NextPc);
        Assert.Equal(NextPcFrom.Memory, records[^2].Wires.NextPcFrom);

        // Decided in ID, the same branch redirects from there, and a predictor that knows it
        // sends fetch the right way the second time round.
        var early = Run("beqz zero, on\nnop\non: nop\n", new PipelineConfig { Branches = BranchDecision.Decode });
        Assert.Equal(NextPcFrom.Decode, early[1].Wires.NextPcFrom);

        var loop = Run("li t0, 3\nagain: addi t0, t0, -1\nbnez t0, again\n", new PipelineConfig { Predictor = Predictor.TwoBit });
        Assert.Contains(loop, record => record.Wires.NextPcFrom == NextPcFrom.Predicted);
    }

    [Fact]
    public void WhatIsOnTheWiresAgreesWithTheRestOfTheRecordBuiltEveryCorrectWay()
    {
        for (var index = 0; index < Programs; index++)
        {
            var seed = Seed + (ulong)index;
            var source = ProgramGenerator.Generate(new SeededRandom(seed));
            foreach (var config in Configurations.Correct)
            {
                var records = Run(source, config);
                var where = $"program {index} (seed 0x{seed:X}) with {Configurations.Name(config)}";
                for (var i = 0; i < records.Count; i++)
                {
                    var (now, wires) = (records[i], records[i].Wires);
                    var at = $"{where}, cycle {now.Cycle}";

                    // A value forwarded to EX is the value EX used.
                    foreach (var forward in now.Events.OfType<ForwardEvent>().Where(item => item.To == Stage.Execute))
                    {
                        Assert.True(forward.Value == (forward.Operand == Operand.A ? wires.ExecuteRs1 : wires.ExecuteRs2), at);
                    }

                    if (i + 1 == records.Count)
                    {
                        continue;
                    }

                    // Whatever is fetched next is fetched from where the counter was sent.
                    var next = records[i + 1];
                    if (next.Fetch.HasInstruction)
                    {
                        Assert.True(next.Fetch.Pc == wires.NextPc, at);
                    }

                    if (now.Execute.State != Occupancy.Normal)
                    {
                        continue;
                    }

                    // What leaves EX is what MEM has a cycle later, and what is written in the end.
                    Assert.True(next.Memory.Seq == now.Execute.Seq, at);
                    Assert.True((next.Wires.MemoryAlu, next.Wires.MemoryRs2) == (wires.AluOut, wires.ExecuteRs2), at);

                    var instruction = Decoder.Decode(now.Execute.Raw);
                    if (instruction.Control is { WritesRd: true, Wb: WbSrc.Alu } && instruction.Rd != 0 && i + 2 < records.Count)
                    {
                        Assert.True(records[i + 2].Commit is { } commit && (commit.Trapped || commit.Value == wires.AluOut), at);
                    }
                }
            }
        }
    }
}
