using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Tests.Isa;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Asm.AssemblerTesting;

namespace Fetchline.Tests.Asm;

/// <summary>The real instructions: one line of assembly, one machine word.</summary>
public class AssemblerInstructionTests
{
    [Theory]
    [InlineData("add x3, x1, x2", 0x002081B3u)]
    [InlineData("sub gp, ra, sp", 0x402081B3u)]
    [InlineData("mul x3, x1, x2", 0x022081B3u)]
    [InlineData("addi x1, x0, -1", 0xFFF00093u)]
    [InlineData("addi x1, x0, 2047", 0x7FF00093u)]
    [InlineData("addi x1, x0, -2048", 0x80000093u)]
    [InlineData("ori gp, gp, 1337", 0x5391E193u)]
    [InlineData("xori a0, a0, -1", 0xFFF54513u)]
    [InlineData("slli a0, a0, 31", 0x01F51513u)]
    [InlineData("srai a0, a0, 3", 0x40355513u)]
    [InlineData("lw x4, 12(x2)", 0x00C12203u)]
    [InlineData("lw x4, (x2)", 0x00012203u)]
    [InlineData("sw x1, -4(x2)", 0xFE112E23u)]
    [InlineData("sw gp, -60(t5)", 0xFC3F2223u)]
    [InlineData("lui x1, 0x12345", 0x123450B7u)]
    [InlineData("lui x1, 0xFFFFF", 0xFFFFF0B7u)]
    [InlineData("auipc t5, 1", 0x00001F17u)]
    [InlineData("jalr x0, 0(x1)", 0x00008067u)]
    [InlineData("jalr ra, 4(a0)", 0x004500E7u)]
    [InlineData("csrrw x0, mstatus, a0", 0x30051073u)]
    [InlineData("csrrs a0, mhartid, x0", 0xF1402573u)]
    [InlineData("csrrs a0, 0xF14, x0", 0xF1402573u)]
    [InlineData("csrrwi x0, mstatus, 5", 0x3002D073u)]
    [InlineData("csrrwi zero, 0x744, 8", 0x74445073u)]
    [InlineData("fence", 0x0FF0000Fu)]
    [InlineData("fence iorw, iorw", 0x0FF0000Fu)]
    [InlineData("fence r, rw", 0x0230000Fu)]
    [InlineData("fence.i", 0x0000100Fu)]
    [InlineData("ecall", 0x00000073u)]
    [InlineData("ebreak", 0x00100073u)]
    [InlineData("mret", 0x30200073u)]
    [InlineData("wfi", 0x10500073u)]
    [InlineData("ADDI x1, x0, -1", 0xFFF00093u)]   // mnemonics are case-blind; registers are not
    public void OneLineIsOneWord(string line, uint expected)
    {
        Assert.Equal([expected], Assemble(line).Words());
    }

    [Fact]
    public void BranchesAndJumpsReachLabelsInBothDirections()
    {
        var program = Assemble("""
            start:
                beq  a0, a1, done
                jal  ra, done
            back:
                bne  a0, a1, back
                jal  zero, start
            done:
                bgeu a0, a1, back
            """);

        Assert.Equal(
            [
                Encoder.Encode(Op.Beq, rs1: 10, rs2: 11, imm: 16),
                Encoder.Encode(Op.Jal, rd: 1, imm: 12),
                Encoder.Encode(Op.Bne, rs1: 10, rs2: 11, imm: 0),
                Encoder.Encode(Op.Jal, rd: 0, imm: -12),
                Encoder.Encode(Op.Bgeu, rs1: 10, rs2: 11, imm: -8),
            ],
            program.Words());
    }

    [Fact]
    public void ANumberWhereALabelIsExpectedIsAnAbsoluteAddress()
    {
        var options = new AssemblerOptions { TextBase = 0x100 };

        Assert.Equal([Encoder.Encode(Op.Jal, rd: 0, imm: 0x10)], Assemble("jal zero, 0x110", options).Words());
        Assert.Equal([Encoder.Encode(Op.Beq, imm: -0x100)], Assemble("beq x0, x0, 0", options).Words());

        // Addresses wrap around, so the top of memory is four bytes behind address zero.
        Assert.Equal([Encoder.Encode(Op.Jal, imm: -4)], Assemble("jal zero, 0xfffffffc").Words());
        Assert.Equal([Encoder.Encode(Op.Jal, imm: -4)], Assemble("jal zero, -4").Words());
    }

    [Fact]
    public void LocalLabelsWorkAsBranchTargets()
    {
        var program = Assemble("1: beq a0, a1, 1f\n   bne a0, a1, 1b\n1: jal zero, 1b");

        Assert.Equal(
            [
                Encoder.Encode(Op.Beq, rs1: 10, rs2: 11, imm: 8),
                Encoder.Encode(Op.Bne, rs1: 10, rs2: 11, imm: -4),
                Encoder.Encode(Op.Jal, imm: 0),
            ],
            program.Words());
    }

    [Fact]
    public void HiAndLoSplitAnAddressSoThatTheHalvesAddUp()
    {
        // 0x10000800 has bit 11 set, so its low part is negative and its high part is one more.
        var program = Assemble("""
            .data
                .zero 0x800
            msg: .word 0
            .text
                lui  a0, %hi(msg)
                addi a0, a0, %lo(msg)
                lw   a1, %lo(msg)(a0)
                sw   a1, %lo(msg + 4)(a0)
                lui  a2, %hi(0x12345678)
                addi a2, a2, %lo(0x12345678)
            """);

        Assert.Equal(0x1000_0800u, program.AddressOf("msg"));
        Assert.Equal(
            [
                Encoder.Encode(Op.Lui, rd: 10, imm: 0x10001000),
                Encoder.Encode(Op.Addi, rd: 10, rs1: 10, imm: -2048),
                Encoder.Encode(Op.Lw, rd: 11, rs1: 10, imm: -2048),
                Encoder.Encode(Op.Sw, rs1: 10, rs2: 11, imm: -2044),
                Encoder.Encode(Op.Lui, rd: 12, imm: 0x12345000),
                Encoder.Encode(Op.Addi, rd: 12, rs1: 12, imm: 0x678),
            ],
            program.Words());
    }

    [Fact]
    public void PcrelLoTakesTheLabelOfTheInstructionWithItsPcrelHi()
    {
        var program = Assemble("""
            .data
                .zero 0x7FC
            msg: .word 0
            .text
            1:  auipc a0, %pcrel_hi(msg)
                addi  a0, a0, %pcrel_lo(1b)
                lw    a1, %pcrel_lo(later)(a2)
            later:
                auipc a2, %pcrel_hi(msg)
            """);

        // From address 0: 0x100007fc - 0 = 0x100007fc. From address 12: 0x100007f0.
        Assert.Equal(
            [
                Encoder.Encode(Op.Auipc, rd: 10, imm: 0x10000000),
                Encoder.Encode(Op.Addi, rd: 10, rs1: 10, imm: 0x7FC),
                Encoder.Encode(Op.Lw, rd: 11, rs1: 12, imm: 0x7F0),
                Encoder.Encode(Op.Auipc, rd: 12, imm: 0x10000000),
            ],
            program.Words());
    }

    [Fact]
    public void InstructionsAndDataCanShareTheTextSection()
    {
        var program = Assemble("    ecall\ntable: .word 1, 2\n    ebreak\n    .byte 1\n    .align 2\n    mret");

        Assert.Equal([0x00000073u, 1u, 2u, 0x00100073u, 1u, 0x30200073u], program.Words());
        Assert.Equal(4u, program.AddressOf("table"));
    }

    [Fact]
    public void EveryInstructionReassemblesFromItsCanonicalDisassembly()
    {
        // The disassembler writes what the assembler reads: for random valid fields of every
        // instruction, at a random address, the text assembles back to the same word.
        var random = new SeededRandom(0xF37C_2501);
        foreach (var def in InstructionSet.All)
        {
            for (var i = 0; i < 300; i++)
            {
                var fields = EncoderTests.RandomFields(def, random);
                var word = Encoder.Encode(fields);
                var pc = random.NextUInt32() & ~3u;
                var text = Disassembler.Format(word, pc, DisassemblyOptions.Canonical);

                var result = Assembler.Assemble(text, new AssemblerOptions { TextBase = pc, DataBase = pc ^ 0x8000_0000 });
                if (!result.Success || result.Program!.Words() is not [var back] || back != word)
                {
                    Assert.Fail($"seed {random.Seed:X}: {word:x8} at {pc:x8} is '{text}', which gave:\n"
                        + (result.Success ? $"{result.Program!.Words()[0]:x8}" : result.RenderDiagnostics()));
                }
            }
        }
    }

    [Theory]
    [InlineData("adid a0, a0, 1", 1, 1, "unknown instruction 'adid'", "did you mean 'addi'?")]
    [InlineData("addw a0, a0, a1", 1, 1, "unknown instruction 'addw'", "did you mean 'addi'?")]
    [InlineData("frobnicate", 1, 1, "unknown instruction 'frobnicate'", null)]
    [InlineData("addi a0, a0", 1, 1, "'addi' does not take these operands", "usage: addi rd, rs1, imm")]
    [InlineData("addi a0, a0, a1", 1, 1, "'addi' does not take these operands", "usage: addi rd, rs1, imm")]
    [InlineData("add a0, a0, 1", 1, 1, "'add' does not take these operands", "usage: add rd, rs1, rs2")]
    [InlineData("lw a0, a1", 1, 1, "'lw' does not take these operands", "usage: lw rd, offset(rs1)")]
    [InlineData("sw a0, 4", 1, 1, "'sw' does not take these operands", "usage: sw rs2, offset(rs1)")]
    [InlineData("ecall a0", 1, 1, "'ecall' does not take these operands", "usage: ecall")]
    [InlineData("fence rw", 1, 1, "'fence' does not take these operands", "usage: fence  or  fence pred, succ")]
    [InlineData("addi a0, a0, 2048", 1, 14, "2048 does not fit in 12 signed bits (-2048 to 2047)", "use li to load a constant of any size")]
    [InlineData("addi a0, a0, -2049", 1, 14, "-2049 does not fit in 12 signed bits (-2048 to 2047)", "use li to load a constant of any size")]
    [InlineData("andi a0, a0, 0xFFF", 1, 14, "4095 does not fit in 12 signed bits (-2048 to 2047)", "as 12 bits that is -1; write -1 if those bits are what you mean")]
    [InlineData("lw a0, 4096(a1)", 1, 8, "4096 does not fit in 12 signed bits (-2048 to 2047)", null)]
    [InlineData("sw a0, -3000(a1)", 1, 8, "-3000 does not fit in 12 signed bits (-2048 to 2047)", null)]
    [InlineData("slli a0, a0, 32", 1, 14, "a shift amount is 0 to 31, not 32", null)]
    [InlineData("srai a0, a0, -1", 1, 14, "a shift amount is 0 to 31, not -1", null)]
    [InlineData("lui a0, 0x100000", 1, 9, "1048576 does not fit in 20 bits (0 to 1048575)", "to load the upper part of a constant or an address, write %hi(...)")]
    [InlineData("lui a0, -1", 1, 9, "-1 does not fit in 20 bits (0 to 1048575)", "to load the upper part of a constant or an address, write %hi(...)")]
    [InlineData("csrrw a0, 4096, a1", 1, 11, "a CSR number is 0 to 4095, not 4096", null)]
    [InlineData("csrrw a0, mstatsu, a1", 1, 11, "unknown CSR 'mstatsu'", "did you mean 'mstatus'?")]
    [InlineData("csrrwi a0, mstatus, 32", 1, 21, "a CSR immediate is 0 to 31, not 32", "put a larger value in a register first")]
    [InlineData("fence rw, wr", 1, 11, "a fence set is some of the letters i, o, r, w, in that order", "for example: fence rw, rw")]
    [InlineData("fence 1, rw", 1, 7, "a fence set is some of the letters i, o, r, w, in that order", "for example: fence rw, rw")]
    [InlineData("beq a0, a1, nowhere", 1, 13, "undefined symbol 'nowhere'", null)]
    [InlineData("beq a0, a1, 3", 1, 13, "the target is +3 bytes away, and an odd distance cannot be encoded", null)]
    [InlineData("jal ra, 0x1FFFFFFFF", 1, 9, "8589934591 is not a 32-bit address", null)]
    [InlineData(".data\naddi a0, a0, 1", 2, 1, "an instruction in .data", "code belongs in .text; put .text before it")]
    [InlineData(".byte 1\naddi a0, a0, 1", 2, 1, "this instruction would start at offset 1, which is not a multiple of 4", "put .align 2 before it")]
    [InlineData("addi a0, a0, %pcrel_lo(here)\nhere: ecall", 1, 24, "%pcrel_lo takes the label of the instruction that has the matching %pcrel_hi", "1: auipc a0, %pcrel_hi(msg)  then  addi a0, a0, %pcrel_lo(1b)")]
    [InlineData("addi a0, A0, 1", 1, 10, "'A0' is not a register", "did you mean 'a0'?")]
    [InlineData("addi a0, a8, 1", 1, 10, "'a8' is not a register", "did you mean 'a7'?")]
    [InlineData("add a0, a1, total", 1, 13, "'total' is not a register", null)]
    public void MistakesInInstructionsAreExplained(string source, int line, int column, string message, string? hint)
    {
        var diagnostic = DiagnoseOne(source);

        Assert.Equal(message, diagnostic.Message);
        Assert.Equal((line, column), (diagnostic.Span.Line, diagnostic.Span.Column));
        Assert.Equal(hint, diagnostic.Hint);
    }

    [Fact]
    public void ABranchThatCannotReachSaysHowFarItIs()
    {
        var far = DiagnoseOne("beq a0, a1, far\n.zero 4096\nfar: ecall");
        Assert.Equal("the target is +4100 bytes away; a branch reaches -4096 to +4094", far.Message);
        Assert.Equal("branch the other way around a 'j', which reaches further", far.Hint);

        // The last distance that fits, in each direction.
        Assemble("beq a0, a1, far\n.zero 4088\nfar: ecall");
        Assemble("back: ecall\n.zero 4092\nbeq a0, a1, back");

        var jump = DiagnoseOne("jal zero, 0x100000");
        Assert.Equal("the target is +1048576 bytes away; a jump reaches -1048576 to +1048574", jump.Message);
    }

    [Fact]
    public void AUserSymbolCanStandForACsrNumber()
    {
        var program = Assemble(".equ MYCSR, 0x340\ncsrrw a0, MYCSR, a1\ncsrrw a0, mscratch, a1");

        Assert.Equal(program.Words()[1], program.Words()[0]);
    }

    [Fact]
    public void EveryMnemonicInTheTableIsKnownToTheAssembler()
    {
        Assert.All(InstructionSet.All, def => Assert.Contains(def.Mnemonic, Assembler.Mnemonics));
    }
}
