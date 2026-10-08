using Fetchline.Core.Isa;

namespace Fetchline.Tests.Isa;

public class InstructionSetTests
{
    [Fact]
    public void TheTableHasEveryInstructionOnceInTheOrderOfTheEnum()
    {
        var ops = Enum.GetValues<Op>().Where(op => op != Op.Illegal).ToArray();

        Assert.Equal(57, InstructionSet.All.Count);
        Assert.Equal(ops, InstructionSet.All.Select(row => row.Op));
        Assert.Equal(57, InstructionSet.All.Select(row => row.Mnemonic).Distinct().Count());
    }

    [Fact]
    public void EveryRowIsFoundByItsOpAndByItsMnemonic()
    {
        foreach (var row in InstructionSet.All)
        {
            Assert.Same(row, InstructionSet.Get(row.Op));
            Assert.True(InstructionSet.TryGet(row.Mnemonic, out var byName));
            Assert.Same(row, byName);
            Assert.Equal(row.Mnemonic, row.Mnemonic.ToLowerInvariant());
        }

        Assert.False(InstructionSet.TryGet("ADD", out _));
        Assert.False(InstructionSet.TryGet("addw", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstructionSet.Get(Op.Illegal));
        Assert.Equal(default, InstructionSet.ControlOf(Op.Illegal));
    }

    [Fact]
    public void EveryMatchLiesInsideItsMaskAndIsA32BitEncoding()
    {
        foreach (var row in InstructionSet.All)
        {
            Assert.True((row.Match & ~row.Mask) == 0, $"{row.Mnemonic}: match has bits outside its mask");
            Assert.True((row.Mask & 0x7F) == 0x7F, $"{row.Mnemonic}: mask must cover the opcode");
            Assert.True((row.Match & 3) == 3, $"{row.Mnemonic}: low bits must be 11");
        }
    }

    [Fact]
    public void NoWordCanMatchTwoRows()
    {
        // Two rows are told apart by a bit that both fix and fix differently. If no such bit
        // exists, some word matches both, and which instruction it is would depend on table order.
        var rows = InstructionSet.All;
        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                var distinguishing = (rows[i].Match ^ rows[j].Match) & rows[i].Mask & rows[j].Mask;
                Assert.True(distinguishing != 0, $"{rows[i].Mnemonic} and {rows[j].Mnemonic} overlap");
            }
        }
    }

    [Theory]
    [InlineData("lui", 0x00000037u, 0x0000007Fu)]
    [InlineData("jalr", 0x00000067u, 0x0000707Fu)]
    [InlineData("bgeu", 0x00007063u, 0x0000707Fu)]
    [InlineData("lhu", 0x00005003u, 0x0000707Fu)]
    [InlineData("sw", 0x00002023u, 0x0000707Fu)]
    [InlineData("srai", 0x40005013u, 0xFE00707Fu)]
    [InlineData("sub", 0x40000033u, 0xFE00707Fu)]
    [InlineData("remu", 0x02007033u, 0xFE00707Fu)]
    [InlineData("csrrci", 0x00007073u, 0x0000707Fu)]
    [InlineData("fence.i", 0x0000100Fu, 0x0000707Fu)]
    [InlineData("ebreak", 0x00100073u, 0xFFFFFFFFu)]
    [InlineData("mret", 0x30200073u, 0xFFFFFFFFu)]
    public void SpotChecksAgainstTheSpecification(string mnemonic, uint match, uint mask)
    {
        Assert.True(InstructionSet.TryGet(mnemonic, out var row));
        Assert.Equal(match, row.Match);
        Assert.Equal(mask, row.Mask);
    }

    [Fact]
    public void ControlSignalsSayWhichRegistersAnInstructionReallyUses()
    {
        // The pipeline's hazard logic depends on these: a dependency on a register the
        // instruction does not read is not a hazard, whatever bits sit in the field.
        static Control C(Op op) => InstructionSet.ControlOf(op);

        Assert.False(C(Op.Lui).UsesRs1);
        Assert.False(C(Op.Jal).UsesRs1);
        Assert.True(C(Op.Jalr).UsesRs1);
        Assert.False(C(Op.Addi).UsesRs2);
        Assert.False(C(Op.Lw).UsesRs2);
        Assert.True(C(Op.Sw).UsesRs2);
        Assert.True(C(Op.Beq).UsesRs1 && C(Op.Beq).UsesRs2);
        Assert.False(C(Op.Csrrwi).UsesRs1);
        Assert.True(C(Op.Csrrw).UsesRs1);
        Assert.False(C(Op.Ecall).UsesRs1 || C(Op.Ecall).UsesRs2 || C(Op.Ecall).WritesRd);

        Assert.False(C(Op.Sw).WritesRd);
        Assert.False(C(Op.Beq).WritesRd);
        Assert.True(C(Op.Jal).WritesRd);

        foreach (var row in InstructionSet.All)
        {
            var c = row.Control;
            Assert.Equal(c.Wb != WbSrc.None, c.WritesRd);
            Assert.Equal(c.Mem != MemOp.None, c.MemBytes != 0);
            Assert.Equal(row.Extension == Extension.M, c.IsMulDiv);
        }
    }
}
