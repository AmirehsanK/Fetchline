using Fetchline.Core.Isa;

namespace Fetchline.Tests.Isa;

public class BitsTests
{
    // Words taken from the RISC-V specification's examples and hand-checked against it.
    [Fact]
    public void RegisterFieldsAreWhereTheSpecificationPutsThem()
    {
        // add x3, x1, x2
        const uint word = 0x002081B3;
        Assert.Equal(0x33, Bits.Opcode(word));
        Assert.Equal(3, Bits.Rd(word));
        Assert.Equal(0, Bits.Funct3(word));
        Assert.Equal(1, Bits.Rs1(word));
        Assert.Equal(2, Bits.Rs2(word));
        Assert.Equal(0, Bits.Funct7(word));
    }

    [Theory]
    [InlineData(0xFFF00093u, -1)]      // addi x1, x0, -1
    [InlineData(0x7FF00093u, 2047)]    // addi x1, x0, 2047
    [InlineData(0x80000093u, -2048)]   // addi x1, x0, -2048
    [InlineData(0x00C12203u, 12)]      // lw x4, 12(x2)
    public void ImmediateOfAnIType(uint word, int expected) => Assert.Equal(expected, Bits.ImmI(word));

    [Theory]
    [InlineData(0x00112623u, 12)]      // sw x1, 12(x2)
    [InlineData(0xFE112E23u, -4)]      // sw x1, -4(x2)
    public void ImmediateOfAnSType(uint word, int expected) => Assert.Equal(expected, Bits.ImmS(word));

    [Theory]
    [InlineData(0x00208463u, 8)]       // beq x1, x2, +8
    [InlineData(0xFE208EE3u, -4)]      // beq x1, x2, -4
    [InlineData(0x7E208FE3u, 4094)]    // beq x1, x2, +4094
    [InlineData(0x80208063u, -4096)]   // beq x1, x2, -4096
    public void ImmediateOfABType(uint word, int expected) => Assert.Equal(expected, Bits.ImmB(word));

    [Theory]
    [InlineData(0x123450B7u, 0x12345000)]            // lui x1, 0x12345
    [InlineData(0xFFFFF0B7u, unchecked((int)0xFFFFF000))]  // lui x1, 0xFFFFF
    public void ImmediateOfAUType(uint word, int expected) => Assert.Equal(expected, Bits.ImmU(word));

    [Theory]
    [InlineData(0x008000EFu, 8)]           // jal x1, +8
    [InlineData(0xFFDFF0EFu, -4)]          // jal x1, -4
    [InlineData(0x7FFFF0EFu, 0xFFFFE)]     // jal x1, +1048574
    [InlineData(0x800000EFu, -0x100000)]   // jal x1, -1048576
    public void ImmediateOfAJType(uint word, int expected) => Assert.Equal(expected, Bits.ImmJ(word));

    [Fact]
    public void ITypeAndSTypeImmediatesSurviveAPackAndUnpackOverTheirWholeRange()
    {
        for (var imm = -2048; imm <= 2047; imm++)
        {
            Assert.Equal(imm, Bits.ImmI(Bits.PackI(imm)));
            Assert.Equal(imm, Bits.ImmS(Bits.PackS(imm)));
        }
    }

    [Fact]
    public void BTypeImmediatesSurviveAPackAndUnpackOverTheirWholeRange()
    {
        for (var imm = -4096; imm <= 4094; imm += 2)
        {
            Assert.Equal(imm, Bits.ImmB(Bits.PackB(imm)));
        }
    }

    [Fact]
    public void JTypeImmediatesSurviveAPackAndUnpackOverTheirWholeRange()
    {
        for (var imm = -0x100000; imm <= 0xFFFFE; imm += 2)
        {
            if (Bits.ImmJ(Bits.PackJ(imm)) != imm)
            {
                Assert.Fail($"J immediate {imm} came back as {Bits.ImmJ(Bits.PackJ(imm))}");
            }
        }
    }

    [Fact]
    public void UTypeImmediatesSurviveAPackAndUnpack()
    {
        for (long upper = 0; upper <= 0xFFFFF; upper += 0x111)
        {
            var imm = (int)(upper << 12);
            Assert.Equal(imm, Bits.ImmU(Bits.PackU(imm)));
        }
    }

    [Fact]
    public void EachPackTouchesOnlyTheBitsOfItsOwnFormat()
    {
        // Packing all-ones must set exactly the immediate's bits and leave opcode, registers and
        // funct3 clear, or two fields of an encoded instruction would overlap.
        Assert.Equal(0xFFF00000u, Bits.PackI(-1));
        Assert.Equal(0xFE000F80u, Bits.PackS(-1));
        Assert.Equal(0xFE000F80u, Bits.PackB(-2));
        Assert.Equal(0xFFFFF000u, Bits.PackU(-1));
        Assert.Equal(0xFFFFF000u, Bits.PackJ(-2));
        Assert.Equal(0x00000F80u, Bits.PackRd(31));
        Assert.Equal(0x000F8000u, Bits.PackRs1(31));
        Assert.Equal(0x01F00000u, Bits.PackRs2(31));
    }
}
