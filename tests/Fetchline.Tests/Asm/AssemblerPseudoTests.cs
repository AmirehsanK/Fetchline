using Fetchline.Core.Asm;
using Fetchline.Core.Isa;
using Fetchline.Tests.Support;
using static Fetchline.Tests.Asm.AssemblerTesting;

namespace Fetchline.Tests.Asm;

/// <summary>The pseudo-instructions: what each one really is.</summary>
public class AssemblerPseudoTests
{
    private const int A0 = 10, A1 = 11, Ra = 1, T1 = 6;

    private static string[] Real(string source, AssemblerOptions? options = null)
    {
        var program = Assemble(source, options);
        return [.. program.Words().Select((word, i) =>
            Disassembler.Format(word, program.Text!.Address + (uint)(4 * i), DisassemblyOptions.Canonical))];
    }

    [Theory]
    [InlineData("nop", "addi zero, zero, 0")]
    [InlineData("mv a0, a1", "addi a0, a1, 0")]
    [InlineData("not a0, a1", "xori a0, a1, -1")]
    [InlineData("neg a0, a1", "sub a0, zero, a1")]
    [InlineData("seqz a0, a1", "sltiu a0, a1, 1")]
    [InlineData("snez a0, a1", "sltu a0, zero, a1")]
    [InlineData("sltz a0, a1", "slt a0, a1, zero")]
    [InlineData("sgtz a0, a1", "slt a0, zero, a1")]
    [InlineData("zext.b a0, a1", "andi a0, a1, 255")]
    [InlineData("beqz a0, 0x40", "beq a0, zero, 0x40")]
    [InlineData("bnez a0, 0x40", "bne a0, zero, 0x40")]
    [InlineData("bgez a0, 0x40", "bge a0, zero, 0x40")]
    [InlineData("bltz a0, 0x40", "blt a0, zero, 0x40")]
    [InlineData("blez a0, 0x40", "bge zero, a0, 0x40")]
    [InlineData("bgtz a0, 0x40", "blt zero, a0, 0x40")]
    [InlineData("bgt a0, a1, 0x40", "blt a1, a0, 0x40")]
    [InlineData("ble a0, a1, 0x40", "bge a1, a0, 0x40")]
    [InlineData("bgtu a0, a1, 0x40", "bltu a1, a0, 0x40")]
    [InlineData("bleu a0, a1, 0x40", "bgeu a1, a0, 0x40")]
    [InlineData("j 0x40", "jal zero, 0x40")]
    [InlineData("jal 0x40", "jal ra, 0x40")]
    [InlineData("jr a0", "jalr zero, 0(a0)")]
    [InlineData("jalr a0", "jalr ra, 0(a0)")]
    [InlineData("jalr t0, a0", "jalr t0, 0(a0)")]
    [InlineData("jalr t0, a0, 12", "jalr t0, 12(a0)")]
    [InlineData("ret", "jalr zero, 0(ra)")]
    [InlineData("csrr a0, mcause", "csrrs a0, mcause, zero")]
    [InlineData("csrw mtvec, a0", "csrrw zero, mtvec, a0")]
    [InlineData("csrs mstatus, a0", "csrrs zero, mstatus, a0")]
    [InlineData("csrc mstatus, a0", "csrrc zero, mstatus, a0")]
    [InlineData("csrwi mstatus, 8", "csrrwi zero, mstatus, 8")]
    [InlineData("csrsi mstatus, 8", "csrrsi zero, mstatus, 8")]
    [InlineData("csrci mstatus, 8", "csrrci zero, mstatus, 8")]
    [InlineData("rdcycle a0", "csrrs a0, cycle, zero")]
    [InlineData("rdinstret a0", "csrrs a0, instret, zero")]
    [InlineData("rdcycleh a0", "csrrs a0, cycleh, zero")]
    [InlineData("rdinstreth a0", "csrrs a0, instreth, zero")]
    public void APseudoInstructionIsOneRealInstruction(string pseudo, string real)
    {
        Assert.Equal([real], Real(pseudo));
    }

    [Theory]
    [InlineData("0", "addi a0, zero, 0")]
    [InlineData("5", "addi a0, zero, 5")]
    [InlineData("-1", "addi a0, zero, -1")]
    [InlineData("2047", "addi a0, zero, 2047")]
    [InlineData("-2048", "addi a0, zero, -2048")]
    [InlineData("0xFFFFFFFF", "addi a0, zero, -1")]             // the same 32 bits as -1
    [InlineData("0xFFFFF800", "addi a0, zero, -2048")]
    [InlineData("0x1000", "lui a0, 0x1")]
    [InlineData("0x12345000", "lui a0, 0x12345")]
    [InlineData("0x80000000", "lui a0, 0x80000")]
    [InlineData("-0x80000000", "lui a0, 0x80000")]
    [InlineData("'A'", "addi a0, zero, 65")]
    [InlineData("3 * 4 + 1", "addi a0, zero, 13")]
    public void LiIsOneInstructionWhenOneIsEnough(string value, string real)
    {
        Assert.Equal([real], Real($"li a0, {value}"));
    }

    [Theory]
    [InlineData("2048", "lui a0, 0x1", "addi a0, a0, -2048")]         // the low part is negative, so the high part is one more
    [InlineData("-2049", "lui a0, 0xfffff", "addi a0, a0, 2047")]
    [InlineData("0x12345678", "lui a0, 0x12345", "addi a0, a0, 1656")]
    [InlineData("0x12345FFF", "lui a0, 0x12346", "addi a0, a0, -1")]
    [InlineData("0x7FFFFFFF", "lui a0, 0x80000", "addi a0, a0, -1")]
    [InlineData("0x80000001", "lui a0, 0x80000", "addi a0, a0, 1")]
    [InlineData("0xDEADBEEF", "lui a0, 0xdeadc", "addi a0, a0, -273")]
    [InlineData("-123456", "lui a0, 0xfffe2", "addi a0, a0, -576")]
    public void LiIsTwoInstructionsOtherwise(string value, string upper, string lower)
    {
        Assert.Equal([upper, lower], Real($"li a0, {value}"));
    }

    [Fact]
    public void WhatLiLoadsIsWhatWasAskedFor()
    {
        // lui and addi both work in 32 bits with a signed 12-bit addend: check the sum.
        var random = new SeededRandom(0xF37C_2601);
        for (var i = 0; i < 5000; i++)
        {
            var value = i < 64 ? (int)(1u << (i % 32)) - (i / 32) : (int)random.NextUInt32();
            var words = Assemble($"li t0, {value}").Words();

            var result = 0;
            foreach (var word in words)
            {
                var instruction = Decoder.Decode(word);
                result = instruction.Op == Op.Lui ? instruction.Imm : unchecked(result + instruction.Imm);
                Assert.Equal(5, instruction.Rd);
            }

            if (result != value)
            {
                Assert.Fail($"seed {random.Seed:X}: li t0, {value} loaded {result}");
            }
        }
    }

    [Fact]
    public void TheSizeOfLiIsFixedByWhatTheFirstPassCanSee()
    {
        var program = Assemble("""
            .equ SMALL, 100
            .equ PAGE, 0x4000
            .data
            buffer: .zero 16
            buffer_end:
            .text
                li a0, SMALL                 # known and small: one instruction
                li a1, PAGE                  # known, low bits zero: one
                li a2, buffer_end - buffer   # a distance inside one section: known, one
                li a3, LATER                 # defined below: two, although it would fit in one
                li a4, buffer                # an address: two
                li a5, after - before        # labels not seen yet: two
            before:
                nop
            after:
            .equ LATER, 7
            """);

        Assert.Equal(
            [
                "addi a0, zero, 100",
                "lui a1, 0x4",
                "addi a2, zero, 16",
                "lui a3, 0x0", "addi a3, a3, 7",
                "lui a4, 0x10000", "addi a4, a4, 0",
                "lui a5, 0x0", "addi a5, a5, 4",
                "addi zero, zero, 0",
            ],
            program.Words().Select(word => Disassembler.Format(word, 0, DisassemblyOptions.Canonical)));

        // The labels after the li's sit where the sizes above put them.
        Assert.Equal(36u, program.AddressOf("before"));
        Assert.Equal(40u, program.AddressOf("after"));
    }

    [Theory]
    [InlineData("li a0, 0x100000000", "4294967296 does not fit in 32 bits")]
    [InlineData("li a0, -0x80000001", "-2147483649 does not fit in 32 bits")]
    public void LiRefusesWhatDoesNotFitInARegister(string source, string message)
    {
        var diagnostic = DiagnoseOne(source);

        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(8, diagnostic.Span.Column);
    }

    [Fact]
    public void LaReachesASymbolFromWhereverItIs()
    {
        var program = Assemble("""
            .data
                .zero 0x7FC
            near_page_end: .word 0
            .text
                nop
                la a0, near_page_end
                la a1, here
            here:
                lla a0, near_page_end
            """);

        // From address 4, the target 0x100007fc is 0x100007f8 away: high 0x10000, low 0x7f8... and
        // 0x7f8 has bit 11 clear, so no carry. From 12 to 20 is just +8.
        Assert.Equal(
            [
                Encoder.Encode(Op.Addi),
                Encoder.Encode(Op.Auipc, rd: A0, imm: 0x10000000),
                Encoder.Encode(Op.Addi, rd: A0, rs1: A0, imm: 0x7F8),
                Encoder.Encode(Op.Auipc, rd: A1, imm: 0),
                Encoder.Encode(Op.Addi, rd: A1, rs1: A1, imm: 8),
                Encoder.Encode(Op.Auipc, rd: A0, imm: 0x10000000),
                Encoder.Encode(Op.Addi, rd: A0, rs1: A0, imm: 0x7E8),
            ],
            program.Words());
    }

    [Fact]
    public void LaCarriesIntoTheUpperPartWhenTheLowerPartIsNegative()
    {
        // The distance is 0x800: its low part is -2048, so the high part must be 0x1000.
        var program = Assemble("la a0, target\n.zero 0x7F8\ntarget: nop");

        Assert.Equal(Encoder.Encode(Op.Auipc, rd: A0, imm: 0x1000), program.Words()[0]);
        Assert.Equal(Encoder.Encode(Op.Addi, rd: A0, rs1: A0, imm: -2048), program.Words()[1]);
    }

    [Fact]
    public void CallAndTailReachAnywhere()
    {
        var program = Assemble("""
            main:
                call helper
                tail helper
                call main
            .zero 0x100000
            helper:
                ret
            """);

        // helper is at 24 + 1 MiB: out of reach of a plain jal from here, which is the point.
        Assert.Equal(0x100018u, program.AddressOf("helper"));
        Assert.Equal(
            [
                Encoder.Encode(Op.Auipc, rd: Ra, imm: 0x100000),
                Encoder.Encode(Op.Jalr, rd: Ra, rs1: Ra, imm: 0x18),
                Encoder.Encode(Op.Auipc, rd: T1, imm: 0x100000),
                Encoder.Encode(Op.Jalr, rd: 0, rs1: T1, imm: 0x10),
                Encoder.Encode(Op.Auipc, rd: Ra, imm: 0),
                Encoder.Encode(Op.Jalr, rd: Ra, rs1: Ra, imm: -16),
            ],
            program.Words()[..6]);
    }

    [Fact]
    public void PseudoBranchesReachLabels()
    {
        var program = Assemble("loop:\n  beqz a0, done\n  bgt a0, a1, loop\n  j loop\ndone:\n  ret");

        Assert.Equal(
            [
                Encoder.Encode(Op.Beq, rs1: A0, rs2: 0, imm: 12),
                Encoder.Encode(Op.Blt, rs1: A1, rs2: A0, imm: -4),
                Encoder.Encode(Op.Jal, imm: -8),
                Encoder.Encode(Op.Jalr, rs1: Ra),
            ],
            program.Words());
    }

    [Theory]
    [InlineData("mv a0, 5", "'mv' does not take these operands", "usage: mv rd, rs")]
    [InlineData("li a0", "'li' does not take these operands", "usage: li rd, imm")]
    [InlineData("ret a0", "'ret' does not take these operands", "usage: ret")]
    [InlineData("call", "'call' does not take these operands", "usage: call label")]
    [InlineData("jal a0, a1", "'jal' does not take these operands", "usage: jal rd, label  or  jal label")]
    [InlineData("jalr 4(a0), a1", "'jalr' does not take these operands", "usage: jalr rd, offset(rs1)  or  jalr rs  or  jalr rd, rs  or  jalr rd, rs, offset")]
    [InlineData("csrwi mstatus, 99", "a CSR immediate is 0 to 31, not 99", "put a larger value in a register first")]
    [InlineData("csrw mstatsu, a0", "unknown CSR 'mstatsu'", "did you mean 'mstatus'?")]
    [InlineData("beqz a8, 0", "'a8' is not a register", "did you mean 'a7'?")]
    [InlineData("j nowhere", "undefined symbol 'nowhere'", null)]
    [InlineData("la a0, nowhere", "undefined symbol 'nowhere'", null)]
    [InlineData("li a0, nowhere", "undefined symbol 'nowhere'", null)]
    public void MistakesInPseudoInstructionsAreExplained(string source, string message, string? hint)
    {
        var diagnostic = DiagnoseOne(source);

        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(hint, diagnostic.Hint);
    }

    // What riscv-opcodes says each pseudo-instruction must look like, checked against what the
    // assembler makes of one instance of it.
    [Theory]
    [InlineData("mv", "mv a0, a1")]
    [InlineData("neg", "neg a0, a1")]
    [InlineData("nop", "nop")]
    [InlineData("zext.b", "zext.b a0, a1")]
    [InlineData("ret", "ret")]
    [InlineData("bleu", "bleu a0, a1, 16")]
    [InlineData("bgtu", "bgtu a0, a1, 16")]
    [InlineData("ble", "ble a0, a1, 16")]
    [InlineData("bgez", "bgez a0, 16")]
    [InlineData("blez", "blez a0, 16")]
    [InlineData("bgt", "bgt a0, a1, 16")]
    [InlineData("bgtz", "bgtz a0, 16")]
    [InlineData("bltz", "bltz a0, 16")]
    [InlineData("bnez", "bnez a0, 16")]
    [InlineData("beqz", "beqz a0, 16")]
    [InlineData("seqz", "seqz a0, a1")]
    [InlineData("snez", "snez a0, a1")]
    [InlineData("sltz", "sltz a0, a1")]
    [InlineData("sgtz", "sgtz a0, a1")]
    [InlineData("jalr", "jalr a0")]
    [InlineData("jr", "jr a0")]
    [InlineData("jal", "jal 16")]
    [InlineData("j", "j 16")]
    [InlineData("csrr", "csrr a0, mstatus")]
    [InlineData("csrw", "csrw mstatus, a0")]
    [InlineData("csrs", "csrs mstatus, a0")]
    [InlineData("csrc", "csrc mstatus, a0")]
    [InlineData("csrwi", "csrwi mstatus, 5")]
    [InlineData("csrsi", "csrsi mstatus, 5")]
    [InlineData("csrci", "csrci mstatus, 5")]
    public void APseudoInstructionMatchesItsOfficialDefinition(string name, string instance)
    {
        var official = OpcodesFile.ReadAll().Single(o => o.IsPseudo && o.Name == name);
        var word = Assert.Single(Assemble(instance).Words());

        Assert.True(
            (word & official.Mask) == official.Match,
            $"'{instance}' assembled to {word:x8}; riscv-opcodes wants match {official.Match:x8} under mask {official.Mask:x8}");
        Assert.Equal(official.Base, InstructionSet.Get(Decoder.Decode(word).Op).Mnemonic);
    }

    [Fact]
    public void EveryOfficialPseudoOpIsEitherSupportedOrLeftOutOnPurpose()
    {
        // The RV32 shift encodings are real instructions here; the rest are things this core has
        // no use for: the old names of ecall and ebreak, and two specialised fences.
        string[] leftOut = ["slli", "srli", "srai", "slli_rv32", "srli_rv32", "srai_rv32", "scall", "sbreak", "fence.tso", "pause"];

        var unsupported = OpcodesFile.ReadAll()
            .Where(o => o.IsPseudo && !leftOut.Contains(o.Name))
            .Select(o => o.Name)
            .Where(name => !Assembler.Mnemonics.Contains(name));

        Assert.Empty(unsupported);
        Assert.All(leftOut.Except(["slli", "srli", "srai"]), name => Assert.DoesNotContain(name, Assembler.Mnemonics));
    }
}
