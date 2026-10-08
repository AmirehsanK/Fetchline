namespace Fetchline.Core.Isa;

/// <summary>
/// Where the fields of a 32-bit instruction word are. RISC-V keeps <c>rd</c>, <c>rs1</c> and
/// <c>rs2</c> in fixed places and scatters each immediate around them, always with its sign in
/// bit 31, so that hardware can sign-extend before it knows the format.
/// </summary>
public static class Bits
{
    /// <summary>The seven-bit major opcode, bits 6..0.</summary>
    public static int Opcode(uint word) => (int)(word & 0x7F);

    /// <summary>Destination register, bits 11..7.</summary>
    public static int Rd(uint word) => (int)((word >> 7) & 31);

    /// <summary>Bits 14..12.</summary>
    public static int Funct3(uint word) => (int)((word >> 12) & 7);

    /// <summary>First source register, bits 19..15.</summary>
    public static int Rs1(uint word) => (int)((word >> 15) & 31);

    /// <summary>Second source register, bits 24..20. A shift amount sits in the same place.</summary>
    public static int Rs2(uint word) => (int)((word >> 20) & 31);

    /// <summary>Bits 31..25.</summary>
    public static int Funct7(uint word) => (int)(word >> 25);

    /// <summary>Bits 31..20 without sign extension: a CSR number, or the body of a <c>fence</c>.</summary>
    public static int Imm12Raw(uint word) => (int)(word >> 20);

    /// <summary>I-type immediate: bits 31..20, sign-extended.</summary>
    public static int ImmI(uint word) => (int)word >> 20;

    /// <summary>S-type immediate: bits 31..25 and 11..7, sign-extended.</summary>
    public static int ImmS(uint word) => (((int)word >> 25) << 5) | (int)((word >> 7) & 31);

    /// <summary>B-type immediate: a multiple of two in the range of 13 signed bits.</summary>
    public static int ImmB(uint word) =>
        (((int)word >> 31) << 12)
        | (int)(((word >> 7) & 1) << 11)
        | (int)(((word >> 25) & 0x3F) << 5)
        | (int)(((word >> 8) & 0xF) << 1);

    /// <summary>U-type immediate: bits 31..12 in place, with the low twelve bits zero.</summary>
    public static int ImmU(uint word) => (int)(word & 0xFFFFF000);

    /// <summary>J-type immediate: a multiple of two in the range of 21 signed bits.</summary>
    public static int ImmJ(uint word) =>
        (((int)word >> 31) << 20)
        | (int)(((word >> 12) & 0xFF) << 12)
        | (int)(((word >> 20) & 1) << 11)
        | (int)(((word >> 21) & 0x3FF) << 1);

    // The Pack functions are the inverses. They keep only the bits the format has room for; the
    // encoder checks the range first, so nothing is lost silently.

    public static uint PackRd(int rd) => ((uint)rd & 31) << 7;

    public static uint PackRs1(int rs1) => ((uint)rs1 & 31) << 15;

    public static uint PackRs2(int rs2) => ((uint)rs2 & 31) << 20;

    public static uint PackI(int imm) => ((uint)imm & 0xFFF) << 20;

    public static uint PackS(int imm) => ((((uint)imm >> 5) & 0x7F) << 25) | (((uint)imm & 31) << 7);

    public static uint PackB(int imm) =>
        ((((uint)imm >> 12) & 1) << 31)
        | ((((uint)imm >> 5) & 0x3F) << 25)
        | ((((uint)imm >> 1) & 0xF) << 8)
        | ((((uint)imm >> 11) & 1) << 7);

    public static uint PackU(int imm) => (uint)imm & 0xFFFFF000;

    public static uint PackJ(int imm) =>
        ((((uint)imm >> 20) & 1) << 31)
        | ((((uint)imm >> 1) & 0x3FF) << 21)
        | ((((uint)imm >> 11) & 1) << 20)
        | ((((uint)imm >> 12) & 0xFF) << 12);
}
