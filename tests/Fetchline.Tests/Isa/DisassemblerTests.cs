using Fetchline.Core.Isa;

namespace Fetchline.Tests.Isa;

public class DisassemblerTests
{
    [Theory]
    // word, pc, with aliases, canonical
    [InlineData(0x00000013u, 0u, "nop", "addi zero, zero, 0")]
    [InlineData(0x00500513u, 0u, "li a0, 5", "addi a0, zero, 5")]
    [InlineData(0x00000513u, 0u, "li a0, 0", "addi a0, zero, 0")]
    [InlineData(0x00058513u, 0u, "mv a0, a1", "addi a0, a1, 0")]
    [InlineData(0xFFF54513u, 0u, "not a0, a0", "xori a0, a0, -1")]
    [InlineData(0x40B00533u, 0u, "neg a0, a1", "sub a0, zero, a1")]
    [InlineData(0x00153513u, 0u, "seqz a0, a0", "sltiu a0, a0, 1")]
    [InlineData(0x00B03533u, 0u, "snez a0, a1", "sltu a0, zero, a1")]
    [InlineData(0x0005A533u, 0u, "sltz a0, a1", "slt a0, a1, zero")]
    [InlineData(0x00B02533u, 0u, "sgtz a0, a1", "slt a0, zero, a1")]
    [InlineData(0x00050463u, 0x100u, "beqz a0, 0x108", "beq a0, zero, 0x108")]
    [InlineData(0x00051463u, 0x100u, "bnez a0, 0x108", "bne a0, zero, 0x108")]
    [InlineData(0x00055463u, 0x100u, "bgez a0, 0x108", "bge a0, zero, 0x108")]
    [InlineData(0x00A05463u, 0x100u, "blez a0, 0x108", "bge zero, a0, 0x108")]
    [InlineData(0x00054463u, 0x100u, "bltz a0, 0x108", "blt a0, zero, 0x108")]
    [InlineData(0x00A04463u, 0x100u, "bgtz a0, 0x108", "blt zero, a0, 0x108")]
    [InlineData(0xFE208EE3u, 0x100u, "beq ra, sp, 0xfc", "beq ra, sp, 0xfc")]
    [InlineData(0x0000006Fu, 0x40u, "j 0x40", "jal zero, 0x40")]
    [InlineData(0x008000EFu, 0u, "jal 0x8", "jal ra, 0x8")]
    [InlineData(0x0080056Fu, 0u, "jal a0, 0x8", "jal a0, 0x8")]
    [InlineData(0x00008067u, 0u, "ret", "jalr zero, 0(ra)")]
    [InlineData(0x000F0067u, 0u, "jr t5", "jalr zero, 0(t5)")]
    [InlineData(0x000500E7u, 0u, "jalr a0", "jalr ra, 0(a0)")]
    [InlineData(0x004500E7u, 0u, "jalr ra, 4(a0)", "jalr ra, 4(a0)")]
    [InlineData(0x34202F73u, 0u, "csrr t5, mcause", "csrrs t5, mcause, zero")]
    [InlineData(0x30529073u, 0u, "csrw mtvec, t0", "csrrw zero, mtvec, t0")]
    [InlineData(0x3002A073u, 0u, "csrs mstatus, t0", "csrrs zero, mstatus, t0")]
    [InlineData(0x3002B073u, 0u, "csrc mstatus, t0", "csrrc zero, mstatus, t0")]
    [InlineData(0x74445073u, 0u, "csrwi mnstatus, 8", "csrrwi zero, mnstatus, 8")]
    [InlineData(0x30046073u, 0u, "csrsi mstatus, 8", "csrrsi zero, mstatus, 8")]
    [InlineData(0x30047073u, 0u, "csrci mstatus, 8", "csrrci zero, mstatus, 8")]
    [InlineData(0x7C059573u, 0u, "csrrw a0, 0x7c0, a1", "csrrw a0, 0x7c0, a1")]
    [InlineData(0x123450B7u, 0u, "lui ra, 0x12345", "lui ra, 0x12345")]
    [InlineData(0x00001F17u, 0u, "auipc t5, 0x1", "auipc t5, 0x1")]
    [InlineData(0xFC3F2223u, 0u, "sw gp, -60(t5)", "sw gp, -60(t5)")]
    [InlineData(0x00C12203u, 0u, "lw tp, 12(sp)", "lw tp, 12(sp)")]
    [InlineData(0x5391E193u, 0u, "ori gp, gp, 1337", "ori gp, gp, 1337")]
    [InlineData(0x01F51513u, 0u, "slli a0, a0, 31", "slli a0, a0, 31")]
    [InlineData(0x022081B3u, 0u, "mul gp, ra, sp", "mul gp, ra, sp")]
    [InlineData(0x0FF0000Fu, 0u, "fence", "fence iorw, iorw")]
    [InlineData(0x0230000Fu, 0u, "fence r, rw", "fence r, rw")]
    [InlineData(0x0000100Fu, 0u, "fence.i", "fence.i")]
    [InlineData(0x00000073u, 0u, "ecall", "ecall")]
    [InlineData(0x00100073u, 0u, "ebreak", "ebreak")]
    [InlineData(0x30200073u, 0u, "mret", "mret")]
    [InlineData(0x10500073u, 0u, "wfi", "wfi")]
    public void InstructionsAreWrittenWithAndWithoutAliases(uint word, uint pc, string aliased, string canonical)
    {
        Assert.Equal(aliased, Disassembler.Format(word, pc));
        Assert.Equal(canonical, Disassembler.Format(word, pc, DisassemblyOptions.Canonical));
    }

    [Theory]
    [InlineData(0x00000000u, ".word 0x00000000")]
    [InlineData(0xFFFFFFFFu, ".word 0xffffffff")]
    [InlineData(0x02051513u, ".word 0x02051513")]   // slli a0, a0, 32
    [InlineData(0x8330000Fu, ".word 0x8330000f")]   // fence.tso: fm is set
    [InlineData(0x0000000Fu, ".word 0x0000000f")]   // fence with empty sets
    [InlineData(0x0FF0008Fu, ".word 0x0ff0008f")]   // fence with rd set
    [InlineData(0x0010100Fu, ".word 0x0010100f")]   // fence.i with an immediate
    public void WhatAssemblyCannotWriteIsShownAsData(uint word, string expected)
    {
        Assert.Equal(expected, Disassembler.Format(word, 0));
        Assert.Equal(expected, Disassembler.Format(word, 0, DisassemblyOptions.Canonical));
    }

    [Fact]
    public void RegistersCanBeWrittenByNumber()
    {
        var numeric = new DisassemblyOptions { Registers = RegisterStyle.Numeric };

        Assert.Equal("add x3, x1, x2", Disassembler.Format(0x002081B3, 0, numeric));
        Assert.Equal("lw x4, 12(x2)", Disassembler.Format(0x00C12203, 0, numeric));
        Assert.Equal("li x10, 5", Disassembler.Format(0x00500513, 0, numeric));
    }

    [Fact]
    public void ATargetWithALabelIsWrittenAsTheLabel()
    {
        var options = new DisassemblyOptions { SymbolAt = address => address == 0x108 ? "done" : null };

        Assert.Equal("beqz a0, done", Disassembler.Format(0x00050463, 0x100, options));
        Assert.Equal("beqz a0, 0x10c", Disassembler.Format(0x00050463, 0x104, options));
        Assert.Equal("j done", Disassembler.Format(0x0080006F, 0x100, options));
    }

    [Fact]
    public void ATargetWrapsAroundTheAddressSpace()
    {
        // jal zero, -4 at address 0 lands at the top of memory, as the hardware would.
        Assert.Equal("j 0xfffffffc", Disassembler.Format(0xFFDFF06F, 0));
    }

    [Fact]
    public void TheMnemonicComesApartFromItsOperands()
    {
        var line = Disassembler.Disassemble(0x00C12203, 0);

        Assert.Equal("lw", line.Mnemonic);
        Assert.Equal("tp, 12(sp)", line.Operands);
        Assert.Equal(string.Empty, Disassembler.Disassemble(0x00000073, 0).Operands);
    }
}
