using Fetchline.Tests.Machine;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Pipeline.PipelineTesting;

namespace Fetchline.Tests.Pipeline;

/// <summary>
/// Forwarding: a result is taken from the latch it sits in, instead of waiting for it to reach
/// the register file. It costs no cycles.
/// </summary>
public class ForwardingTests
{
    [Theory]
    [InlineData("li a0, 5\naddi a1, a0, 1", 6u)]                    // one behind: from EX/MEM
    [InlineData("li a0, 5\nnop\naddi a1, a0, 1", 6u)]               // two behind: from MEM/WB
    [InlineData("li a0, 5\nnop\nnop\naddi a1, a0, 1", 6u)]          // three behind: the register file has it
    [InlineData("li a0, 5\nadd a1, a0, a0", 10u)]                   // both operands from one producer
    [InlineData("li t0, 5\nli t1, 7\nsub a1, t1, t0", 2u)]          // A from EX/MEM, B from MEM/WB
    [InlineData("li t0, 5\nli t1, 7\nsub a1, t0, t1", 0xFFFF_FFFEu)] // A from MEM/WB, B from EX/MEM
    [InlineData("li a0, 1\nli a0, 2\naddi a1, a0, 0", 2u)]          // two producers: the newer one wins
    [InlineData("li a0, 1\nli a0, 2\nnop\naddi a1, a0, 0", 2u)]
    [InlineData("li a1, 1\naddi a1, a1, 1\naddi a1, a1, 1\naddi a1, a1, 1", 4u)]   // a chain
    [InlineData("addi x0, x0, 5\naddi a1, x0, 1", 1u)]              // x0 is zero, never a forwarded 5
    [InlineData("li t0, 3\nli t1, 4\nmul a1, t0, t1", 12u)]
    public void AConsumerGetsTheNewestValue(string source, uint expected)
    {
        var run = RunToEnd(source);

        Assert.Equal(expected, run["a1"]);
    }

    [Fact]
    public void ForwardingCostsNoCycles()
    {
        var run = RunToEnd("li a1, 1\naddi a1, a1, 1\naddi a1, a1, 1\naddi a1, a1, 1\naddi a1, a1, 1");

        Assert.Equal(5 + 4, run.Cycles);
        Assert.All(Enumerable.Range(1, 5), seq => Assert.Equal("IF ID EX MEM WB", run.Row((ulong)seq)));
    }

    [Fact]
    public void TheDataOfAStoreIsForwardedToo()
    {
        var run = RunToEnd("""
            lui  s0, 0x10000
            li   t0, 42
            sw   t0, 0(s0)               # t0 from EX/MEM, s0 from MEM/WB
            li   t1, 7
            sw   t1, 4(s0)
            nop
            nop
            nop
            lw   a0, 0(s0)
            lw   a1, 4(s0)
            """);

        Assert.Equal((42u, 7u), (run["a0"], run["a1"]));
    }

    [Fact]
    public void AFieldThatIsNotARegisterIsNotForwardedTo()
    {
        // The constant 5 of the csrrwi sits where a register number would. t0 is register 5 and
        // has just been written, but the instruction does not read it.
        var run = RunToEnd("li t0, 0x7FF\ncsrrwi zero, mscratch, 5\nnop\nnop\nnop\ncsrr a0, mscratch");

        Assert.Equal(5u, run["a0"]);
    }

    [Fact]
    public void StraightLineCodeWithoutLoadsMatchesTheReferenceMachine()
    {
        // Random arithmetic on a handful of registers, so nearly every instruction depends on
        // one of the two before it. Loads are left out: they need the stall that comes next.
        string[] operations = ["add", "sub", "xor", "or", "and", "sll", "srl", "sra", "slt", "sltu", "mul", "mulh", "div", "remu"];
        string[] registers = ["a0", "a1", "a2", "a3", "t0", "t1", "zero"];
        var random = new SeededRandom(0xF37C_5201);

        for (var round = 0; round < 300; round++)
        {
            var lines = new List<string>();
            for (var i = 0; i < 40; i++)
            {
                lines.Add(random.Chance(30)
                    ? $"addi {random.Pick(registers)}, {random.Pick(registers)}, {random.Next(-2048, 2047)}"
                    : $"{random.Pick(operations)} {random.Pick(registers)}, {random.Pick(registers)}, {random.Pick(registers)}");
            }

            var source = string.Join('\n', lines);
            var (reference, pipeline) = (MachineTesting.RunToEnd(source), RunToEnd(source));

            if (!reference.Commits.SequenceEqual(pipeline.Commits))
            {
                Assert.Fail($"seed {random.Seed:X}, round {round}: the records differ for\n{source}");
            }

            Assert.Equal(40 + 4, pipeline.Cycles);
        }
    }
}
