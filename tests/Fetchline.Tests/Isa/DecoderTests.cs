using Fetchline.Core.Isa;

namespace Fetchline.Tests.Isa;

public class DecoderTests
{
    [Theory]
    [InlineData(0x002081B3u, Op.Add, 3, 1, 2, 0)]              // add x3, x1, x2
    [InlineData(0x402081B3u, Op.Sub, 3, 1, 2, 0)]              // sub x3, x1, x2
    [InlineData(0x022081B3u, Op.Mul, 3, 1, 2, 0)]              // mul x3, x1, x2
    [InlineData(0xFFF00093u, Op.Addi, 1, 0, 0, -1)]            // addi x1, x0, -1
    [InlineData(0x00C12203u, Op.Lw, 4, 2, 0, 12)]              // lw x4, 12(x2)
    [InlineData(0xFE112E23u, Op.Sw, 0, 2, 1, -4)]              // sw x1, -4(x2)
    [InlineData(0xFE208EE3u, Op.Beq, 0, 1, 2, -4)]             // beq x1, x2, -4
    [InlineData(0x123450B7u, Op.Lui, 1, 0, 0, 0x12345000)]     // lui x1, 0x12345
    [InlineData(0x00001097u, Op.Auipc, 1, 0, 0, 0x1000)]       // auipc x1, 0x1
    [InlineData(0xFFDFF0EFu, Op.Jal, 1, 0, 0, -4)]             // jal x1, -4
    [InlineData(0x00008067u, Op.Jalr, 0, 1, 0, 0)]             // jalr x0, 0(x1)  (ret)
    [InlineData(0x01F51513u, Op.Slli, 10, 10, 0, 31)]          // slli a0, a0, 31
    [InlineData(0x40355513u, Op.Srai, 10, 10, 0, 3)]           // srai a0, a0, 3
    [InlineData(0x30051073u, Op.Csrrw, 0, 10, 0, 0x300)]       // csrrw x0, mstatus, a0
    [InlineData(0xF1402573u, Op.Csrrs, 10, 0, 0, 0xF14)]       // csrrs a0, mhartid, x0
    [InlineData(0x3002D073u, Op.Csrrwi, 0, 5, 0, 0x300)]       // csrrwi x0, mstatus, 5
    [InlineData(0x0FF0000Fu, Op.Fence, 0, 0, 0, 0x0FF)]        // fence iorw, iorw
    [InlineData(0x0000100Fu, Op.FenceI, 0, 0, 0, 0)]           // fence.i
    [InlineData(0x00000073u, Op.Ecall, 0, 0, 0, 0)]
    [InlineData(0x00100073u, Op.Ebreak, 0, 0, 0, 0)]
    [InlineData(0x30200073u, Op.Mret, 0, 0, 0, 0)]
    [InlineData(0x10500073u, Op.Wfi, 0, 0, 0, 0)]
    public void KnownWordsDecodeToTheirFields(uint word, Op op, int rd, int rs1, int rs2, int imm)
    {
        var instruction = Decoder.Decode(word);

        Assert.Equal(op, instruction.Op);
        Assert.Equal(rd, instruction.Rd);
        Assert.Equal(rs1, instruction.Rs1);
        Assert.Equal(rs2, instruction.Rs2);
        Assert.Equal(imm, instruction.Imm);
        Assert.Equal(word, instruction.Raw);
        Assert.True(instruction.IsLegal);
    }

    [Theory]
    [InlineData(0x00000000u)]   // all zeros: defined to be illegal
    [InlineData(0xFFFFFFFFu)]   // all ones: defined to be illegal
    [InlineData(0x00000001u)]   // a 16-bit encoding
    [InlineData(0x00008082u)]   // c.ret
    [InlineData(0x02051513u)]   // slli a0, a0, 32: an RV64 shift amount
    [InlineData(0x02055513u)]   // srli a0, a0, 32
    [InlineData(0x42055513u)]   // srai a0, a0, 32
    [InlineData(0x0000003Bu)]   // addw x0, x0, x0: RV64 only
    [InlineData(0x00003003u)]   // ld x0, 0(x0): RV64 only
    [InlineData(0x00003023u)]   // sd x0, 0(x0): RV64 only
    [InlineData(0x04000033u)]   // OP with an unassigned funct7
    [InlineData(0x00002063u)]   // BRANCH with funct3 2: unassigned
    [InlineData(0x00001067u)]   // JALR with funct3 1: unassigned
    [InlineData(0x00004073u)]   // SYSTEM with funct3 4: unassigned
    [InlineData(0x10200073u)]   // sret: there is no supervisor mode
    [InlineData(0x00200073u)]   // uret
    [InlineData(0x7B200073u)]   // dret
    [InlineData(0x00000053u)]   // fadd.s: no floating point
    [InlineData(0x0000202Fu)]   // amoadd.w: no atomics
    public void WordsOutsideTheInstructionSetAreIllegal(uint word)
    {
        var instruction = Decoder.Decode(word);

        Assert.Equal(Op.Illegal, instruction.Op);
        Assert.False(instruction.IsLegal);
        Assert.Equal(word, instruction.Raw);
        Assert.Equal(default, instruction.Control);
    }

    [Fact]
    public void EcallAndItsNeighboursAreToldApartByEveryBit()
    {
        // ecall, ebreak, mret and wfi share an opcode and funct3 and are fixed in all 32 bits,
        // so a register number in any of them makes the word something else: illegal.
        Assert.Equal(Op.Illegal, Decoder.Decode(0x00000073u | (1u << 7)).Op);
        Assert.Equal(Op.Illegal, Decoder.Decode(0x00100073u | (1u << 15)).Op);
        Assert.Equal(Op.Illegal, Decoder.Decode(0x30200073u | (1u << 7)).Op);
        Assert.Equal(Op.Illegal, Decoder.Decode(0x00200073u | (1u << 20)).Op);
    }
}
