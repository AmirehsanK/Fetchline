using System.Numerics;
using Fetchline.Core.Isa;
using Fetchline.Core.Machine;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Machine;

/// <summary>The edge cases of every operation, one row each, from the ISA manual.</summary>
public class ExecTests
{
    private const uint Min = 0x8000_0000;   // -2^31
    private const uint Max = 0x7FFF_FFFF;   // 2^31 - 1
    private const uint Ones = 0xFFFF_FFFF;  // -1

    [Theory]
    // add and sub wrap around
    [InlineData(AluOp.Add, 1u, 2u, 3u)]
    [InlineData(AluOp.Add, Max, 1u, Min)]
    [InlineData(AluOp.Add, Ones, 1u, 0u)]
    [InlineData(AluOp.Add, Ones, Ones, 0xFFFF_FFFEu)]
    [InlineData(AluOp.Sub, 3u, 5u, 0xFFFF_FFFEu)]
    [InlineData(AluOp.Sub, Min, 1u, Max)]
    [InlineData(AluOp.Sub, 0u, Min, Min)]
    // shifts use only the low five bits of the amount
    [InlineData(AluOp.Sll, 1u, 31u, Min)]
    [InlineData(AluOp.Sll, 1u, 32u, 1u)]
    [InlineData(AluOp.Sll, 1u, 33u, 2u)]
    [InlineData(AluOp.Sll, Ones, 0xFFFF_FFE4u, 0xFFFF_FFF0u)]
    [InlineData(AluOp.Srl, Min, 31u, 1u)]
    [InlineData(AluOp.Srl, Min, 32u, Min)]
    [InlineData(AluOp.Srl, Ones, 4u, 0x0FFF_FFFFu)]
    [InlineData(AluOp.Sra, Min, 31u, Ones)]
    [InlineData(AluOp.Sra, Min, 4u, 0xF800_0000u)]
    [InlineData(AluOp.Sra, Max, 4u, 0x07FF_FFFFu)]
    [InlineData(AluOp.Sra, Ones, 63u, Ones)]
    // comparisons: signed and unsigned disagree when the top bit is set
    [InlineData(AluOp.Slt, Ones, 0u, 1u)]
    [InlineData(AluOp.Slt, 0u, Ones, 0u)]
    [InlineData(AluOp.Slt, Min, Max, 1u)]
    [InlineData(AluOp.Slt, 5u, 5u, 0u)]
    [InlineData(AluOp.Sltu, Ones, 0u, 0u)]
    [InlineData(AluOp.Sltu, 0u, Ones, 1u)]
    [InlineData(AluOp.Sltu, Min, Max, 0u)]
    [InlineData(AluOp.Sltu, 0u, 1u, 1u)]      // sltiu rd, rs, 1 is "seqz"
    [InlineData(AluOp.Sltu, 1u, 1u, 0u)]
    // logic
    [InlineData(AluOp.Xor, 0xFF00_FF00u, 0x0FF0_0FF0u, 0xF0F0_F0F0u)]
    [InlineData(AluOp.Xor, 0x1234_5678u, Ones, 0xEDCB_A987u)]
    [InlineData(AluOp.Or, 0xFF00_FF00u, 0x0FF0_0FF0u, 0xFFF0_FFF0u)]
    [InlineData(AluOp.And, 0xFF00_FF00u, 0x0FF0_0FF0u, 0x0F00_0F00u)]
    // multiply: the low word is the same for signed and unsigned
    [InlineData(AluOp.Mul, 7u, 6u, 42u)]
    [InlineData(AluOp.Mul, Ones, Ones, 1u)]
    [InlineData(AluOp.Mul, Min, 2u, 0u)]
    [InlineData(AluOp.Mul, 0x0001_0000u, 0x0001_0000u, 0u)]
    // the high word depends on how each operand is read
    [InlineData(AluOp.Mulh, Ones, Ones, 0u)]                     // -1 * -1 = 1
    [InlineData(AluOp.Mulh, Min, Min, 0x4000_0000u)]             // 2^62
    [InlineData(AluOp.Mulh, Min, Max, 0xC000_0000u)]
    [InlineData(AluOp.Mulh, Ones, 1u, Ones)]                     // -1 * 1 = -1
    [InlineData(AluOp.Mulhu, Ones, Ones, 0xFFFF_FFFEu)]
    [InlineData(AluOp.Mulhu, Min, Min, 0x4000_0000u)]
    [InlineData(AluOp.Mulhu, Ones, 1u, 0u)]
    [InlineData(AluOp.Mulhsu, Ones, Ones, Ones)]                 // -1 * (2^32 - 1)
    [InlineData(AluOp.Mulhsu, Min, Ones, 0x8000_0000u)]
    [InlineData(AluOp.Mulhsu, 1u, Ones, 0u)]
    [InlineData(AluOp.Mulhsu, Ones, 1u, Ones)]
    // divide: rounds towards zero
    [InlineData(AluOp.Div, 20u, 6u, 3u)]
    [InlineData(AluOp.Div, 0xFFFF_FFECu, 6u, 0xFFFF_FFFDu)]      // -20 / 6 = -3
    [InlineData(AluOp.Div, 20u, 0xFFFF_FFFAu, 0xFFFF_FFFDu)]     // 20 / -6 = -3
    [InlineData(AluOp.Div, 0xFFFF_FFECu, 0xFFFF_FFFAu, 3u)]      // -20 / -6 = 3
    [InlineData(AluOp.Divu, 0xFFFF_FFECu, 6u, 0x2AAA_AAA7u)]
    [InlineData(AluOp.Rem, 20u, 6u, 2u)]
    [InlineData(AluOp.Rem, 0xFFFF_FFECu, 6u, 0xFFFF_FFFEu)]      // the remainder has the dividend's sign
    [InlineData(AluOp.Rem, 20u, 0xFFFF_FFFAu, 2u)]
    [InlineData(AluOp.Rem, 0xFFFF_FFECu, 0xFFFF_FFFAu, 0xFFFF_FFFEu)]
    [InlineData(AluOp.Remu, 0xFFFF_FFECu, 6u, 2u)]
    // divide by zero does not trap: the quotient is all ones, the remainder is the dividend
    [InlineData(AluOp.Div, 5u, 0u, Ones)]
    [InlineData(AluOp.Div, 0u, 0u, Ones)]
    [InlineData(AluOp.Div, Min, 0u, Ones)]
    [InlineData(AluOp.Divu, 5u, 0u, Ones)]
    [InlineData(AluOp.Rem, 5u, 0u, 5u)]
    [InlineData(AluOp.Rem, Min, 0u, Min)]
    [InlineData(AluOp.Remu, 5u, 0u, 5u)]
    // the one signed overflow does not trap either
    [InlineData(AluOp.Div, Min, Ones, Min)]
    [InlineData(AluOp.Rem, Min, Ones, 0u)]
    [InlineData(AluOp.Divu, Min, Ones, 0u)]                      // unsigned: 2^31 / (2^32 - 1) is just 0
    [InlineData(AluOp.Remu, Min, Ones, Min)]
    public void TheAluAtItsEdges(AluOp op, uint a, uint b, uint expected)
    {
        Assert.Equal(expected, Exec.Alu(op, a, b));
    }

    [Fact]
    public void MultiplyAndDivideAgreeWithArbitraryPrecisionArithmetic()
    {
        // An independent statement of what the M extension means, in numbers that cannot overflow.
        static uint Low(BigInteger value) => (uint)(value & 0xFFFF_FFFF);
        static uint High(BigInteger value) => (uint)((value >> 32) & 0xFFFF_FFFF);

        uint[] corners = [0, 1, 2, Max, Min, Min + 1, Ones, Ones - 1, 0x0001_0000, 0xFFFF];
        var random = new SeededRandom(0xF37C_3201);

        for (var i = 0; i < 100_000; i++)
        {
            var a = i < 100 ? corners[i / 10] : random.Chance(15) ? random.Pick(corners) : random.NextUInt32();
            var b = i < 100 ? corners[i % 10] : random.Chance(15) ? random.Pick(corners) : random.NextUInt32();
            BigInteger sa = (int)a, sb = (int)b, ua = a, ub = b;

            var expected = new (AluOp Op, uint Value)[]
            {
                (AluOp.Mul, Low(ua * ub)),
                (AluOp.Mulh, High(sa * sb)),
                (AluOp.Mulhsu, High(sa * ub)),
                (AluOp.Mulhu, High(ua * ub)),
                (AluOp.Div, b == 0 ? Ones : Low(BigInteger.Divide(sa, sb))),
                (AluOp.Divu, b == 0 ? Ones : Low(BigInteger.Divide(ua, ub))),
                (AluOp.Rem, b == 0 ? a : Low(BigInteger.Remainder(sa, sb))),
                (AluOp.Remu, b == 0 ? a : Low(BigInteger.Remainder(ua, ub))),
            };

            foreach (var (op, value) in expected)
            {
                if (Exec.Alu(op, a, b) != value)
                {
                    Assert.Fail($"seed {random.Seed:X}: {op}({a:x8}, {b:x8}) gave {Exec.Alu(op, a, b):x8}, expected {value:x8}");
                }
            }

            // What the manual promises of a quotient and remainder taken together.
            if (b != 0)
            {
                Assert.Equal(a, unchecked((Exec.Alu(AluOp.Div, a, b) * b) + Exec.Alu(AluOp.Rem, a, b)));
                Assert.Equal(a, unchecked((Exec.Alu(AluOp.Divu, a, b) * b) + Exec.Alu(AluOp.Remu, a, b)));
            }
        }
    }

    [Theory]
    [InlineData(BranchKind.Beq, 5u, 5u, true)]
    [InlineData(BranchKind.Beq, 5u, 6u, false)]
    [InlineData(BranchKind.Bne, 5u, 6u, true)]
    [InlineData(BranchKind.Bne, 5u, 5u, false)]
    [InlineData(BranchKind.Blt, Ones, 0u, true)]       // -1 < 0
    [InlineData(BranchKind.Blt, 0u, Ones, false)]
    [InlineData(BranchKind.Blt, 5u, 5u, false)]
    [InlineData(BranchKind.Bge, 5u, 5u, true)]
    [InlineData(BranchKind.Bge, 0u, Ones, true)]
    [InlineData(BranchKind.Bge, Min, Max, false)]
    [InlineData(BranchKind.Bltu, 0u, Ones, true)]      // 0 < 2^32 - 1
    [InlineData(BranchKind.Bltu, Ones, 0u, false)]
    [InlineData(BranchKind.Bgeu, Ones, 0u, true)]
    [InlineData(BranchKind.Bgeu, 5u, 5u, true)]
    [InlineData(BranchKind.Bgeu, Max, Min, false)]
    [InlineData(BranchKind.Jal, 1u, 2u, true)]
    [InlineData(BranchKind.Jalr, 1u, 2u, true)]
    [InlineData(BranchKind.None, 5u, 5u, false)]
    public void BranchConditions(BranchKind kind, uint rs1, uint rs2, bool taken)
    {
        Assert.Equal(taken, Exec.Taken(kind, rs1, rs2));
    }

    [Fact]
    public void ATargetIsRelativeToTheInstructionExceptForJalr()
    {
        Assert.Equal(0x1010u, Exec.Target(BranchKind.Beq, 0x1000, 16, aluResult: 99));
        Assert.Equal(0x0FF0u, Exec.Target(BranchKind.Jal, 0x1000, -16, aluResult: 99));
        Assert.Equal(0xFFFF_FFFCu, Exec.Target(BranchKind.Jal, 0, -4, aluResult: 99));

        // jalr takes the ALU's sum and clears bit 0, so an odd address becomes the even one below.
        Assert.Equal(0x2000u, Exec.Target(BranchKind.Jalr, 0x1000, 16, aluResult: 0x2000));
        Assert.Equal(0x2000u, Exec.Target(BranchKind.Jalr, 0x1000, 16, aluResult: 0x2001));
        Assert.Equal(0x2002u, Exec.Target(BranchKind.Jalr, 0x1000, 16, aluResult: 0x2003));
    }

    [Theory]
    [InlineData(0x1234_5680u, 1, true, 0xFFFF_FF80u)]     // lb: bit 7 is the sign
    [InlineData(0x1234_5680u, 1, false, 0x0000_0080u)]    // lbu
    [InlineData(0x1234_567Fu, 1, true, 0x0000_007Fu)]
    [InlineData(0x1234_8000u, 2, true, 0xFFFF_8000u)]     // lh: bit 15 is the sign
    [InlineData(0x1234_8000u, 2, false, 0x0000_8000u)]    // lhu
    [InlineData(0x1234_7FFFu, 2, true, 0x0000_7FFFu)]
    [InlineData(0x8234_5678u, 4, true, 0x8234_5678u)]     // lw: nothing to extend
    public void ALoadIsExtendedToAWord(uint raw, int bytes, bool signed, uint expected)
    {
        Assert.Equal(expected, Exec.Extend(raw, bytes, signed));
    }

    [Fact]
    public void OperandsComeFromWhereTheControlSignalsSay()
    {
        Assert.Equal(7u, Exec.OperandA(SrcA.Rs1, rs1: 7, pc: 0x40));
        Assert.Equal(0x40u, Exec.OperandA(SrcA.Pc, rs1: 7, pc: 0x40));
        Assert.Equal(0u, Exec.OperandA(SrcA.Zero, rs1: 7, pc: 0x40));
        Assert.Equal(9u, Exec.OperandB(SrcB.Rs2, rs2: 9, imm: -1));
        Assert.Equal(Ones, Exec.OperandB(SrcB.Imm, rs2: 9, imm: -1));
    }

    [Fact]
    public void CsrInstructionsWriteSetOrClear()
    {
        Assert.Equal(0x0Fu, Exec.CsrUpdate(SystemOp.CsrWrite, old: 0xF0, operand: 0x0F));
        Assert.Equal(0xFFu, Exec.CsrUpdate(SystemOp.CsrSet, old: 0xF0, operand: 0x0F));
        Assert.Equal(0xC0u, Exec.CsrUpdate(SystemOp.CsrClear, old: 0xF0, operand: 0x30));
        Assert.Equal(0xF0u, Exec.CsrUpdate(SystemOp.None, old: 0xF0, operand: 0x0F));
    }

    [Fact]
    public void EveryInstructionWithAnAluOperationIsCoveredByAnEdgeCase()
    {
        // If an instruction is added with a new operation, this fails until it has a row above.
        var tested = typeof(ExecTests).GetMethod(nameof(TheAluAtItsEdges))!
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(InlineDataAttribute))
            .Select(a => (AluOp)((IList<System.Reflection.CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!)[0].Value!)
            .ToHashSet();

        Assert.All(InstructionSet.All, row => Assert.Contains(row.Control.Alu, tested));
        Assert.Equal(Enum.GetValues<AluOp>().Length, tested.Count);
    }
}
