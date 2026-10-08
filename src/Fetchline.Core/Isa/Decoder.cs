namespace Fetchline.Core.Isa;

/// <summary>Turns a 32-bit word into an <see cref="Instruction"/> by looking it up in the table.</summary>
public static class Decoder
{
    // The table is searched through 256 buckets keyed by opcode bits 6..2 and funct3, which
    // between them separate every row except the handful that also differ in funct7 or bits
    // 31..20. A bucket holds at most a few rows, and each is tested with its own match and mask.
    private static readonly InstructionDef[][] Buckets = BuildBuckets();

    /// <summary>Decodes one word. A word no row matches comes back as <see cref="Op.Illegal"/>.</summary>
    public static Instruction Decode(uint word)
    {
        foreach (var def in Buckets[BucketOf(word)])
        {
            if ((word & def.Mask) == def.Match)
            {
                return Extract(def, word);
            }
        }

        return Instruction.Illegal(word);
    }

    private static int BucketOf(uint word) => (int)((((word >> 2) & 0x1F) << 3) | ((word >> 12) & 7));

    private static Instruction Extract(InstructionDef def, uint word)
    {
        var rd = (byte)Bits.Rd(word);
        var rs1 = (byte)Bits.Rs1(word);
        var rs2 = (byte)Bits.Rs2(word);

        return def.Format switch
        {
            Format.R => new Instruction(def.Op, rd, rs1, rs2, 0, word),
            Format.I => new Instruction(def.Op, rd, rs1, 0, Bits.ImmI(word), word),
            Format.Shift => new Instruction(def.Op, rd, rs1, 0, rs2, word),
            Format.S => new Instruction(def.Op, 0, rs1, rs2, Bits.ImmS(word), word),
            Format.B => new Instruction(def.Op, 0, rs1, rs2, Bits.ImmB(word), word),
            Format.U => new Instruction(def.Op, rd, 0, 0, Bits.ImmU(word), word),
            Format.J => new Instruction(def.Op, rd, 0, 0, Bits.ImmJ(word), word),
            Format.Csr or Format.CsrImm or Format.Fence =>
                new Instruction(def.Op, rd, rs1, 0, Bits.Imm12Raw(word), word),
            Format.Fixed => new Instruction(def.Op, 0, 0, 0, 0, word),
            _ => Instruction.Illegal(word),
        };
    }

    private static InstructionDef[][] BuildBuckets()
    {
        const uint keyBits = 0x0000_707F;
        var buckets = new InstructionDef[256][];
        for (var key = 0; key < buckets.Length; key++)
        {
            // The low two bits of every 32-bit instruction are 11; anything else is a 16-bit
            // encoding, which this core does not have, and lands in a bucket no row matches.
            var partial = (uint)(((key >> 3) << 2) | 3 | ((key & 7) << 12));
            buckets[key] =
            [
                .. InstructionSet.All.Where(def => ((partial ^ def.Match) & def.Mask & keyBits) == 0),
            ];
        }

        return buckets;
    }
}
